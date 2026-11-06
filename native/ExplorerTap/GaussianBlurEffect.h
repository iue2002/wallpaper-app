#pragma once

#include <windows.h>
#include <d2d1_1.h>
#include <d2d1effects.h>
#include <wrl/client.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <windows.graphics.effects.interop.h>

namespace wge = winrt::Windows::Graphics::Effects;
namespace awge = ABI::Windows::Graphics::Effects;

// 自己实现的高斯模糊效果。
//
// WinRT 只投影了 SceneLightingEffect，没有高斯模糊；而 CompositionEffectFactory
// 拿到 IGraphicsEffect 之后会 QI 一个 IGraphicsEffectD2D1Interop，靠它去建 D2D 效果图，
// 所以这里把 D2D 的 CLSID_D2D1GaussianBlur 包一层，暴露一个 BlurAmount 属性。
// 任务栏的「亚克力」就靠它 + 一层半透明着色叠出来 —— AcrylicBrush(Backdrop)
// 在 explorer 的 XAML 岛里不出背景模糊，只能自己合成。
struct GaussianBlurEffect : winrt::implements<GaussianBlurEffect,
	wge::IGraphicsEffect, wge::IGraphicsEffectSource, awge::IGraphicsEffectD2D1Interop>
{
	// IGraphicsEffect
	winrt::hstring Name();
	void Name(winrt::hstring const& value);

	// IGraphicsEffectD2D1Interop
	HRESULT __stdcall GetEffectId(GUID* id) noexcept override;
	HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR name, UINT* index, awge::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept override;
	HRESULT __stdcall GetPropertyCount(UINT* count) noexcept override;
	HRESULT __stdcall GetProperty(UINT index, ABI::Windows::Foundation::IPropertyValue** value) noexcept override;
	HRESULT __stdcall GetSource(UINT index, awge::IGraphicsEffectSource** source) noexcept override;
	HRESULT __stdcall GetSourceCount(UINT* count) noexcept override;

	// D2D1GaussianBlur 一共三个属性，必须全部报出来：
	// compositor 建工厂时会按顺序挨个问 0/1/2，少一个就整体 E_INVALIDARG。
	float BlurAmount = 12.0f;
	uint32_t Optimization = 1;   // D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED
	uint32_t BorderMode = 1;     // D2D1_BORDER_MODE_SOFT
	wge::IGraphicsEffectSource Source{ nullptr };

private:
	winrt::hstring m_name = L"XiaoKGaussianBlur";
};
