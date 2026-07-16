# APK Resource Assistant

面向 Windows 10/11 x64 的 APK 下载、解压和多引擎资源恢复工作台。v5 保留三个互不依赖的入口，并加入可信的引擎版本识别、恢复准备检查、恢复质量摘要和最近任务。

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
- 从原生运行时、序列化资源、PCK/容器头和恢复日志提取引擎版本，并显示证据与可信度
- 区分运行时版本、资源兼容版本、GDScript 字节码版本、推荐编辑器版本和恢复工具版本
- 检查 Unity IL2CPP 文件配对、Godot 恢复输入以及 Unreal UTOC/UCAS 配对

### 3. 引擎恢复

- 可选择已有解压目录、以前的任务根目录或 `Extracted`；旧版 `AssetRipper_Input` 仍兼容
- 已有目录只做快速、只读扫描，不复制、不改写
- Unity：沿用 AssetRipper 1.3.14+ 自动启动、`/LoadFolder` 自动载入、`/Reset` 复用和兼容回退
- Godot：自动定位 PCK、`project.godot`、`.godot` 或场景资源，通过 GDRETools/gdsdecomp 无头模式恢复到 `Godot_Recovered`
- Unreal Engine：识别 UE4/UE5 PAK 与 UE5 IoStore 的 UTOC/UCAS，使用 repak/retoc 检查和解包，并准备标准 `Content/Paks` 目录后启动 FModel
- GDRETools、repak、retoc、FModel 使用固定版本下载地址和内置 SHA-256；首次使用下载，之后从本机缓存复验并复用
- Godot PCK 密钥与 Unreal AES 密钥只保存在当前操作内存和子进程参数中，输入框结束后清空，不写入设置、报告或日志
- Godot 会区分 GDScript 与 C#/.NET，并分别推荐标准版或 .NET 版编辑器
- 恢复结果记录输出文件数、容量、警告、失败容器及实际使用的工具版本

## 引擎版本与最近任务

- 结果卡片显示引擎、版本、脚本运行时、恢复准备状态和版本可信度
- “版本依据”可展开查看来源文件及冲突；“复制版本”和“版本下载页”用于准备对应编辑器
- Godot 原生运行时与 GDRETools 字节码版本不一致时会同时保留，不会静默覆盖
- “最近任务”最多保留 30 条本机索引，可搜索、按引擎筛选、继续任务或打开目录
- 最近任务索引位于 `%LOCALAPPDATA%\GooglePlayApkDownloader\recent-tasks.json`；移除记录不会删除任务文件

## 任务目录

```text
<保存根目录>/<包名>_<时间戳>/
├─ Original_APKs/
├─ Extracted/
│  ├─ base/
│  ├─ assetPackInstallTime/
│  └─ config.arm64_v8a/
├─ KeyFiles/
├─ Godot_Recovered/          # Godot 恢复时生成
├─ Unreal_Input/             # UE 容器浏览目录
├─ Unreal_Extracted/         # UE 解包结果
├─ Logs/
├─ task.json
├─ analysis.json             # 包含引擎版本、可信度和恢复准备信息
├─ engine-recovery.json
└─ 分析说明.txt
```

原始 APK 和全部 split 始终保留。重复执行会创建新任务，不覆盖已有结果。游戏运行后另外下载的 Addressables、OBB 或热更新资源不在 APK 内，需要单独采集。

新任务使用扁平的 `<包名>_<时间戳>` 目录，比 v3/v4 早期的 `<包名>/<时间戳>` 少一层。`Extracted` 是 Unity、Godot、Unreal 共用的中立名称；旧任务中的 `AssetRipper_Input` 会继续自动识别，不需要迁移或重命名。

## 设置与隐私

Google 凭据、OAuth 兑换和 AssetRipper 路径集中放在“设置”窗口。选择记住凭据时，token 使用当前 Windows 用户的 DPAPI 加密，设置文件保存在 `%LOCALAPPDATA%\GooglePlayApkDownloader\settings.json`。v2/v3 设置会自动迁移。按需下载的引擎工具位于 `%LOCALAPPDATA%\GooglePlayApkDownloader\Tools`。

## 构建与测试

需要 .NET 9 SDK：

```powershell
dotnet run --project .\tests\ApkResourceAssistant.Tests\ApkResourceAssistant.Tests.csproj -c Release
dotnet build .\ApkResourceAssistant.sln -c Release
dotnet publish .\src\ApkResourceAssistant\ApkResourceAssistant.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

发布结果是自包含单文件 EXE，目标机无需另装 .NET 或 7-Zip。AssetRipper 由用户在设置中选择；Godot/UE 工具首次使用时从官方 Release 自动下载。

## 第三方组件

发布程序内置 EFForg 的 `apkeep 1.0.0` Windows x64 可执行文件，并将 PE 栈预留调整为 64 MB。GDRETools、repak、retoc 与 FModel 作为独立程序按需下载。版本、许可证、下载地址和校验值详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## License

MIT
