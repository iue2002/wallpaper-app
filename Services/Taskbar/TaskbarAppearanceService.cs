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
        private const int InjectionTimeoutMs = 15000;

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
        private uint _injectedPid;
        private DateTime _lastInjectAttempt = DateTime.MinValue;
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

                // 先把目标档位记下来：即使这次注入失败，看门狗也会继续重试，
                // 开机自启那种任务栏还没准备好的场景不用用户再点一次。
                Current = effect;

                if (!EnsureResources())
                {
                    return false;
                }

                if (effect == TaskbarEffect.Off)
                {
                    PublishMode(effect);
                    LastError = null;
                    return true;
                }

                StartWatchdog();

                if (!EnsureInjected())
                {
                    return false;
                }

                PublishMode(effect);
                LastError = null;
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

            var dllPath = Path.Combine(AppContext.BaseDirectory, DllFileName);
            if (!File.Exists(dllPath))
            {
                LastError = $"缺少 {DllFileName}";
                App1.Services.Diagnostics.AppLog.Write("[任务栏] " + LastError + "：" + dllPath);
                return false;
            }

            // 载进本进程只是为了拿到 TapHookProc 的地址；真正的安装发生在 explorer 里
            if (_module == IntPtr.Zero)
            {
                _module = TapIpc.LoadLibraryW(dllPath);
                if (_module == IntPtr.Zero)
                {
                    LastError = "加载 ExplorerTap.dll 失败";
                    return false;
                }
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
                        PublishMode(Current);
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
