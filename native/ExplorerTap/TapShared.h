#pragma once

#include <windows.h>

// 与 C# 侧 App1.Services.Taskbar.TapSharedState 必须逐字段一致（都是 4 字节，顺序相同）。
// 布局：Size/Mode/Color/OwnerPid/Status/Applied/DebugLog/Reserved = 32 字节。
struct TapSharedState
{
	LONG Size;      // sizeof(TapSharedState)，两侧校验用
	LONG Mode;      // 见下面 kMode*
	LONG Color;     // 0xAABBGGRR（和 XAML 的 Color 一致，不是 COLORREF 的 0x00BBGGRR）
	LONG OwnerPid;  // 调用方进程 PID，进程一退就自动还原
	LONG Status;    // 0=未就绪 1=已挂上 2=失败
	LONG Applied;   // 当前实际生效的任务栏背景数量（0 说明没找到 BackgroundFill）
	LONG DebugLog;  // 1=允许写日志
	// DLL 的 IPC 线程每次循环写入 GetTickCount()（系统启动以来的毫秒数，跨进程一致）。
	// 宿主读一次就能算出多久没跳，据此判断那个常驻模块是否还活着、能否复用。
	// 结构大小仍是 32 字节，和旧版 DLL 二进制兼容（旧版这里是 Reserved，恒为 0）。
	LONG Heartbeat;
};

// 命名对象都放在 Local\ 下：同一登录会话内可见，跨用户互不干扰。
constexpr wchar_t kSharedName[] = L"Local\\XiaoKWallpaper.Tap.v1";
constexpr wchar_t kCommandEventName[] = L"Local\\XiaoKWallpaper.Tap.Cmd.v1";
constexpr wchar_t kReadyEventName[] = L"Local\\XiaoKWallpaper.Tap.Ready.v1";

constexpr LONG kModeRestore = 0;  // 还原成系统默认外观
constexpr LONG kModeClear = 1;    // 纯透明（背景色 alpha 由 Color 决定）
constexpr LONG kModeAcrylic = 2;  // 亚克力

constexpr LONG kStatusIdle = 0;
constexpr LONG kStatusReady = 1;
constexpr LONG kStatusFailed = 2;
