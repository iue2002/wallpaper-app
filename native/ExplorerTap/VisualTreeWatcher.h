#pragma once

#include <windows.h>

// windows.h 把 GetCurrentTime 定义成 GetTickCount，会撞掉 XAML 投影里的同名方法
#undef GetCurrentTime

#include <unknwn.h>
#include <xamlOM.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>

#include <vector>

#include "TapShared.h"

namespace wux = winrt::Windows::UI::Xaml;

// 挂在 explorer 的 XAML 诊断上，只干一件事：
// 找到任务栏那层背景 Rectangle（BackgroundFill / BackgroundStroke），换掉它的 Fill。
//
// 任务栏背景是 XAML 画的，外部进程无论怎么调 SetWindowCompositionAttribute 都改不到它，
// 这是 TranslucentTB 在 Win11 上必须注入 explorer 的原因。
class VisualTreeWatcher : public winrt::implements<VisualTreeWatcher, IVisualTreeServiceCallback2, winrt::non_agile>
{
public:
	explicit VisualTreeWatcher(winrt::com_ptr<IUnknown> site);

	// 必须从非 XAML 线程调用：AdviseVisualTreeChange 内部会把回调切回 UI 线程，
	// 在 UI 线程上直接调容易和 XAML 自己的消息循环互等。
	void StartFromBackgroundThread(HANDLE readyEvent);

	// 只在 XAML 线程执行（由 DispatcherQueue 派发进来）。
	void ApplyOnXamlThread(LONG mode, LONG color);

	winrt::Windows::System::DispatcherQueue Queue() const noexcept { return m_queue; }

	HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
		ParentChildRelation relation, VisualElement element, VisualMutationType mutationType) override;

	HRESULT STDMETHODCALLTYPE OnElementStateChanged(
		InstanceHandle handle, VisualElementState state, LPCWSTR context) noexcept override;

private:
	struct Entry
	{
		InstanceHandle Handle;
		wux::Shapes::Rectangle Rect;
		wux::Media::Brush Original;
		bool IsBorder;
	};

	template <typename T>
	T FromHandle(InstanceHandle handle);

	bool IsInsideTaskbarFrame(wux::DependencyObject element);
	void Register(InstanceHandle handle, const wchar_t* name, bool insideTaskbar);
	void ApplyOne(Entry& entry, LONG mode, LONG color);

	// 亚克力：往背景矩形上挂「模糊 + 着色」的合成子树。
	// 失败返回 false（日志里会写明卡在哪一步），调用方退回纯色，不至于全透明。
	bool TryApplyAcrylic(Entry& entry, const winrt::Windows::UI::Color& tint);

	winrt::com_ptr<IXamlDiagnostics> m_diagnostics;
	winrt::com_ptr<IVisualTreeService3> m_service;
	winrt::Windows::System::DispatcherQueue m_queue{ nullptr };
	std::vector<Entry> m_entries;
	int m_loggedRectangles = 0;
	LONG m_mode = kModeRestore;
	LONG m_color = 0;
};
