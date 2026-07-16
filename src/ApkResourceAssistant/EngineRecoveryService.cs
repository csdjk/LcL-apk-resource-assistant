using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace GooglePlayApkDownloader;

internal sealed class EngineRecoveryService : IDisposable
{
    private const long DiskReserve = 512L * 1024 * 1024;
    private readonly AnalysisService _analysisService;
    private readonly IExternalToolProvider _tools;
    private readonly IExternalProcessRunner _processes;
    private readonly Action<string, string> _launchGui;

    public EngineRecoveryService()
        : this(new AnalysisService(), new ExternalToolManager(), new ExternalProcessRunner(),
            (executable, workingDirectory) => Process.Start(new ProcessStartInfo(executable)
            { UseShellExecute = true, WorkingDirectory = workingDirectory }))
    { }

    internal EngineRecoveryService(AnalysisService analysisService, IExternalToolProvider tools,
        IExternalProcessRunner processes, Action<string, string>? launchGui = null)
    {
        _analysisService = analysisService;
        _tools = tools;
        _processes = processes;
        _launchGui = launchGui ?? ((_, _) => { });
    }

    public async Task<EngineRecoveryResult> RecoverAsync(EngineRecoveryRequest request,
        IProgress<WorkflowProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = NormalizeTemporaryKey(request.TemporaryKey);
        var analysis = await _analysisService.AnalyzeExistingDirectoryAsync(request.SelectedPath, progress, cancellationToken);
        return analysis.Engine switch
        {
            GameEngine.Unity => throw new InvalidOperationException("Unity 目录应由 AssetRipperLauncher 处理。"),
            GameEngine.Godot => await RecoverGodotAsync(analysis, key, progress, cancellationToken),
            GameEngine.Unreal => await RecoverUnrealAsync(analysis, key, request.ExtractUnrealContainers, progress, cancellationToken),
            _ => throw new InvalidOperationException("目录中没有识别出 Unity、Godot 或 Unreal Engine 特征。")
        };
    }

    private async Task<EngineRecoveryResult> RecoverGodotAsync(DirectoryAnalysis analysis, string? key,
        IProgress<WorkflowProgress>? progress, CancellationToken cancellationToken)
    {
        var inventory = analysis.EngineAssets ?? EngineAssetInventory.Empty;
        var input = ChooseGodotInput(analysis.InputDirectory, inventory.GodotPackages, inventory.GodotApks);
        var outputRoot = analysis.TaskRoot ?? Directory.GetParent(analysis.InputDirectory)?.FullName ?? analysis.InputDirectory;
        var output = CreateUniqueDirectoryPath(outputRoot, "Godot_Recovered");
        EnsureDiskSpace(outputRoot, Math.Max(analysis.TotalBytes * 3, DiskReserve));
        progress?.Report(new WorkflowProgress(WorkflowStage.DownloadingTools, "准备 GDRETools", 5));
        var installation = await _tools.EnsureInstalledAsync(ExternalToolId.GdreTools,
            new Progress<ExternalToolProgress>(value => progress?.Report(new WorkflowProgress(
                WorkflowStage.DownloadingTools, value.Message, value.Percent))), cancellationToken);
        Directory.CreateDirectory(output);
        var log = Path.Combine(outputRoot, "Logs", "gdre-tools.log");
        var arguments = new List<string> { "--headless", $"--recover={input}", $"--output={output}" };
        if (key != null) arguments.Add($"--key={key}");
        progress?.Report(new WorkflowProgress(WorkflowStage.RecoveringGodot, "正在恢复 Godot 工程…", 30));
        var result = await _processes.RunAsync(new ExternalProcessRequest(
            installation.ExecutablePath, arguments, installation.Directory, log, key == null ? [] : [key]),
            line => progress?.Report(new WorkflowProgress(WorkflowStage.RecoveringGodot, TrimProgress(line), null)), cancellationToken);
        if (result.ExitCode != 0 || !Directory.EnumerateFileSystemEntries(output).Any())
        {
            _launchGui(installation.ExecutablePath, installation.Directory);
            var reason = result.ExitCode != 0 ? $"退出码 {result.ExitCode}" : "恢复目录为空";
            var version = await EngineIntelligenceAnalyzer.EnrichGodotRecoveryAsync(
                analysis.EngineVersion ?? EngineVersionInfo.Unknown, output, log, cancellationToken);
            var summary = await EngineIntelligenceAnalyzer.BuildRecoverySummaryAsync(
                output, log, 0, 1, cancellationToken);
            var fallback = new EngineRecoveryResult(GameEngine.Godot, EngineRecoveryOutcome.ManualFallback,
                input, output, installation.Spec.DisplayName, installation.Spec.Version,
                $"GDRETools 自动恢复未完成（{reason}），已启动图形界面供手动检查输入和密钥。", log,
                FailedContainers: 1, ToolLaunched: true, EngineVersion: version, Summary: summary,
                ScriptRuntime: analysis.ScriptRuntime, Readiness: analysis.RecoveryReadiness);
            await PersistRecoveryAsync(analysis, fallback, cancellationToken);
            progress?.Report(new WorkflowProgress(WorkflowStage.ReadyForEngineTool, fallback.Message, 100));
            return fallback;
        }
        var recoveredVersion = await EngineIntelligenceAnalyzer.EnrichGodotRecoveryAsync(
            analysis.EngineVersion ?? EngineVersionInfo.Unknown, output, log, cancellationToken);
        var recoveredSummary = await EngineIntelligenceAnalyzer.BuildRecoverySummaryAsync(
            output, log, 0, 0, cancellationToken);
        var recovery = new EngineRecoveryResult(GameEngine.Godot, EngineRecoveryOutcome.Completed,
            input, output, installation.Spec.DisplayName, installation.Spec.Version,
            "Godot 工程恢复完成。请按运行时版本与字节码兼容提示选择编辑器。", log,
            EngineVersion: recoveredVersion, Summary: recoveredSummary,
            ScriptRuntime: analysis.ScriptRuntime, Readiness: analysis.RecoveryReadiness);
        await PersistRecoveryAsync(analysis, recovery, cancellationToken);
        progress?.Report(new WorkflowProgress(WorkflowStage.ReadyForEngineTool, recovery.Message, 100));
        return recovery;
    }

