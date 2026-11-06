#include "GaussianBlurEffect.h"

#include <cstdio>
#ifdef XIAOK_TRACE
#define TRACE(...) printf(__VA_ARGS__)
#else
#define TRACE(...)
#endif


#include <winrt/Windows.Foundation.h>

using namespace winrt;

namespace
{
	// d2d1effects.h 里声明了这个 CLSID，但 d2d1.lib 不导出定义，自己写一份。
	// 值与 SDK 头文件里的 DEFINE_GUID(CLSID_D2D1GaussianBlur, ...) 完全一致。
	const GUID kGaussianBlurClsid =
	{ 0x1feb6d69, 0x2fe6, 0x4ac9, { 0x8c, 0x58, 0x1d, 0x7f, 0x93, 0xe7, 0xa6, 0xa5 } };
}

winrt::hstring GaussianBlurEffect::Name()
{
	return m_name;
}

void GaussianBlurEffect::Name(winrt::hstring const& value)
{
	m_name = value;
}

HRESULT GaussianBlurEffect::GetEffectId(GUID* id) noexcept
{
	TRACE("[fx] GetEffectId\n");
	if (!id)
	{
		return E_POINTER;
	}

	*id = kGaussianBlurClsid;
	return S_OK;
}

HRESULT GaussianBlurEffect::GetNamedPropertyMapping(LPCWSTR name, UINT* index,
	awge::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept
{
	TRACE("[fx] GetNamedPropertyMapping %ls\n", name ? name : L"(null)");
	if (!name || !index || !mapping)
	{
		return E_POINTER;
	}

	if (wcscmp(name, L"BlurAmount") == 0)
	{
		*index = 0;   // D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION
	}
	else if (wcscmp(name, L"Optimization") == 0)
	{
		*index = 1;   // D2D1_GAUSSIANBLUR_PROP_OPTIMIZATION
	}
	else if (wcscmp(name, L"BorderMode") == 0)
	{
		*index = 2;   // D2D1_GAUSSIANBLUR_PROP_BORDER_MODE
	}
	else
	{
		return E_INVALIDARG;
	}

	*mapping = awge::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
	return S_OK;
}

HRESULT GaussianBlurEffect::GetPropertyCount(UINT* count) noexcept
{
	TRACE("[fx] GetPropertyCount\n");
	if (!count)
	{
		return E_POINTER;
	}

	*count = 3;
	return S_OK;
}

HRESULT GaussianBlurEffect::GetProperty(UINT index, ABI::Windows::Foundation::IPropertyValue** value) noexcept
{
	TRACE("[fx] GetProperty %u\n", index);
	if (!value)
	{
		return E_POINTER;
	}

	*value = nullptr;

	switch (index)
	{
	case 0:
		*value = winrt::Windows::Foundation::PropertyValue::CreateSingle(BlurAmount)
			.as<ABI::Windows::Foundation::IPropertyValue>().detach();
		return S_OK;

	case 1:
		*value = winrt::Windows::Foundation::PropertyValue::CreateUInt32(Optimization)
			.as<ABI::Windows::Foundation::IPropertyValue>().detach();
		return S_OK;

	case 2:
		*value = winrt::Windows::Foundation::PropertyValue::CreateUInt32(BorderMode)
			.as<ABI::Windows::Foundation::IPropertyValue>().detach();
		return S_OK;

	default:
		return E_INVALIDARG;
	}
}

HRESULT GaussianBlurEffect::GetSource(UINT index, awge::IGraphicsEffectSource** source) noexcept
{
	TRACE("[fx] GetSource %u\n", index);
	if (!source)
	{
		return E_POINTER;
	}

	*source = nullptr;
	if (index != 0)
	{
		return E_INVALIDARG;
	}

	winrt::copy_to_abi(Source, *reinterpret_cast<void**>(source));
	return S_OK;
}

HRESULT GaussianBlurEffect::GetSourceCount(UINT* count) noexcept
{
	TRACE("[fx] GetSourceCount\n");
	if (!count)
	{
		return E_POINTER;
	}

	*count = 1;
	return S_OK;
}
