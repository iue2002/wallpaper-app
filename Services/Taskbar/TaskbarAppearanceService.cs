using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using App1.Helpers;

namespace App1.Services.Taskbar
{
    /// <summary>
    /// 任务栏透明/亚克力。
    ///
    /// Windows 11 的任务栏是 XAML 画的（explorer 加载了 Taskbar.dll），背景那层
    /// Rectangle 只有进程内才够得着，外部的 SetWindowCompositionAttribute 改不动它。
    /// 所以这里的做法和 TranslucentTB 一样：把 native\ExplorerTap 编译出的 DLL
    /// 注入 explorer，让它在 XAML 诊断回调里换掉任务栏背景的 Fill。
    ///
    /// 跨进程只靠一块共享内存 + 一个事件，不做 COM，也不需要注册表。
    /// 宿主进程退出时 DLL 会自己把任务栏还原，不会留下透明的残局。
    /// </summary>
    public sealed class TaskbarAppearanceService : IDisposable
    {
        private const string DllFileName = "ExplorerTap.dll";
        private const int WatchdogIntervalMs = 3000;

        /// <summary>
        /// 注入超时。DLL 那边按 60 个连接名 × 250ms 逐个试，约 15 秒，
        /// 这里留 20 秒，保证失败原因（Status=Failed）能被读回来，
        /// 而不是只剩一句"超时"。
        /// </summary>
        private const int InjectionTimeoutMs = 20000;

        /// <summary>
        /// 看门狗连续注入失败多少次后放弃。
        ///
        /// 这是有实际事故依据的：XAML 诊断不可用的机器上（Win11 24H2+ 一部分系统
        /// 没有 XamlDiagnostics.dll，InitializeXamlDiagnosticsEx 恒返回 0x80070490），
        /// 原来的逻辑是"先把档位记下来，看门狗每 15 秒重试"，每次重试都要往
        /// explorer 挂 15 秒的钩子 —— explorer 被反复占用，任务栏一直闪。
        /// explorer 晚启动的场景三次（约 45 秒）足够覆盖，坏机器三次就该闭嘴。
        /// </summary>
        private const int MaxConsecutiveFailures = 3;

        /// <summary>透明：完全透明（alpha=0）。</summary>
        private const int ClearColor = 0x00000000;

        /// <summary>亚克力：25% 黑，TintOpacity 由 alpha 换算。</summary>
        private const int AcrylicColor = 0x40000000;

        private readonly object _gate = new();

        private MemoryMappedFile? _mapping;
        private MemoryMappedViewAccessor? _view;
        private EventWaitHandle? _command;
        private EventWaitHandle? _ready;
        private IntPtr _module;
        private string? _modulePath;
        private string? _stagedDllPath;
        private uint _injectedPid;
        private DateTime _lastInjectAttempt = DateTime.MinValue;
        private int _consecutiveFailures;
        private Timer? _watchdog;
        private bool _disposed;

        public TaskbarEffect Current { get; private set; } = TaskbarEffect.Off;

        /// <summary>最近一次失败原因，给 UI 显示用。</summary>
        public string? LastError { get; private set; }

