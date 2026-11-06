# 小K壁纸

一个 Windows 桌面壁纸工具，WinUI 3 写的。核心用法就两个：想换壁纸的时候点一下，或者让它自己定时换。

这个仓库的 `perf/quality-fast` 分支是把 2.1.0.0 那版几乎推倒重做了一遍——不是因为功能不够，是那版太慢、太占内存、还会卡死。下面写清楚这版改了什么、还剩什么问题。

---

## 这一版到底改了什么

### 一、图片加载：从“每张都下完整原图”改成“只下该下的”

以前的流程很离谱：列表里每一张候选图，先把整张 4K 原图下载到本地，再完整下载第二遍去读分辨率判断合不合格，不合格还把文件删掉。九个图源串行跑，单个源还要等 30 秒超时，所以首屏要转很久。

现在：

- 列表只解缩略图尺寸（按屏幕缩放算，上限 560px），四源不再预下整图
- 缓存文件名改用 FNV-1a 稳定哈希。原来用的 `string.GetHashCode()` 每个进程的种子都不一样，重启后算出来的名字和上次对不上，**缓存从来没命中过**
- 三个优先源并进同一个并发池，谁先回来先用，不再串行等
- Bing 缩略图换 `_400x240` 那一档

实测首屏 8 张：**2584KB → 144KB**，耗时 86ms → 129ms。多花的那 43ms 是并发的开销，值。

### 二、内存：250MB → 25MB

原因有三个，都修了：

1. **两处列表被包在 ScrollViewer 里**，高度约束变成无限，虚拟化彻底失效——几百张卡片全部实例化。现在 GridView 自己滚。
2. **图片按原图尺寸解码**。现在列表按卡片尺寸解，全屏预览按屏幕宽度解（上限 2560）。
3. **换壁纸时把整张 4K 图读进内存**。`GetByteArrayAsync` 内部先用倍增的 MemoryStream 攒完整张图，再 `ToArray()` 复制一遍，一张十几 MB 的图能把托管堆顶到 36MB。现在改 `ResponseHeadersRead` + 64KB 缓冲直接落盘。

另外：关窗口时会真正断掉位图引用（`Image.Source = null` 递归清、`ItemsSource = null`），然后强制 GC 一次再空工作集。控件样式字典（`XamlControlsResources`）也从 `App.xaml` 里拿掉了，改成**打开窗口前才合并**——后台常驻只挂个托盘，不需要整套控件样式。

结果：**开机自启的纯后台进程约 25MB**；打开过一次主界面再关掉会回到 30MB 左右，因为 XAML 运行时一旦启动就不会卸载，这是框架的底线，代码层面改不动。

### 三、卡死：全部移出 UI 线程

以前有两个必卡的地方：

- **换壁纸**：`SystemParametersInfo` 和 `IDesktopWallpaper` 是同步阻塞的，而 `IDesktopWallpaper` 是 explorer 的 LocalServer，在没消息泵的线程上同步调用会和它互相等死。现在走独立 STA 线程 + 硬超时，SPI 优先、COM 只兜底。
- **点卡片**：预览器里调了一次 `UpdateLayout()`，等于同步重排整棵视觉树（几百张卡片）。删掉了，改成先挂载再操作合成层。

### 四、后台常驻：一个进程，两种形态

同一份 exe、同一个进程，不会变成两个程序。

- 托盘是纯 Win32 隐藏窗口（不依赖 XAML），右键菜单有：显示主界面 / 立即换一张 / 开机启动 / 退出
- 关掉主窗口只销毁 UI，后台继续按定时换壁纸；进程靠 `DispatcherShutdownMode.OnExplicitShutdown` 活着
- 开机自启写 `HKCU\...\Run` 带 `/background`，只起托盘不起窗口。商店（MSIX）版走系统 `StartupTask` API——因为 MSIX 的 exe 在带版本号的目录里，写注册表会在应用更新后失效

### 五、任务栏外观（新增）

右键菜单 →「任务栏外观」，三档：**关闭 / 透明 / 亚克力**。

- **透明**：分档浓度（全透明 / 25% / 50% / 75% / 不透明黑）
- **亚克力**：真的高斯模糊 + 一层着色，浓度同样分档
- 想要连续调节，菜单最后一项「更多设置」能弹滑块

