#include "TapSite.h"

#include "TapGuids.h"
#include "TapLog.h"
#include "TapRuntime.h"
#include "VisualTreeWatcher.h"

HRESULT TapSite::SetSite(IUnknown* site)
{
	try
	{
		m_site.copy_from(site);

		if (!site)
		{
			return S_OK;
		}

		auto watcher = winrt::make_self<VisualTreeWatcher>(m_site);
		TapRuntime::AttachWatcher(watcher);

		// 从后台线程去挂监听：在 XAML 线程上直接调容易和自己互等
		watcher->StartFromBackgroundThread(TapRuntime::ReadyEvent());
		return S_OK;
	}
	catch (const winrt::hresult_error& e)
	{
		TapLog(L"[site] SetSite 失败: 0x%08X", static_cast<unsigned>(e.code().value));
		return e.code();
	}
	catch (...)
	{
		return E_FAIL;
	}
}

HRESULT TapSite::GetSite(REFIID riid, void** ppvSite)
{
	if (!ppvSite)
	{
		return E_POINTER;
	}

	*ppvSite = nullptr;
	if (!m_site)
	{
		return E_FAIL;
	}

	return m_site->QueryInterface(riid, ppvSite);
}

namespace
{
	class TapFactory : public winrt::implements<TapFactory, IClassFactory>
	{
	public:
		HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
		{
			if (!ppv)
			{
				return E_POINTER;
			}

			*ppv = nullptr;
			if (outer)
			{
				return CLASS_E_NOAGGREGATION;
			}

			try
			{
				return winrt::make<TapSite>().as(riid, ppv);
			}
			catch (...)
			{
				return winrt::to_hresult();
			}
		}

		HRESULT STDMETHODCALLTYPE LockServer(BOOL) override
		{
			// 一旦被 XAML 诊断钉住就不打算卸载，锁不锁无所谓
			return S_OK;
		}
	};
}

extern "C" HRESULT STDMETHODCALLTYPE DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
	if (!ppv)
	{
		return E_POINTER;
	}

	*ppv = nullptr;

	if (!IsEqualCLSID(rclsid, CLSID_XiaoKExplorerTap))
	{
		return CLASS_E_CLASSNOTAVAILABLE;
	}

	try
	{
		return winrt::make<TapFactory>().as(riid, ppv);
	}
	catch (...)
	{
		return winrt::to_hresult();
	}
}

extern "C" HRESULT STDMETHODCALLTYPE DllCanUnloadNow()
{
	return S_FALSE;
}
