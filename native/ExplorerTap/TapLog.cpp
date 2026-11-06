#include "TapLog.h"

#include <shlobj.h>
#include <cstdarg>
#include <cstdio>
#include <cwchar>
#include <string>

namespace
{
	SRWLOCK g_lock = SRWLOCK_INIT;
	bool g_enabled = false;
	bool g_initialized = false;
	std::wstring g_path;
}

static void EnsurePath()
{
	if (g_initialized)
	{
		return;
	}

	g_initialized = true;

	PWSTR localAppData = nullptr;
	if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &localAppData)))
	{
		return;
	}

	g_path = localAppData;
	CoTaskMemFree(localAppData);

	g_path += L"\\小K壁纸";
	CreateDirectoryW(g_path.c_str(), nullptr);
	g_path += L"\\tap.log";
}

static unsigned long long FileSize(const std::wstring& path)
{
	WIN32_FILE_ATTRIBUTE_DATA data{};
	if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data))
	{
		return 0;
	}

	return (static_cast<unsigned long long>(data.nFileSizeHigh) << 32) | data.nFileSizeLow;
}

void TapLogEnable(bool enable)
{
	AcquireSRWLockExclusive(&g_lock);
	if (enable && !g_enabled)
	{
		g_enabled = true;
		EnsurePath();

		// 超过 512KB 才滚一次。
		//
		// 原来每次注入都先 DeleteFile —— 一次注入要试 60 个连接名，
		// 日志被删掉之后，"试到第几次才成功 / 每次返回什么码"这些唯一能
		// 拿来排障的东西全没了，只能靠猜。
		if (!g_path.empty() && FileSize(g_path) > 512ull * 1024)
		{
			DeleteFileW(g_path.c_str());
		}
	}
	else if (!enable)
	{
		g_enabled = false;
	}
	ReleaseSRWLockExclusive(&g_lock);
}

void TapLog(const wchar_t* format, ...)
{
	AcquireSRWLockExclusive(&g_lock);
	if (!g_enabled || g_path.empty())
	{
		ReleaseSRWLockExclusive(&g_lock);
		return;
	}

	FILE* file = nullptr;
	if (_wfopen_s(&file, g_path.c_str(), L"a, ccs=UTF-8") != 0 || file == nullptr)
	{
		ReleaseSRWLockExclusive(&g_lock);
		return;
	}

	SYSTEMTIME st{};
	GetLocalTime(&st);
	fwprintf(file, L"[%02d:%02d:%02d.%03d] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

	va_list args;
	va_start(args, format);
	vfwprintf(file, format, args);
	va_end(args);

	fwprintf(file, L"\n");
	fclose(file);

	ReleaseSRWLockExclusive(&g_lock);
}