实现方式和 TranslucentTB 一个路子：往 explorer 里注入一个 TAP DLL，拿到 XAML 之后把任务栏背景那层 `BackgroundFill` 换掉。差别在于亚克力不能用 `AcrylicBrush`——任务栏是 XAML 岛，`AcrylicBrush(Backdrop)` 在那儿拿不到窗口背景，画出来只有颜色没有磨砂。所以是自己实现了一个 D2D 高斯模糊效果，拼成 `BackdropBrush → 模糊 → 半透明着色`。

**这块限制很多，写在这里免得踩：**

- XAML 诊断通道**一个 explorer 进程同时只能被一个程序占用**。装了 TranslucentTB 这类工具，两边会抢；实测能共存，但谁先占谁生效。
- 改完注入 DLL 必须**重启资源管理器**才能换成新版，因为 XAML 诊断会把 DLL 永久钉在 explorer 里，同一路径再挂 hook 不会重跑 `DllMain`。
- 商店（MSIX）版默认不做注入（`IsSupported` 里挡住了），审核和杀软对这种行为不友好。

---

## 功能

- 壁纸源：Bing 每日 / Pexels / Unsplash / Pixabay / Picsum / Wikimedia Commons / 360 / Peapix（实测失效的源默认关闭，代码留着）
- 桌面、锁屏分别设定时切换，间隔从 1 分钟到 1 天
- 多图卡片：纯图 + 圆角 + 悬停缩放，单击全屏预览、再单击关闭、滚轮切换
- 托盘常驻，关窗口不退出
- 任务栏透明 / 亚克力
- 下载缓存、清理缓存、设为壁纸/锁屏、下载原图

## 系统要求

- Windows 10 2004 (19041) 及以上（任务栏外观在 Win11 的 XAML 任务栏上才有意义）
- .NET 8 Runtime
- 源码构建需要 VS 2022 + Windows App SDK；改注入模块还需要 MSVC 和 Windows SDK

## 从源码构建

```powershell
dotnet restore 小K壁纸.csproj -r win-x64
msbuild 小K壁纸.csproj /p:Configuration=Debug /p:Platform=x64
```

产物在 `bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\小K壁纸.exe`。

**注入模块**（只有改 C++ 时才需要）：

```powershell
powershell -File native\build-native.ps1
```

产物 `native\dist\ExplorerTap.dll` 已经入库，`dotnet build` 会自动复制到输出目录。

商店包（MSIX）：`powershell -File Installer\build-store-msix.ps1 -Configuration Release -Platform x64 -Version 2.1.0.0`

## 分支与历史

`master` 一直停在最初的提交没动过。所有改造都在 `perf/quality-fast`。

历史版本的实验也留着，没有覆盖也没有删：

- `archive/full-20261105`：完整的 10 次提交
- `v1.1.0-initial`、`store/2.1.0.0`、`exp/resolution-filter-0320`、`exp/quality-filter-0323`、`head/2026-03-23`

后两个 tag 是当初尝试过的「下载整图再按分辨率筛选」方案，实测太慢，已经废弃，留作对照。

## 调试日志

都在 `%LOCALAPPDATA%\小K壁纸\`：

- `app.log`：启动、调度、换壁纸、内存回收
- `tap.log`：任务栏注入的每一步（连接名、返回码、模糊层挂载结果）

## 踩过的坑（给后来的人）

- `Border.CornerRadius` **不裁子元素**，圆角必须用合成层 `RoundedRectangleGeometry`
- `BitmapImage.UriSource` 指向网络地址会走系统图片栈，受代理和 UA 影响，失败还是静默的
- 用 `ScrollViewer` 包 `GridView` 等于关掉虚拟化
- `SetWindowsHookEx` 把 DLL 带进目标进程后，同路径再挂一次不会重跑 `DllMain`
- WinUI 不会给 `MenuFlyout` 里的元素生成 `x:Name` 字段；菜单项只能在运行时遍历
- 自己实现的 `IGraphicsEffectD2D1Interop` 必须把 D2D 效果的所有属性都报出来，少一个 `CreateEffectFactory` 就返回 `E_INVALIDARG`

## 许可证

MIT
