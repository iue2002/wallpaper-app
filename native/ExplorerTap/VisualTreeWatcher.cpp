#include "VisualTreeWatcher.h"

#include <oleauto.h>
#include <string>
#include <thread>

#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>

#include "GaussianBlurEffect.h"
#include "TapLog.h"
#include "TapRuntime.h"

using namespace winrt;

namespace wuc = winrt::Windows::UI::Composition;
namespace wuxh = winrt::Windows::UI::Xaml::Hosting;

namespace
{
	// 任务栏背景那一层的父级名字。Windows 11 的任务栏 XAML 结构大致是
	// DesktopWindowXamlSource -> RootGrid -> TaskbarFrame -> ... -> BackgroundFill
	constexpr wchar_t kTaskbarFrameName[] = L"TaskbarFrame";
	constexpr wchar_t kTaskbarFrameType[] = L"Taskbar.TaskbarFrame";
	constexpr wchar_t kBackgroundFillName[] = L"BackgroundFill";
	constexpr wchar_t kBackgroundStrokeName[] = L"BackgroundStroke";

	wchar_t AsciiLower(wchar_t c)
	{
		return (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c - L'A' + L'a') : c;
	}

	bool EqualsIgnoreCase(std::wstring_view left, std::wstring_view right)
	{
		if (left.size() != right.size())
		{
			return false;
		}

		for (size_t i = 0; i < left.size(); ++i)
		{
			if (AsciiLower(left[i]) != AsciiLower(right[i]))
			{
				return false;
			}
		}

		return true;
	}
}

VisualTreeWatcher::VisualTreeWatcher(winrt::com_ptr<IUnknown> site) :
	m_diagnostics(site.as<IXamlDiagnostics>()),
	m_service(site.as<IVisualTreeService3>())
{
	try
	{
		// 记下 XAML 线程的派发队列，后面 IPC 线程要往这里投递
		m_queue = winrt::Windows::System::DispatcherQueue::GetForCurrentThread();
	}
	catch (...)
	{
		TapLog(L"[watcher] 取 DispatcherQueue 失败");
	}

	TapLog(L"[watcher] 已创建，DispatcherQueue=%s", m_queue ? L"有" : L"无");
}

void VisualTreeWatcher::StartFromBackgroundThread(HANDLE readyEvent)
{
	auto self = get_strong();

	std::thread([self, readyEvent]
	{
		try
		{
			check_hresult(self->m_service->AdviseVisualTreeChange(self.get()));
			TapLog(L"[watcher] AdviseVisualTreeChange 成功");
		}
		catch (const hresult_error& e)
		{
			TapLog(L"[watcher] AdviseVisualTreeChange 失败: 0x%08X %s",
				static_cast<unsigned>(e.code().value), e.message().c_str());
		}
		catch (...)
		{
			TapLog(L"[watcher] AdviseVisualTreeChange 未知异常");
		}

		if (readyEvent)
		{
			SetEvent(readyEvent);
		}
	}).detach();
}

template <typename T>
T VisualTreeWatcher::FromHandle(InstanceHandle handle)
{
	winrt::com_ptr<::IInspectable> inspectable;
	check_hresult(m_diagnostics->GetIInspectableFromHandle(handle, inspectable.put()));
	return inspectable.as<T>();
}

bool VisualTreeWatcher::IsInsideTaskbarFrame(wux::DependencyObject element)
{
	try
	{
		auto current = wux::Media::VisualTreeHelper::GetParent(element);
		for (int depth = 0; depth < 24 && current; ++depth)
		{
			if (const auto framework = current.try_as<wux::FrameworkElement>())
			{
				if (EqualsIgnoreCase({ framework.Name() }, kTaskbarFrameName) ||
					EqualsIgnoreCase(winrt::get_class_name(framework), kTaskbarFrameType))
				{
					return true;
				}
			}

			current = wux::Media::VisualTreeHelper::GetParent(current);
		}
	}
	catch (const hresult_error& e)
	{
		TapLog(L"[watcher] 向上找父级失败: 0x%08X", static_cast<unsigned>(e.code().value));
	}
	catch (...)
	{
	}

	return false;
}

