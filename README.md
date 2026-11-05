# 小K壁纸应用

小K壁纸是一款功能丰富的壁纸应用，支持多种壁纸源，包括Bing、Unsplash、Pexels等。

## 功能特性

- 📱 多种壁纸源选择
- 🔄 定时轮换壁纸
- 🚀 开机自启
- 🖼️ 壁纸预览
- 💾 本地缓存
- 🎨 深色模式支持

## 系统要求

- Windows 10/11
- .NET 8.0 Runtime

## 安装方法

### 方法一：使用MSI安装包
1. 从 [Releases](https://github.com/iue2002/wallpaper-app/releases) 页面下载最新的MSI安装包
2. 双击运行安装包，按照提示完成安装

### 方法二：从源码构建
仓库根目录就是项目根目录，仓库内没有 `.sln` 文件。

1. 克隆仓库：`git clone https://github.com/iue2002/wallpaper-app.git`
2. 还原依赖：`dotnet restore 小K壁纸.csproj -r win-x64`
3. 用 Visual Studio 2022 直接打开 `小K壁纸.csproj`，或命令行构建：
   `msbuild 小K壁纸.csproj /p:Configuration=Debug /p:Platform=x64`
4. 运行 `bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\小K壁纸.exe`

商店包（MSIX）由 GitHub Actions 的 `Build MSIX for Store` 工作流生成，也可本地执行：
`powershell -File Installer\build-store-msix.ps1 -Configuration Release -Platform x64 -Version 2.1.0.0`

## 壁纸源

- Bing每日壁纸
- Unsplash
- Pexels
- 360壁纸
- Peapix

## 开发环境

- Visual Studio 2022
- .NET 8.0 SDK
- Windows App SDK

## 贡献

欢迎提交Issue和Pull Request！

## 许可证

MIT License
