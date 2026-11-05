#pragma once

#include <windows.h>
#include <unknwn.h>
#include <winrt/base.h>

#include "TapShared.h"

class VisualTreeWatcher;

// 进程内的小中枢：共享内存、命令事件、watcher 引用、跨线程派发。
// 注入进 explorer 之后没有 UI，全部状态都靠这一层和外部进程对接。
namespace TapRuntime
{
	// 打开外部（小K壁纸）已经建好的共享内存与事件。失败说明调用方没准备好，直接放弃。
	bool Initialize();

	HANDLE ReadyEvent();

	void AttachWatcher(winrt::com_ptr<VisualTreeWatcher> watcher);

	// 任意线程调用，内部切到 XAML 线程执行。
	void ApplyAsync(LONG mode, LONG color);

	void PublishStatus(LONG status, LONG applied);

	void StartIpcThread();
}
