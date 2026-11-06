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

		wchar_t selfPath[MAX_PATH]{};
		if (!GetModuleFileNameW(g_module, selfPath, MAX_PATH))
		{
			TapLog(L"[tap] 取自身路径失败");
			return 2;
		}

		const std::wstring dllPath = selfPath;

		// 找 XamlDiagnostics.dll。
		//
		// InitializeXamlDiagnosticsEx 的第三个参数就是它的路径，传 nullptr 时
		// Windows.UI.Xaml.dll 会自己去 C:\Windows 下找；Win11 26200 的 System32 里
		// 没有这个文件，于是直接返回 0x80070490(ERROR_NOT_FOUND)，注入永远起不来。
		// 这个 DLL 由 Windows SDK 分发（Windows Kits\10\bin\x64\XamlDiagnostics），
		// 依赖只有 msvcrt/CoreMessaging/OLEAUT32，可以安全地跟着程序走。
		std::wstring xamlDiagnostics;
		{
			if (wchar_t* slash = wcsrchr(selfPath, L'\\'))
			{
				*(slash + 1) = L'\0';
				std::wstring candidate = std::wstring(selfPath) + L"xamldiagnostics.dll";
				if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES)
				{
					xamlDiagnostics = candidate;
				}
			}

			if (xamlDiagnostics.empty())
			{
				wchar_t systemDir[MAX_PATH]{};
				if (GetSystemDirectoryW(systemDir, MAX_PATH))
				{
					std::wstring candidate = std::wstring(systemDir) + L"\\XamlDiagnostics.dll";
					if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES)
					{
						xamlDiagnostics = candidate;
					}
				}
			}

			if (xamlDiagnostics.empty())
			{
				TapLog(L"[tap] 没找到 XamlDiagnostics.dll，交给系统兜底（多半还是 0x80070490）");
			}
			else
			{
				// 先自己加载一次：真加载不进来（位数不对、依赖缺失）在日志里立刻能看出来，
				// 免得只看到一个笼统的错误码。
				const HMODULE preload = LoadLibraryExW(xamlDiagnostics.c_str(), nullptr,
					LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
				TapLog(L"[tap] XamlDiagnostics=%s 预加载=%s",
					xamlDiagnostics.c_str(), preload ? L"成功" : L"失败");
			}
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

		TapLog(L"[tap] 注入开始 pid=%lu tap=%s", GetCurrentProcessId(), dllPath.c_str());

		HRESULT hr = E_FAIL;
		// 连接名必须是 "VisualDiagConnectionN"。
		//
		// 这个参数不是随便起的标签：InitializeXamlDiagnosticsEx 拿它去 explorer 里
		// 查一个「已存在的 XAML 端点」，查不到就返回 0x80070490(Element not found)。
		// 之前这里用的是自造的 "XiaoKTapConnection"，对应的端点根本不存在，
		// 所以 60 次尝试注定全失败 —— 注入本身是成功的，名字是错的。
		// TranslucentTB、UWPSpy、Windhawk 的 XAML 诊断实现用的都是这一串。
		//
		// 端点表按进程维护、旧的只关不删，所以从 1 往上试到空位为止。
		// 60 次 × 250ms ≈ 15 秒，落在宿主 20 秒超时之内。
		for (int attempt = 1; attempt <= 60; ++attempt)
		{
			const std::wstring connection = L"VisualDiagConnection" + std::to_wstring(attempt);

			// 每次尝试都换一个线程：XAML 诊断一个线程只能初始化一次，
			// 失败重试必须在新线程上进行。
			std::thread([&hr, initialize, connection, dllPath, xamlDiagnostics]
			{
				hr = initialize(connection.c_str(), GetCurrentProcessId(),
					xamlDiagnostics.empty() ? nullptr : xamlDiagnostics.c_str(),
					dllPath.c_str(), CLSID_XiaoKExplorerTap, nullptr);
			}).join();

			if (SUCCEEDED(hr))
			{
				TapLog(L"[tap] InitializeXamlDiagnosticsEx 成功，第 %d 次，连接=%s",
					attempt, connection.c_str());
				break;
			}

			// 逐次全记：改名之后"第几次开始成功"就是端点表状态的直接证据，
			// 只记前几次会把关键信息裁掉。
			TapLog(L"[tap] 第 %d 次失败: 0x%08X 连接=%s 系统错误=%lu", attempt,
				static_cast<unsigned>(hr), connection.c_str(), GetLastError());

			Sleep(250);
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