        /// <summary>当前真正生效的任务栏背景数量。0 表示挂上了但没找到 BackgroundFill。</summary>
        /// <remarks>
        /// 故意不拿锁：注入过程最长会握着锁等 15 秒，UI 线程查一下状态不该被拖住。
        /// 这里只读 DLL 回写的 Applied 字段，和我们写的字段不重叠。
        /// </remarks>
        public int AppliedCount
        {
            get
            {
                try
                {
                    var view = _view;
                    if (view == null)
                    {
                        return 0;
                    }

                    view.Read(0, out TapSharedState state);
                    return state.Applied;
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// 商店打包版不做进程注入：MSIX 审核对往 explorer 注入 DLL 这类行为不友好。
        /// 如果以后确认要放到商店版，把这里改成恒 true 即可。
        /// </summary>
        public static bool IsSupported => !StartupRegistrar.IsPackaged;

        public bool Apply(TaskbarEffect effect)
        {
            if (!IsSupported)
            {
                LastError = "商店版不支持任务栏透明（需要注入资源管理器），请用桌面版";
                return false;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                // 用户手动点一次，重新给看门狗计数机会
                _consecutiveFailures = 0;

                if (!EnsureResources())
                {
                    return false;
                }

                if (effect == TaskbarEffect.Off)
                {
                    PublishMode(effect);
                    Current = effect;
                    LastError = null;
                    return true;
                }

                // 记下这次尝试的时间，看门狗不会在 3 秒后立刻又来一遍
                _lastInjectAttempt = DateTime.UtcNow;

                if (!EnsureInjected())
                {
                    // 失败就失败：不再进入重试循环。原来的"先记档位、看门狗慢慢重试"
                    // 在 XAML 诊断不可用的机器上等于每 15 秒把 explorer 挂住 15 秒，
                    // 任务栏闪个不停。想再试，点菜单再来。
                    Current = TaskbarEffect.Off;
                    StopWatchdog();
                    return false;
                }

                PublishMode(effect);
                Current = effect;
                LastError = null;
                StartWatchdog();
                return true;
            }
        }

        private void PublishMode(TaskbarEffect effect)
        {
            var state = Read();
            state.Size = Marshal.SizeOf<TapSharedState>();
            state.OwnerPid = Environment.ProcessId;
            state.DebugLog = 1;

            switch (effect)
            {
                case TaskbarEffect.Clear:
                    state.Mode = TapIpc.ModeClear;
                    state.Color = ClearColor;
                    break;

                case TaskbarEffect.Acrylic:
                    state.Mode = TapIpc.ModeAcrylic;
                    state.Color = AcrylicColor;
                    break;

                default:
                    state.Mode = TapIpc.ModeRestore;
                    state.Color = 0;
                    break;
            }

            Write(state);
            _command?.Set();
        }

        private bool EnsureResources()
        {
            if (_view != null)
            {
                return true;
            }

            try
            {
                _mapping = MemoryMappedFile.CreateOrOpen(
                    TapIpc.SharedName, Marshal.SizeOf<TapSharedState>());
                _view = _mapping.CreateViewAccessor();

                // C# 先建，DLL 后开，顺序不能反
                _ready = new EventWaitHandle(false, EventResetMode.ManualReset, TapIpc.ReadyEventName);
                _command = new EventWaitHandle(false, EventResetMode.AutoReset, TapIpc.CommandEventName);

                Write(new TapSharedState
                {
                    Size = Marshal.SizeOf<TapSharedState>(),
                    OwnerPid = Environment.ProcessId,
                    DebugLog = 1,
                });

                return true;
            }
            catch (Exception ex)
            {
                LastError = "创建共享内存失败: " + ex.Message;
                App1.Services.Diagnostics.AppLog.Write("[任务栏] " + LastError);
                return false;
            }
        }

        /// <summary>
        /// 把注入模块复制到一个纯 ASCII 的短路径再加载。
        ///
        /// 两条原因，都是照 TranslucentTB 的部署方式来的（它把 ExplorerTAP.dll 放在
        /// 包的 TempState 里，从不从安装目录加载）：
        /// 1. 程序目录可能带中文和空格（本机就是 D:\...\桌面壁纸\App1 - 副本\...）。
        ///    这个路径要交给 XAML 诊断的内部逻辑再处理一遍，路径越干净越不容易出岔子。
        /// 2. 文件名带版本号，每次编译都是新路径。否则 explorer 里已经加载过的旧模块
        ///    会按路径直接复用，新的 DLL 永远进不去，只能重启资源管理器。
        /// </summary>
        private string? EnsureStagedDll()
        {
            if (_stagedDllPath != null && File.Exists(_stagedDllPath))
            {
                return _stagedDllPath;
            }

            try
            {
                var source = Path.Combine(AppContext.BaseDirectory, DllFileName);
                if (!File.Exists(source))
                {
                    LastError = $"缺少 {DllFileName}";
                    return null;
                }

                var info = new FileInfo(source);
                var tag = $"{info.Length:x}_{info.LastWriteTimeUtc.Ticks:x}";
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "XiaoKWallpaper",
                    "TAP");

                Directory.CreateDirectory(dir);

                var target = Path.Combine(dir, $"ExplorerTap.{tag}.dll");
                if (!File.Exists(target))
                {
                    File.Copy(source, target);
                }

                // 旧版本删得掉就删，删不掉（explorer 还握着）就留着，不影响使用
                foreach (var old in Directory.GetFiles(dir, "ExplorerTap.*.dll"))
                {
                    if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(old); } catch { }
                    }
                }

                App1.Services.Diagnostics.AppLog.Write("[任务栏] 注入模块已就位: " + target);
                _stagedDllPath = target;
                return target;
            }
            catch (Exception ex)
            {
                LastError = "准备注入模块失败: " + ex.Message;
                return null;
            }
        }

