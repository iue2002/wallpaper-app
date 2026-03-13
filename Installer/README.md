# WiX MSI 构建

已可直接构建 MSI。

## 1) 先发布自包含文件

```powershell
.\build-msi-ready.ps1 -Runtime win-x64 -Clean
```

## 2) 构建 MSI

```powershell
.\Installer\build-wix-msi.ps1 -Runtime win-x64 -Version 1.0.0
```

## 输出

- 发布目录：`artifacts\msi-input\win-x64`
- MSI：`artifacts\msi\KWallpaper-1.0.0-win-x64.msi`