void VisualTreeWatcher::Register(InstanceHandle handle, const wchar_t* name, bool insideTaskbar)
{
	auto rect = FromHandle<wux::Shapes::Rectangle>(handle);

	// 有的系统版本在 Add 时父链还没接上，这时候按名字兜底登记：
	// explorer 的 XAML 里叫 BackgroundFill/BackgroundStroke 的 Rectangle 只有任务栏。
	if (!insideTaskbar)
	{
		TapLog(L"[watcher] %s 未确认在 TaskbarFrame 内，仍然登记", name);
	}

	Entry entry{ handle, rect, rect.Fill(), EqualsIgnoreCase(name, kBackgroundStrokeName) };
	m_entries.push_back(entry);
	ApplyOne(m_entries.back(), m_mode, m_color);

	TapRuntime::PublishStatus(kStatusReady, static_cast<LONG>(m_entries.size()));
	TapLog(L"[watcher] 登记 %s，当前共 %zu 个", name, m_entries.size());
}

void VisualTreeWatcher::ApplyOne(Entry& entry, LONG mode, LONG color)
{
	try
	{
		if (mode == kModeRestore)
		{
			// 把亚克力那层合成子树摘掉，不然还原之后它还盖在上面
			try
			{
				wuxh::ElementCompositionPreview::SetElementChildVisual(entry.Rect, nullptr);
			}
			catch (...)
			{
			}

			entry.Rect.Fill(entry.Original);
			return;
		}

		// 透明/亚克力下，任务栏顶部那条一像素的线也一起去掉，否则边缘会露一条
		if (entry.IsBorder)
		{
			entry.Rect.Fill(nullptr);
			return;
		}

		winrt::Windows::UI::Color tint{};
		tint.A = static_cast<uint8_t>((color >> 24) & 0xFF);
		tint.R = static_cast<uint8_t>((color >> 16) & 0xFF);
		tint.G = static_cast<uint8_t>((color >> 8) & 0xFF);
		tint.B = static_cast<uint8_t>(color & 0xFF);

		if (mode == kModeAcrylic && TryApplyAcrylic(entry, tint))
		{
			return;
		}

		// 透明走纯色；亚克力合成失败也退回纯色，至少不是全透明一片
		{
			wux::Media::SolidColorBrush brush;
			brush.Color(tint);
			entry.Rect.Fill(brush);
		}
	}
	catch (const hresult_error& e)
	{
		TapLog(L"[watcher] 设置 Fill 失败: 0x%08X", static_cast<unsigned>(e.code().value));
	}
	catch (...)
	{
		TapLog(L"[watcher] 设置 Fill 未知异常");
	}
}

void VisualTreeWatcher::ApplyOnXamlThread(LONG mode, LONG color)
{
	m_mode = mode;
	m_color = color;

	// 拷贝一份再遍历：设置 Fill 有可能间接触发新的可视树回调，
	// 那些回调会往 m_entries 里加东西，直接遍历原表会踩坏迭代器。
	const auto snapshot = m_entries;
	for (auto entry : snapshot)
	{
		ApplyOne(entry, mode, color);
	}

	TapRuntime::PublishStatus(kStatusReady, static_cast<LONG>(m_entries.size()));
	TapLog(L"[watcher] 应用模式=%ld 颜色=0x%08X 生效 %zu 个", mode, color, m_entries.size());
}

