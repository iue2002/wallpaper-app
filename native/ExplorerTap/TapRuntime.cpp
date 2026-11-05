#include "TapRuntime.h"

#include <thread>

#include "TapLog.h"
#include "VisualTreeWatcher.h"

namespace
{
	HANDLE g_mapping = nullptr;
	TapSharedState* g_state = nullptr;
	HANDLE g_command = nullptr;
	HANDLE g_ready = nullptr;

	SRWLOCK g_watcherLock = SRWLOCK_INIT;
	winrt::com_ptr<VisualTreeWatcher> g_watcher;

	DWORD WINAPI IpcThreadProc(void*);
}

namespace TapRuntime
{
	bool Initialize()
	{
		g_mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, kSharedName);
		if (!g_mapping)
		{
			return false;
		}

		g_state = static_cast<TapSharedState*>(MapViewOfFile(g_mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(TapSharedState)));
		if (!g_state)
		{
			return false;
		}

		if (g_state->Size != sizeof(TapSharedState))
		{
			// 结构对不上就别动，免得写到别人的内存布局上
			return false;
		}

		g_command = OpenEventW(EVENT_MODIFY_STATE | SYNCHRONIZE, FALSE, kCommandEventName);
		g_ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, kReadyEventName);

		TapLogEnable(g_state->DebugLog == 1);
		TapLog(L"[tap] 初始化完成，命令事件=%s 就绪事件=%s",
			g_command ? L"有" : L"无", g_ready ? L"有" : L"无");

		return g_command != nullptr;
	}

	HANDLE ReadyEvent()
	{
		return g_ready;
	}

	void AttachWatcher(winrt::com_ptr<VisualTreeWatcher> watcher)
	{
		AcquireSRWLockExclusive(&g_watcherLock);
		g_watcher = std::move(watcher);
		ReleaseSRWLockExclusive(&g_watcherLock);
	}

	void ApplyAsync(LONG mode, LONG color)
	{
		winrt::com_ptr<VisualTreeWatcher> watcher;
		AcquireSRWLockShared(&g_watcherLock);
		watcher = g_watcher;
		ReleaseSRWLockShared(&g_watcherLock);

		if (!watcher)
		{
			TapLog(L"[tap] 还没有 watcher，忽略模式=%ld", mode);
			return;
		}

		const auto queue = watcher->Queue();
		if (queue)
		{
			// 队列在 XAML 线程上，投递过去再改 XAML 对象。
			// 这里的异常必须自己吞掉：跑在 explorer 的派发队列上，
			// 漏一个未处理异常就是整个资源管理器崩掉。
			queue.TryEnqueue([watcher, mode, color]
			{
				try
				{
					watcher->ApplyOnXamlThread(mode, color);
				}
				catch (const winrt::hresult_error& e)
				{
					TapLog(L"[tap] 应用失败: 0x%08X", static_cast<unsigned>(e.code().value));
				}
				catch (...)
				{
					TapLog(L"[tap] 应用未知异常");
				}
			});
			return;
		}

		TapLog(L"[tap] 没有 DispatcherQueue，直接调用（可能抛跨线程异常）");
		try
		{
			watcher->ApplyOnXamlThread(mode, color);
		}
		catch (...)
		{
			TapLog(L"[tap] 直接调用失败");
		}
	}

	void PublishStatus(LONG status, LONG applied)
	{
		if (!g_state)
		{
			return;
		}

		InterlockedExchange(&g_state->Status, status);
		if (applied >= 0)
		{
			InterlockedExchange(&g_state->Applied, applied);
		}
	}

	void StartIpcThread()
	{
		HANDLE thread = CreateThread(nullptr, 0, IpcThreadProc, nullptr, 0, nullptr);
		if (thread)
		{
			CloseHandle(thread);
		}
	}
}

namespace
{
	DWORD WINAPI IpcThreadProc(void*)
	{
		HANDLE owner = nullptr;
		if (g_state && g_state->OwnerPid != 0)
		{
			// 宿主进程一退，把任务栏还原回去，免得留下一个透明的残局
			owner = OpenProcess(SYNCHRONIZE, FALSE, static_cast<DWORD>(g_state->OwnerPid));
		}

		TapLog(L"[tap] IPC 线程启动，宿主 pid=%ld 句柄=%s",
			g_state ? g_state->OwnerPid : 0, owner ? L"有" : L"无");

		for (;;)
		{
			HANDLE handles[2] = { g_command, owner };
			const DWORD count = owner ? 2 : 1;
			const DWORD result = WaitForMultipleObjects(count, handles, FALSE, 5000);

			if (owner && result == WAIT_OBJECT_0 + 1)
			{
				TapLog(L"[tap] 宿主进程已退出，还原任务栏");
				TapRuntime::ApplyAsync(kModeRestore, 0);
				Sleep(300);
				break;
			}

			if (result == WAIT_OBJECT_0)
			{
				if (!g_state)
				{
					break;
				}

				TapLogEnable(g_state->DebugLog == 1);
				TapRuntime::ApplyAsync(g_state->Mode, g_state->Color);
				continue;
			}

			if (result == WAIT_FAILED || result == WAIT_ABANDONED)
			{
				break;
			}
		}

		if (owner)
		{
			CloseHandle(owner);
		}

		return 0;
	}
}
