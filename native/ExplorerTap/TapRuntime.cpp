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
			TapLog(L"[tap] OpenFileMapping 失败，错误码=%lu，名字=%s", GetLastError(), kSharedName);
			return false;
		}

		g_state = static_cast<TapSharedState*>(MapViewOfFile(g_mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(TapSharedState)));
		if (!g_state)
		{
			TapLog(L"[tap] MapViewOfFile 失败，错误码=%lu", GetLastError());
			return false;
		}

		if (g_state->Size != sizeof(TapSharedState))
		{
			// 结构对不上就别动，免得写到别人的内存布局上。
			// 典型成因：explorer 里还钉着旧版本的 TAP，共享区被旧结构初始化过。
			TapLog(L"[tap] 共享结构不匹配：对端 Size=%d 本模块 sizeof=%zu，放弃安装",
				g_state->Size, sizeof(TapSharedState));
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
		DWORD boundPid = 0;

		// 宿主可能换过进程（托盘退出后重新打开），每次收到命令都要重新核对 OwnerPid。
		auto BindOwner = [&]()
		{
			const DWORD pid = g_state ? static_cast<DWORD>(g_state->OwnerPid) : 0;
			if (pid == 0 || pid == boundPid)
			{
				return;
			}

			if (owner)
			{
				CloseHandle(owner);
				owner = nullptr;
			}

			owner = OpenProcess(SYNCHRONIZE, FALSE, pid);
			boundPid = pid;
			TapLog(L"[tap] 绑定宿主 pid=%lu 句柄=%s", pid, owner ? L"有" : L"无");
		};

		BindOwner();
		TapLog(L"[tap] IPC 线程启动，宿主 pid=%lu", boundPid);

		for (;;)
		{
			HANDLE handles[2] = { g_command, owner };
			const DWORD count = owner ? 2 : 1;
			const DWORD result = WaitForMultipleObjects(count, handles, FALSE, 1000);

			// 心跳写系统启动以来的毫秒数（GetTickCount，跨进程一致）。
			// 宿主读一次就能算出「多久没跳了」，不用等两轮采样，也就不会卡住启动。
			if (g_state)
			{
				InterlockedExchange(&g_state->Heartbeat, static_cast<LONG>(GetTickCount()));
			}

			if (owner && result == WAIT_OBJECT_0 + 1)
			{
				// 关键：这里绝对不能 break。
				//
				// DLL 一旦被 XAML 诊断加载就永久钉在 explorer 里（Windows 不会自动卸载）。
				// 旧版在这里退出 IPC 线程，结果模块还在、却没人再听命令事件；
				// 下一次启动挂同一个路径的 hook，Windows 也不会重跑 DllMain，
				// 于是只能干等就绪事件超时 —— 必须重启资源管理器才能恢复。
				//
				// 正确做法是「还原外观 + 解绑宿主 + 继续待命」，等下一个宿主重连。
				TapLog(L"[tap] 宿主进程已退出，还原任务栏并转入待命");
				TapRuntime::ApplyAsync(kModeRestore, 0);
				if (g_state)
				{
					InterlockedExchange(&g_state->OwnerPid, 0);
				}

				CloseHandle(owner);
				owner = nullptr;
				boundPid = 0;
				continue;
			}

			if (result == WAIT_OBJECT_0)
			{
				if (!g_state)
				{
					break;
				}

				BindOwner();
				TapLogEnable(g_state->DebugLog == 1);
				TapRuntime::ApplyAsync(g_state->Mode, g_state->Color);
				continue;
			}

			if (result == WAIT_FAILED || result == WAIT_ABANDONED)
			{
				break;
			}

			// WAIT_TIMEOUT：什么都不做，下一轮继续刷心跳
		}

		if (owner)
		{
			CloseHandle(owner);
		}

		return 0;
	}
}
