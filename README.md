# APK Resource Assistant

Windows GUI 工具：从 APKPure 或 Google Play 下载 APK/Split APK，安全解压并自动识别 Unity、Godot、Unreal，生成适合 AssetRipper 使用的分析目录。

## 功能

- APKPure 免登录、Google Play 匿名、Google Play 个人账号三种来源
- 仅下载、下载并解压、下载并准备逆向三种流程
- 每个 split 独立解压，保留 Base、ABI、语言、屏幕密度和 Play Asset Delivery 结构
- ZIP 路径穿越防护、损坏 APK 检查和解压磁盘空间检查
- Unity/Godot/Unreal 自动识别
- Unity IL2CPP/Mono 判断，定位 `libil2cpp.so`、`global-metadata.dat`、Managed DLL 和 AssetBundle
- 生成 `analysis.json`、中文分析报告和关键文件索引
- 配置本地 AssetRipper 后，Unity 样本可自动启动 AssetRipper、打开输入目录并复制路径
- 单文件、自包含 Windows x64 发布

## 构建

需要 .NET 9 SDK：

```powershell
dotnet build .\ApkResourceAssistant.sln -c Release
dotnet publish .\src\ApkResourceAssistant\ApkResourceAssistant.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

运行测试：

```powershell
dotnet run --project .\tests\ApkResourceAssistant.Tests\ApkResourceAssistant.Tests.csproj -c Release
```

## 输出结构

```text
<保存根目录>/<包名>/<时间戳>/
├─ Original_APKs/
├─ AssetRipper_Input/
│  ├─ base/
│  ├─ assetPackInstallTime/
│  └─ config.arm64_v8a/
├─ KeyFiles/
├─ analysis.json
└─ 分析说明.txt
```

原始 APK 始终保留。AssetRipper 只用于 Unity；Godot、Unreal 和未知引擎会完整解压并给出对应提示。

## 第三方组件

发布程序内置 EFForg 的 `apkeep 1.0.0` Windows x64 可执行文件，并将 PE 栈预留调整为 64 MB。详见 `THIRD_PARTY_NOTICES.md`。

## License

MIT
