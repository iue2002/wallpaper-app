#include "GaussianBlurEffect.h"

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
	if (!name || !index || !mapping)
	{
		return E_POINTER;
	}

	if (wcscmp(name, L"BlurAmount") == 0)
	{
		*index = 0;
		*mapping = awge::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
		return S_OK;
	}

	return E_INVALIDARG;
}

HRESULT GaussianBlurEffect::GetPropertyCount(UINT* count) noexcept
{
	if (!count)
	{
		return E_POINTER;
	}

	*count = 1;
	return S_OK;
}

HRESULT GaussianBlurEffect::GetProperty(UINT index, ABI::Windows::Foundation::IPropertyValue** value) noexcept
{
	if (!value)
	{
		return E_POINTER;
	}

	*value = nullptr;
	if (index != 0)
	{
		return E_INVALIDARG;
	}

	const auto boxed = winrt::Windows::Foundation::PropertyValue::CreateSingle(BlurAmount);
	*value = boxed.as<ABI::Windows::Foundation::IPropertyValue>().detach();
	return S_OK;
}

HRESULT GaussianBlurEffect::GetSource(UINT index, awge::IGraphicsEffectSource** source) noexcept
{
	if (!source)
	{
		return E_POINTER;
	}

	*source = nullptr;
	if (index != 0 || !Source)
	{
		return E_INVALIDARG;
	}

	Source.as<awge::IGraphicsEffectSource>().copy_to(source);
	return S_OK;
}

HRESULT GaussianBlurEffect::GetSourceCount(UINT* count) noexcept
{
	if (!count)
	{
		return E_POINTER;
	}

	*count = Source ? 1 : 0;
	return S_OK;
}
