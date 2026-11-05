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

void TapLogEnable(bool enable)
{
	AcquireSRWLockExclusive(&g_lock);
	if (enable && !g_enabled)
	{
		g_enabled = true;
		EnsurePath();
		// 每次开启都重开一份，避免日志无限增长
		if (!g_path.empty())
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
