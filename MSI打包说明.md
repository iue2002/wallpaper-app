# MSI 打包说明（避免提示安装 .NET Runtime）

## 1) 先生成自包含发布文件
在 `App1` 目录执行：

```powershell
.\build-msi-ready.ps1 -Runtime win-x64 -Clean
```

输出目录：

- `artifacts\msi-input\win-x64`

> 如需 32 位/ARM64：
> - `-Runtime win-x86`
> - `-Runtime win-arm64`

---

## 2) 制作 MSI 时必须注意

在安装项目中，把 `artifacts\msi-input\win-x64` 目录里的**全部文件**加入 MSI。

- 不要只添加 `小K壁纸.exe`
- 不要只用“Primary output”

否则会出现“需要安装 .NET Desktop Runtime”提示。

---

## 3) 验证

在一台未安装 .NET SDK/Runtime 的干净机器上安装 MSI，直接运行应用，若可正常启动则打包正确。
