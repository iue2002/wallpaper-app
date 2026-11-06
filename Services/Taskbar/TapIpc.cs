using System;
using System.Runtime.InteropServices;

namespace App1.Services.Taskbar
{
    /// <summary>
    /// 任务栏外观档位。数值同时用来存设置文件，不要随意改。
    /// </summary>
    public enum TaskbarEffect
    {
        Off = 0,
        Clear = 1,    // 纯透明
        Acrylic = 2,  // 亚克力
    }

    /// <summary>
    /// 与 native\ExplorerTap\TapShared.h 里的 TapSharedState 逐字段对应（8 个 int，32 字节）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TapSharedState
    {
        public int Size;
        public int Mode;
        public int Color;      // 0xAABBGGRR，和 XAML 的 Color 一致
        public int OwnerPid;
        public int Status;     // 0=未就绪 1=已挂上 2=失败
        public int Applied;    // 实际生效的背景数量
        public int DebugLog;   // 1=让 DLL 写日志
        /// <summary>DLL 写入的 GetTickCount()；宿主靠时间差判断常驻模块是否还活着。</summary>
        public int Heartbeat;
    }

    /// <summary>
    /// 和注入 DLL 之间的命名对象 + 用到的 Win32。
    /// </summary>
    internal static class TapIpc
    {
        // 必须与 TapShared.h 一字不差
        public const string SharedName = @"Local\XiaoKWallpaper.Tap.v1";
        public const string CommandEventName = @"Local\XiaoKWallpaper.Tap.Cmd.v1";
        public const string ReadyEventName = @"Local\XiaoKWallpaper.Tap.Ready.v1";

        public const int ModeRestore = 0;
        public const int ModeClear = 1;
        public const int ModeAcrylic = 2;

        public const int StatusIdle = 0;
        public const int StatusReady = 1;
        public const int StatusFailed = 2;

        public const int WhCallWndProc = 4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowW(string? className, string? windowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookExW(int idHook, IntPtr lpfn, IntPtr hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        public const string TaskbarClassName = "Shell_TrayWnd";
        public const string SecondaryTaskbarClassName = "Shell_SecondaryTrayWnd";
    }
}
