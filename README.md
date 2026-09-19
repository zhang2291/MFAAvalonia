# MFAAvalonia（个人 Fork）

本仓库是 [MaaXYZ/MFAAvalonia](https://github.com/MaaXYZ/MFAAvalonia) 的个人 Fork。

- 原仓库：`MaaXYZ/MFAAvalonia`
- 当前 Fork：`zhang2291/MFAAvalonia`
- 用途：作为 MBCCtools 的 Windows 桌面 GUI，并集成个人使用的 MaaPipelineEditor 与相关本地修改。
- 本仓库仅用于个人设备、本地构建与测试，不代表上游官方版本。
- LICENSE 与原作者/贡献者版权信息按上游保留。

## 构建

环境：Windows 10/11、.NET 10 SDK。

```powershell
cd D:\a-maa-dev\MFAAvalonia
dotnet restore
dotnet publish .\MFAAvalonia.Desktop\MFAAvalonia.Desktop.csproj -c Release -r win-x64
```

当前 Windows x64 publish 输出通常位于：

```text
D:\a-maa-dev\MFAAvalonia\bin\AnyCPU\Release\win-x64\publish
```

该输出由 MBCCtools 的 `build_local_gui.ps1` 继续组装为最终桌面运行目录。

## 更新上游

```powershell
git fetch upstream
git merge upstream\master
```

如上游默认分支发生变化，请以实际分支名为准。个人修改提交到 `origin`，上游仓库保留为 `upstream`。

## License

本项目许可证沿用上游，详见 `LICENSE`。