    private async Task<EngineRecoveryResult> RecoverUnrealAsync(DirectoryAnalysis analysis, string? key, bool extract,
        IProgress<WorkflowProgress>? progress, CancellationToken cancellationToken)
    {
        var inventory = analysis.EngineAssets ?? EngineAssetInventory.Empty;
        if (inventory.UnrealPakFiles.Count == 0 && inventory.UnrealUtocFiles.Count == 0)
            throw new InvalidOperationException("没有找到 Unreal PAK 或 IoStore UTOC 容器。");
        foreach (var utoc in inventory.UnrealUtocFiles)
        {
            var stem = Path.GetFileNameWithoutExtension(utoc);
            if (!inventory.UnrealUcasFiles.Any(ucas =>
                    Path.GetFileNameWithoutExtension(ucas).Equals(stem, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"IoStore 容器缺少配对的 UCAS：{Path.GetFileName(utoc)}");
        }
        var workspaceRoot = analysis.TaskRoot ?? CreateUniqueDirectoryPath(
            Directory.GetParent(analysis.InputDirectory)?.FullName ?? analysis.InputDirectory, "Unreal_Recovery");
        Directory.CreateDirectory(workspaceRoot);
        var staged = CreateUniqueDirectoryPath(workspaceRoot, "Unreal_Input");
        var paksDirectory = Path.Combine(staged, "Game", "Content", "Paks");
        Directory.CreateDirectory(paksDirectory);
        foreach (var source in inventory.UnrealPakFiles.Concat(inventory.UnrealUtocFiles).Concat(inventory.UnrealUcasFiles))
            LinkOrCopy(source, MakeUniqueFilePath(paksDirectory, Path.GetFileName(source)));

        var estimated = SumFileLengths(inventory.UnrealPakFiles.Concat(inventory.UnrealUcasFiles));
        if (extract) EnsureDiskSpace(workspaceRoot, Math.Max(estimated * 3, DiskReserve));
        progress?.Report(new WorkflowProgress(WorkflowStage.DownloadingTools, "准备 Unreal 工具链", 5));
        var repak = await _tools.EnsureInstalledAsync(ExternalToolId.Repak,
            new Progress<ExternalToolProgress>(value => progress?.Report(new WorkflowProgress(WorkflowStage.DownloadingTools, value.Message, value.Percent))), cancellationToken);
        var retoc = inventory.UnrealUtocFiles.Count > 0
            ? await _tools.EnsureInstalledAsync(ExternalToolId.Retoc, null, cancellationToken) : null;
        var fmodel = await _tools.EnsureInstalledAsync(ExternalToolId.FModel, null, cancellationToken);
        var output = CreateUniqueDirectoryPath(workspaceRoot, "Unreal_Extracted");
        Directory.CreateDirectory(output);
        var logs = Path.Combine(workspaceRoot, "Logs");
        Directory.CreateDirectory(logs);
        var failures = 0;
        var processed = 0;

        foreach (var pak in inventory.UnrealPakFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            progress?.Report(new WorkflowProgress(WorkflowStage.InspectingUnreal, $"检查 PAK：{Path.GetFileName(pak)}", null, processed,
                inventory.UnrealPakFiles.Count + inventory.UnrealUtocFiles.Count));
            var global = key == null ? new List<string>() : ["--aes-key", key];
            var info = global.Concat(["info", pak]).ToArray();
            var inspectResult = await _processes.RunAsync(new ExternalProcessRequest(repak.ExecutablePath, info,
                repak.Directory, Path.Combine(logs, $"repak-info-{processed}.log"), key == null ? [] : [key]), null, cancellationToken);
            if (inspectResult.ExitCode != 0) { failures++; continue; }
            if (!extract) continue;
            var destination = Path.Combine(output, "PAK", SafeName(Path.GetFileNameWithoutExtension(pak)));
            Directory.CreateDirectory(destination);
            var unpack = global.Concat(["unpack", "--output", destination, pak]).ToArray();
            var unpackResult = await _processes.RunAsync(new ExternalProcessRequest(repak.ExecutablePath, unpack,
                repak.Directory, Path.Combine(logs, $"repak-unpack-{processed}.log"), key == null ? [] : [key]), null, cancellationToken);
            if (unpackResult.ExitCode != 0) failures++;
        }

        if (retoc != null)
        {
            foreach (var utoc in inventory.UnrealUtocFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(new WorkflowProgress(WorkflowStage.InspectingUnreal, $"验证 IoStore：{Path.GetFileName(utoc)}", null, processed,
                    inventory.UnrealPakFiles.Count + inventory.UnrealUtocFiles.Count));
                var global = key == null ? new List<string>() : ["--aes-key", key];
                var verify = global.Concat(["verify", utoc]).ToArray();
                var verifyResult = await _processes.RunAsync(new ExternalProcessRequest(retoc.ExecutablePath, verify,
                    retoc.Directory, Path.Combine(logs, $"retoc-verify-{processed}.log"), key == null ? [] : [key]), null, cancellationToken);
                if (verifyResult.ExitCode != 0) { failures++; continue; }
                if (!extract) continue;
                var destination = Path.Combine(output, "IoStore", SafeName(Path.GetFileNameWithoutExtension(utoc)));
                Directory.CreateDirectory(destination);
                var unpack = global.Concat(["unpack", utoc, destination]).ToArray();
                var unpackResult = await _processes.RunAsync(new ExternalProcessRequest(retoc.ExecutablePath, unpack,
                    retoc.Directory, Path.Combine(logs, $"retoc-unpack-{processed}.log"), key == null ? [] : [key]), null, cancellationToken);
                if (unpackResult.ExitCode != 0) failures++;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _launchGui(fmodel.ExecutablePath, fmodel.Directory);
        var message = failures == 0
            ? "Unreal 容器已检查，FModel 已启动；请选择已准备的 Content/Paks 目录。"
            : $"FModel 已启动；{failures} 个容器检查或解包失败，常见原因是版本差异或需要 AES 密钥。";
        var recovery = new EngineRecoveryResult(GameEngine.Unreal,
            failures == 0 ? EngineRecoveryOutcome.ToolLaunched : EngineRecoveryOutcome.ManualFallback,
            staged, output, $"{repak.Spec.DisplayName} + {(retoc == null ? "" : retoc.Spec.DisplayName + " + ")}FModel",
            $"{repak.Spec.Version}/{retoc?.Spec.Version ?? "-"}/{fmodel.Spec.Version}", message, logs,
            processed, failures, true);
        var summary = await EngineIntelligenceAnalyzer.BuildRecoverySummaryAsync(
            output, null, processed, failures, cancellationToken);
        recovery = recovery with
        {
            EngineVersion = analysis.EngineVersion ?? EngineVersionInfo.Unknown,
            Summary = summary,
            ScriptRuntime = analysis.ScriptRuntime,
            Readiness = analysis.RecoveryReadiness
        };
        await PersistRecoveryAsync(analysis, recovery, cancellationToken);
        progress?.Report(new WorkflowProgress(WorkflowStage.ReadyForEngineTool, recovery.Message, 100));
        return recovery;
    }

    private static string ChooseGodotInput(string root, IReadOnlyList<string> candidates, IReadOnlyList<string> apks)
    {
        var pck = candidates.FirstOrDefault(File.Exists);
        if (pck != null) return pck;
        var project = candidates.FirstOrDefault(Directory.Exists);
        if (project != null) return project;
        var marker = Directory.EnumerateFiles(root, "project.godot", SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.EnumerateFiles(root, "project.binary", SearchOption.AllDirectories).FirstOrDefault();
        if (marker != null) return Path.GetDirectoryName(marker)!;
        var godotDirectory = Directory.EnumerateDirectories(root, ".godot", SearchOption.AllDirectories).FirstOrDefault();
        if (godotDirectory != null) return Directory.GetParent(godotDirectory)?.FullName ?? root;
        if (Directory.EnumerateFiles(root, "*.scn", SearchOption.AllDirectories).Any()
            || Directory.EnumerateFiles(root, "*.res", SearchOption.AllDirectories).Any()) return root;
        var apk = apks.FirstOrDefault(File.Exists);
        if (apk != null) return apk;
        throw new InvalidOperationException("没有找到可交给 GDRETools 的 PCK、project.godot、.godot 或场景资源目录。");
    }

    internal static string? NormalizeTemporaryKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = value.Trim();
        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) key = key[2..];
        if (!Regex.IsMatch(key, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("加密密钥必须是 64 个十六进制字符（256 位）。");
        return key.ToUpperInvariant();
    }

    private static async Task PersistRecoveryAsync(DirectoryAnalysis analysis, EngineRecoveryResult recovery,
        CancellationToken cancellationToken)
    {
        var root = analysis.TaskRoot ?? Directory.GetParent(recovery.OutputDirectory)?.FullName ?? recovery.OutputDirectory;
        var path = Path.Combine(root, "engine-recovery.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recovery, AnalysisPipeline.JsonOptions),
            new UTF8Encoding(false), cancellationToken);
        if (analysis.TaskRoot == null) return;
        var manifest = await TaskManifestStore.TryLoadAsync(analysis.TaskRoot, cancellationToken);
        if (manifest == null) return;
        await TaskManifestStore.SaveAsync(manifest with
        {
            SchemaVersion = 3,
            Engine = recovery.Engine,
            EngineVersion = recovery.EngineVersion?.DisplayVersion,
            EngineVersionConfidence = recovery.EngineVersion?.Confidence,
            RecoveryDirectory = recovery.OutputDirectory,
            RecoveryTool = recovery.ToolName,
            CurrentStage = WorkflowStage.ReadyForEngineTool,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private static void EnsureDiskSpace(string path, long required)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root == null) return;
        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < required + DiskReserve)
            throw new IOException($"磁盘空间不足：至少需要约 {AnalysisPipeline.FormatBytes(required + DiskReserve)} 可用空间。");
    }

    private static long SumFileLengths(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var path in paths.Where(File.Exists))
        {
            var length = new FileInfo(path).Length;
            if (long.MaxValue - total < length) return long.MaxValue;
            total += length;
        }
        return total;
    }

    private static string CreateUniqueDirectoryPath(string parent, string name)
    {
        var candidate = Path.Combine(parent, name);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        return Path.Combine(parent, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}");
    }

    private static string MakeUniqueFilePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 2;
        while (File.Exists(candidate)) candidate = Path.Combine(directory, $"{stem}_{index++}{extension}");
        return candidate;
    }

    private static void LinkOrCopy(string source, string destination)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !NativeMethods.CreateHardLink(destination, source, IntPtr.Zero))
                throw new IOException();
        }
        catch { File.Copy(source, destination, false); }
    }

    private static string SafeName(string value) => AnalysisPipeline.SanitizeFileName(value);
    private static string TrimProgress(string value) => value.Length <= 180 ? value : value[..177] + "…";
    public void Dispose() => (_tools as IDisposable)?.Dispose();

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true,
            CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
    }
}