        private bool EnsureInjected()
        {
            var taskbar = TapIpc.FindWindowW(TapIpc.TaskbarClassName, null);
            if (taskbar == IntPtr.Zero)
            {
                LastError = "找不到任务栏窗口";
                return false;
            }

            if (!TapIpc.IsWindow(taskbar))
            {
                LastError = "任务栏窗口无效";
                return false;
            }

            TapIpc.GetWindowThreadProcessId(taskbar, out var pid);
            if (pid != 0 && pid == _injectedPid && _ready?.WaitOne(0) == true)
            {
                // 已经挂在同一个 explorer 上
                return true;
            }

            var dllPath = EnsureStagedDll();
            if (dllPath == null)
            {
                App1.Services.Diagnostics.AppLog.Write("[任务栏] " + LastError);
                return false;
            }

            // 载进本进程只是为了拿到 TapHookProc 的地址；真正的安装发生在 explorer 里
            if (_module == IntPtr.Zero || !string.Equals(_modulePath, dllPath, StringComparison.OrdinalIgnoreCase))
            {
                _module = TapIpc.LoadLibraryW(dllPath);
                if (_module == IntPtr.Zero)
                {
                    LastError = "加载 ExplorerTap.dll 失败";
                    return false;
                }

                _modulePath = dllPath;
            }

            var hookProc = TapIpc.GetProcAddress(_module, "TapHookProc");
            if (hookProc == IntPtr.Zero)
            {
                LastError = "ExplorerTap.dll 缺少注入入口";
                return false;
            }

            _ready!.Reset();

            var threadId = TapIpc.GetWindowThreadProcessId(taskbar, out _);
            var hook = TapIpc.SetWindowsHookExW(TapIpc.WhCallWndProc, hookProc, _module, threadId);
            if (hook == IntPtr.Zero)
            {
                LastError = $"注入资源管理器失败（错误码 {Marshal.GetLastWin32Error()}）";
                App1.Services.Diagnostics.AppLog.Write("[任务栏] " + LastError);
                return false;
            }

            bool ready;
            try
            {
                ready = _ready.WaitOne(InjectionTimeoutMs);
            }
            finally
            {
                // DLL 已经被 XAML 诊断钉在 explorer 里，钩子只是搬运工，马上撤掉
                TapIpc.UnhookWindowsHookEx(hook);
            }

            if (!ready)
            {
                LastError = "注入超时（XAML 诊断没有响应）";
                App1.Services.Diagnostics.AppLog.Write("[任务栏] " + LastError);
                return false;
            }

            var status = Read().Status;
            if (status == TapIpc.StatusFailed)
            {
                LastError = "XAML 诊断初始化失败，详见 tap.log";
                return false;
            }

            _injectedPid = pid;
            App1.Services.Diagnostics.AppLog.Write($"[任务栏] 已注入 explorer，pid={pid}");
            return true;
        }

        private void StartWatchdog()
        {
            _watchdog ??= new Timer(_ => WatchdogTick(), null, WatchdogIntervalMs, WatchdogIntervalMs);
        }

        private void StopWatchdog()
        {
            try
            {
                _watchdog?.Dispose();
            }
            catch
            {
            }

            _watchdog = null;
        }

        /// <summary>
        /// explorer 重启（任务栏重建）后注入会失效，这里发现 pid 变了就重新挂一次。
        /// 用轮询而不是 WinEventHook：3 秒一次 EnumWindows 级别的开销可以忽略，
        /// 也不依赖消息循环，后台常驻时更稳。
        /// </summary>
        private void WatchdogTick()
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed || Current == TaskbarEffect.Off || _view == null)
                    {
                        return;
                    }

                    var taskbar = TapIpc.FindWindowW(TapIpc.TaskbarClassName, null);
                    if (taskbar == IntPtr.Zero)
                    {
                        return;
                    }

                    TapIpc.GetWindowThreadProcessId(taskbar, out var pid);
                    var injected = pid != 0 && pid == _injectedPid && _ready?.WaitOne(0) == true;
                    if (injected)
                    {
                        return;
                    }

                    // 失败过就 15 秒再试一次，不要每 3 秒刷一条日志
                    if ((DateTime.UtcNow - _lastInjectAttempt).TotalSeconds < 15)
                    {
                        return;
                    }

                    _lastInjectAttempt = DateTime.UtcNow;
                    App1.Services.Diagnostics.AppLog.Write($"[任务栏] 需要重新注入 explorer，pid={pid}");

                    if (EnsureInjected())
                    {
                        _consecutiveFailures = 0;
                        PublishMode(Current);
                    }
                    else
                    {
                        _consecutiveFailures++;

                        if (_consecutiveFailures >= MaxConsecutiveFailures)
                        {
                            // 连续失败到上限：这台机器的 XAML 诊断多半根本起不来，
                            // 再试只是每 15 秒把 explorer 挂住一次。停下，写清原因。
                            App1.Services.Diagnostics.AppLog.Write(
                                $"[任务栏] 连续 {_consecutiveFailures} 次注入失败（{LastError}），暂停自动重试");

                            Current = TaskbarEffect.Off;
                            StopWatchdog();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App1.Services.Diagnostics.AppLog.Write("[任务栏] 看门狗异常: " + ex.Message);
            }
        }

        private TapSharedState Read()
        {
            _view!.Read(0, out TapSharedState state);
            return state;
        }

        private void Write(TapSharedState state)
        {
            _view!.Write(0, ref state);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                try
                {
                    _watchdog?.Dispose();
                }
                catch
                {
                }

                _watchdog = null;

                try
                {
                    if (_view != null)
                    {
                        var restore = Read();
                        restore.Mode = TapIpc.ModeRestore;
                        Write(restore);
                        _command?.Set();

                        // 给 DLL 一点时间把 Fill 换回去，进程一退就没人管了
                        Thread.Sleep(150);
                    }
                }
                catch
                {
                }

                try { _command?.Dispose(); } catch { }
                try { _ready?.Dispose(); } catch { }
                try { _view?.Dispose(); } catch { }
                try { _mapping?.Dispose(); } catch { }

                _command = null;
                _ready = null;
                _view = null;
                _mapping = null;
            }
        }
    }
}
