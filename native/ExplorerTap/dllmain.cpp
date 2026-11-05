#include <windows.h>

#include <string>
#include <thread>

#include "TapGuids.h"
#include "TapLog.h"
#include "TapRuntime.h"
#include "TapShared.h"

namespace
{
	HMODULE g_module = nullptr;

	using PFN_InitializeXamlDiagnosticsEx =
		HRESULT(WINAPI*)(LPCWSTR endPointName, DWORD pid, LPCWSTR dllXamlDiagnostics,
			LPCWSTR tapDllName, CLSID tapClsid, LPCWSTR initializationData);

	bool IsExplorerProcess()
	{
		wchar_t path[MAX_PATH]{};
		if (!GetModuleFileNameW(nullptr, path, MAX_PATH))
		{
			return false;
		}

		const wchar_t* name = wcsrchr(path, L'\\');
		name = name ? name + 1 : path;

		return _wcsicmp(name, L"explorer.exe") == 0;
	}

	// XAML 诊断只在一个进程里初始化一次，而且初始化过程会等 explorer 的 XAML 起来，
	// 所以放在独立线程里做，DllMain 里不能干这种活。
	DWORD WINAPI InstallThread(void*)
	{
		if (!TapRuntime::Initialize())
		{
			TapLog(L"[tap] 打开共享内存失败，放弃安装");
			return 1;
		}

		wchar_t dllPath[MAX_PATH]{};
		if (!GetModuleFileNameW(g_module, dllPath, MAX_PATH))
		{
			TapLog(L"[tap] 取自身路径失败");
			return 2;
		}

		const HMODULE xaml = LoadLibraryExW(L"Windows.UI.Xaml.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
		if (!xaml)
		{
			TapLog(L"[tap] 加载 Windows.UI.Xaml.dll 失败: %lu", GetLastError());
			TapRuntime::PublishStatus(kStatusFailed, -1);
			return 3;
		}

		const auto initialize = reinterpret_cast<PFN_InitializeXamlDiagnosticsEx>(
			GetProcAddress(xaml, "InitializeXamlDiagnosticsEx"));

		if (!initialize)
		{
			TapLog(L"[tap] 找不到 InitializeXamlDiagnosticsEx");
			TapRuntime::PublishStatus(kStatusFailed, -1);
			return 4;
		}

		HRESULT hr = E_FAIL;
		for (int attempt = 1; attempt <= 60; ++attempt)
		{
			const std::wstring connection = L"XiaoKTapConnection" + std::to_wstring(attempt);

			// 每次尝试都换一个线程：XAML 诊断一个线程只能初始化一次，
			// 失败重试必须在新线程上进行。
			std::thread([&hr, initialize, connection, dllPath]
			{
				hr = initialize(connection.c_str(), GetCurrentProcessId(), nullptr,
					dllPath, CLSID_XiaoKExplorerTap, nullptr);
			}).join();

			if (SUCCEEDED(hr))
			{
				TapLog(L"[tap] InitializeXamlDiagnosticsEx 成功，第 %d 次", attempt);
				break;
			}

			if (attempt == 1 || attempt % 10 == 0)
			{
				TapLog(L"[tap] 第 %d 次初始化失败: 0x%08X", attempt, static_cast<unsigned>(hr));
			}

			Sleep(500);
		}

		TapRuntime::PublishStatus(SUCCEEDED(hr) ? kStatusReady : kStatusFailed, -1);
		TapRuntime::StartIpcThread();

		if (FAILED(hr))
		{
			// 失败也要把就绪事件置上，否则外部只能干等到超时
			if (const HANDLE ready = TapRuntime::ReadyEvent())
			{
				SetEvent(ready);
			}
		}

		return 0;
	}
}

// 注入用的钩子过程。SetWindowsHookEx 需要一个位于本模块内的函数地址，
// 具体逻辑不需要，钩子只负责把 DLL 带进 explorer，挂上之后外部会立刻撤销钩子。
extern "C" __declspec(dllexport) LRESULT CALLBACK TapHookProc(int code, WPARAM wParam, LPARAM lParam)
{
	return CallNextHookEx(nullptr, code, wParam, lParam);
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
	switch (reason)
	{
	case DLL_PROCESS_ATTACH:
	{
		g_module = module;
		DisableThreadLibraryCalls(module);

		// 只有被带进 explorer 才装；被小K壁纸自己 LoadLibrary 进来时什么都不做
		if (IsExplorerProcess())
		{
			const HANDLE thread = CreateThread(nullptr, 0, InstallThread, nullptr, 0, nullptr);
			if (thread)
			{
				CloseHandle(thread);
			}
		}

		break;
	}

	case DLL_PROCESS_DETACH:
		break;
	}

	return TRUE;
}