HRESULT VisualTreeWatcher::OnVisualTreeChange(
	ParentChildRelation, VisualElement element, VisualMutationType mutationType)
{
	try
	{
		if (mutationType == Remove)
		{
			for (auto it = m_entries.begin(); it != m_entries.end(); ++it)
			{
				if (it->Handle == element.Handle)
				{
					// 任务栏重建（换分辨率、重启 explorer）时把旧对象丢掉，别再去碰
					m_entries.erase(it);
					break;
				}
			}

			return S_OK;
		}

		if (mutationType != Add || element.Type == nullptr)
		{
			return S_OK;
		}

		const std::wstring_view type{ element.Type, SysStringLen(element.Type) };
		if (!EqualsIgnoreCase(type, L"Windows.UI.Xaml.Shapes.Rectangle"))
		{
			return S_OK;
		}

		const std::wstring name = element.Name
			? std::wstring(element.Name, SysStringLen(element.Name))
			: std::wstring();

		const bool isFill = EqualsIgnoreCase(name, kBackgroundFillName);
		const bool isStroke = EqualsIgnoreCase(name, kBackgroundStrokeName);

		// 前 40 个 Rectangle 一律记名字：万一哪个系统版本把 BackgroundFill 改了名，
		// 看 tap.log 就知道实际叫什么，不用靠猜。
		if (!isFill && !isStroke && m_loggedRectangles >= 40)
		{
			return S_OK;
		}

		bool insideTaskbar = false;
		try
		{
			insideTaskbar = IsInsideTaskbarFrame(FromHandle<wux::DependencyObject>(element.Handle));
		}
		catch (...)
		{
		}

		if (m_loggedRectangles < 40)
		{
			++m_loggedRectangles;
			TapLog(L"[watcher] Rectangle name='%s' 任务栏内=%d", name.c_str(), insideTaskbar ? 1 : 0);
		}

		if (!isFill && !isStroke)
		{
			return S_OK;
		}

		Register(element.Handle, isStroke ? kBackgroundStrokeName : kBackgroundFillName, insideTaskbar);
		return S_OK;
	}
	catch (const hresult_error& e)
	{
		TapLog(L"[watcher] OnVisualTreeChange 异常: 0x%08X", static_cast<unsigned>(e.code().value));
		return e.code();
	}
	catch (...)
	{
		return E_FAIL;
	}
}

HRESULT VisualTreeWatcher::OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) noexcept
{
	return S_OK;
}

bool VisualTreeWatcher::TryApplyAcrylic(Entry& entry, const winrt::Windows::UI::Color& tint)
{
	// 亚克力 = 背景高斯模糊 + 一层半透明着色。
	//
	// AcrylicBrush(Backdrop) 在 explorer 的 XAML 岛里拿不到窗口背景，
	// 画出来只有颜色没有磨砂，所以这里往 BackgroundFill 上挂一棵合成子树：
	//   底：BackdropBrush → 高斯模糊（自己实现的 D2D 效果）
	//   上：半透明纯色，负责 tint
	const wchar_t* step = L"a 入口";

	try
	{
		auto element = entry.Rect;

		step = L"b 取元素可视化";
		auto visual = wuxh::ElementCompositionPreview::GetElementVisual(element);
		auto compositor = visual.Compositor();

		step = L"c BackdropBrush";
		auto backdrop = compositor.CreateBackdropBrush();

		step = L"d EffectFactory";
		auto effect = winrt::make_self<GaussianBlurEffect>();
		effect->BlurAmount = 14.0f;
		effect->Source = wuc::CompositionEffectSourceParameter(L"backdrop");
		auto factory = compositor.CreateEffectFactory(*effect);

		step = L"e EffectBrush";
		auto blurBrush = factory.CreateBrush();
		blurBrush.SetSourceParameter(L"backdrop", backdrop);

		step = L"f 组装子树";
		auto blurSprite = compositor.CreateSpriteVisual();
		blurSprite.Brush(blurBrush);

		auto tintSprite = compositor.CreateSpriteVisual();
		tintSprite.Brush(compositor.CreateColorBrush(tint));

		auto container = compositor.CreateContainerVisual();
		container.Children().InsertAtTop(blurSprite);
		container.Children().InsertAtTop(tintSprite);

		const winrt::Windows::Foundation::Size size{
			static_cast<float>(element.ActualWidth()),
			static_cast<float>(element.ActualHeight()) };
		container.Size(size);
		blurSprite.Size(size);
		tintSprite.Size(size);

		step = L"g 挂子可视化";
		wuxh::ElementCompositionPreview::SetElementChildVisual(element, container);

		element.Fill(nullptr);

		TapLog(L"[acrylic] 合成层挂载成功 %.0fx%.0f", size.Width, size.Height);
		return true;
	}
	catch (const winrt::hresult_error& e)
	{
		TapLog(L"[acrylic] %s 失败: 0x%08X %s", step,
			static_cast<unsigned>(e.code().value), e.message().c_str());
		return false;
	}
	catch (...)
	{
		TapLog(L"[acrylic] %s 未知异常", step);
		return false;
	}
}
