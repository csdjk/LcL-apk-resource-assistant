# APK Resource Assistant

面向 Windows 10/11 x64 的 APK 下载与游戏资源分析工作台。v3 将流程拆成三个互不依赖的入口：下载 APK、解压与分析、用 AssetRipper 打开。你可以从任意一步开始，工具不会擅自执行下一阶段。

## 三阶段工作流

### 1. 下载 APK

- APKPure 免登录
- Google Play 一键匿名
- Google Play 个人账号
- 可选择下载 Split APK
- 下载结果保存在独立的时间戳任务目录中，完成后按需继续解压

### 2. 解压与分析

- 支持单个 APK、多个 Split APK、含 APK 的文件夹和以前的任务目录
- 外部 APK 会复制到新的任务目录，源文件保持原样
- 每个 split 解压到独立子目录，保留 Base、ABI、语言、屏幕密度和 Play Asset Delivery 结构
- 使用 .NET 内置 ZIP API，并检查损坏包、磁盘空间及 ZIP 路径穿越
- 自动识别 Unity、Godot、Unreal 和未知引擎
- Unity 样本进一步判断 IL2CPP/Mono，定位 `libil2cpp.so`、`global-metadata.dat`、Managed DLL、AssetBundle 等关键文件

### 3. 用 AssetRipper 打开

- 可选择已有解压目录、以前的任务根目录或 `AssetRipper_Input`
- 已有目录只做快速、只读扫描，不复制、不改写
- 对 AssetRipper 1.3.14+ 优先使用随机本机端口启动，并通过 `/LoadFolder` 自动载入目录
- 重复载入同一受管实例时先调用 `/Reset`
- 接口不兼容或超时时自动退回到普通启动，同时复制路径并打开输入目录
- Godot 和 Unreal 样本会显示对应工具建议，避免误用 AssetRipper

## 任务目录

```text
<保存根目录>/<包名>/<时间戳>/
├─ Original_APKs/
├─ AssetRipper_Input/
│  ├─ base/
│  ├─ assetPackInstallTime/
│  └─ config.arm64_v8a/
├─ KeyFiles/
├─ task.json
├─ analysis.json
└─ 分析说明.txt
```

原始 APK 和全部 split 始终保留。重复执行会创建新任务，不覆盖已有结果。游戏运行后另外下载的 Addressables、OBB 或热更新资源不在 APK 内，需要单独采集。

## 设置与隐私

Google 凭据、OAuth 兑换和 AssetRipper 路径集中放在“设置”窗口。选择记住凭据时，token 使用当前 Windows 用户的 DPAPI 加密，设置文件保存在 `%LOCALAPPDATA%\GooglePlayApkDownloader\settings.json`。v2 设置会自动迁移。

## 构建与测试

需要 .NET 9 SDK：

```powershell
dotnet run --project .\tests\ApkResourceAssistant.Tests\ApkResourceAssistant.Tests.csproj -c Release
dotnet build .\ApkResourceAssistant.sln -c Release
dotnet publish .\src\ApkResourceAssistant\ApkResourceAssistant.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

发布结果是自包含单文件 EXE，目标机无需另装 .NET 或 7-Zip。AssetRipper 本身由用户单独下载并在设置中选择，不包含在本项目发布包内。

## 第三方组件

发布程序内置 EFForg 的 `apkeep 1.0.0` Windows x64 可执行文件，并将 PE 栈预留调整为 64 MB。详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## License

MIT
