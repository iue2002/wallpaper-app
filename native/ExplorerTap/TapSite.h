#pragma once

#include <windows.h>
#include <unknwn.h>
#include <ocidl.h>
#include <winrt/base.h>

// XAML 诊断创建这个对象后，会通过 IObjectWithSite::SetSite 把 IXamlDiagnostics 交给我们。
class TapSite : public winrt::implements<TapSite, IObjectWithSite, winrt::non_agile>
{
public:
	HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override;
	HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** ppvSite) override;

private:
	winrt::com_ptr<IUnknown> m_site;
};
