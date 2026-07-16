using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GooglePlayApkDownloader;

internal sealed record EngineIntelligenceResult(
    EngineDetectionInfo Detection,
    EngineVersionInfo Version,
    ScriptRuntimeInfo ScriptRuntime,
    RecoveryReadiness Readiness);

internal static partial class EngineIntelligenceAnalyzer
{
    private static readonly EnumerationOptions SafeEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static async Task<EngineIntelligenceResult> AnalyzeAsync(
        string inputDirectory,
        string? taskRoot,
        IReadOnlyList<string> relativeFiles,
        IProgress<WorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var detection = AnalysisPipeline.DetectEngineInfo(relativeFiles);
        var scriptRuntime = DetectScriptRuntime(detection.Engine, relativeFiles);
        var readiness = DetectReadiness(detection.Engine, relativeFiles);
        progress?.Report(new WorkflowProgress(WorkflowStage.AnalyzingFiles, "识别引擎版本与兼容信息", null));
        var version = detection.Engine switch
        {
            GameEngine.Godot => await AnalyzeGodotAsync(inputDirectory, taskRoot, relativeFiles, cancellationToken),
            GameEngine.Unity => await AnalyzeUnityAsync(inputDirectory, relativeFiles, cancellationToken),
            GameEngine.Unreal => await AnalyzeUnrealAsync(inputDirectory, relativeFiles, cancellationToken),
            _ => EngineVersionInfo.Unknown
        };
        return new EngineIntelligenceResult(detection, version, scriptRuntime, readiness);
    }

    public static async Task<EngineVersionInfo> EnrichGodotRecoveryAsync(
        EngineVersionInfo current,
        string outputDirectory,
        string? logPath,
        CancellationToken cancellationToken = default)
    {
        var evidence = current.Evidence.ToList();
        var warnings = current.Warnings.ToList();
        string? contentVersion = current.ContentVersion;
        string? bytecodeVersion = current.BytecodeVersion;

        var project = Directory.Exists(outputDirectory)
            ? Directory.EnumerateFiles(outputDirectory, "project.godot", SafeEnumeration).FirstOrDefault()
            : null;
        if (project != null)
        {
            var featureVersion = await ReadGodotProjectFeatureAsync(project, cancellationToken);
            if (featureVersion != null)
            {
                contentVersion ??= featureVersion;
                AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.RecoveredProject, featureVersion,
                    project, DetectionConfidence.Approximate, "project.godot 的 config/features 只保证主次版本兼容。"));
            }
        }

        if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
        {
            var log = await File.ReadAllTextAsync(logPath, cancellationToken);
            var engineMatch = GdreEngineRegex().Match(log);
            if (engineMatch.Success)
            {
                contentVersion = engineMatch.Groups["version"].Value;
                AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.Content, contentVersion, logPath,
                    DetectionConfidence.High, "GDRETools 检测的资源引擎版本。"));
            }
            var bytecodeMatch = GdreBytecodeRegex().Match(log);
            if (bytecodeMatch.Success)
            {
                bytecodeVersion = bytecodeMatch.Groups["version"].Value;
                AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.Bytecode, bytecodeVersion, logPath,
                    DetectionConfidence.High, "GDRETools 检测的 GDScript 字节码版本。"));
            }
        }

        AddVersionConflictWarnings(current.RuntimeVersion, contentVersion, bytecodeVersion, warnings);
        var confidence = warnings.Any(warning => warning.Contains("不同", StringComparison.Ordinal))
            ? DetectionConfidence.Conflict
            : current.Confidence != DetectionConfidence.Unknown
                ? current.Confidence
                : HighestConfidence(evidence);
        var recommended = current.RuntimeVersion ?? contentVersion ?? bytecodeVersion;
        return new EngineVersionInfo(current.RuntimeVersion, contentVersion, bytecodeVersion, recommended,
            current.BuildFlavor, confidence, evidence, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static async Task<RecoverySummary> BuildRecoverySummaryAsync(
        string outputDirectory,
        string? logPath,
        int processedContainers,
        int failedContainers,
        CancellationToken cancellationToken = default)
    {
        long bytes = 0;
        var files = 0;
        if (Directory.Exists(outputDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(outputDirectory, "*", SafeEnumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files++;
                var length = new FileInfo(file).Length;
                bytes = bytes > long.MaxValue - length ? long.MaxValue : bytes + length;
                if (files % 500 == 0) await Task.Yield();
            }
        }

        var warnings = new List<string>();
        var warningCount = 0;
        var errorCount = failedContainers;
        if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(logPath, cancellationToken))
            {
                if (line.Contains("warning", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("warn:", StringComparison.OrdinalIgnoreCase))
                {
                    warningCount++;
                    if (warnings.Count < 20) warnings.Add(TrimDiagnostic(line));
                }
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    errorCount++;
            }
        }
        if (failedContainers > 0) warnings.Insert(0, $"{failedContainers} 个容器检查或解包失败。");
        return new RecoverySummary(files, bytes, warningCount, errorCount,
            processedContainers, failedContainers, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static async Task<EngineVersionInfo> AnalyzeGodotAsync(
        string root,
        string? taskRoot,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var evidence = new List<VersionEvidence>();
        var warnings = new List<string>();
        string? runtime = null;
        string? content = null;
        string? bytecode = null;
        string? flavor = null;

        foreach (var relative in files.Where(path => Path.GetFileName(path).Contains("godot", StringComparison.OrdinalIgnoreCase)
                                                      && path.EndsWith(".so", StringComparison.OrdinalIgnoreCase)))
        {
            var full = ToFullPath(root, relative);
            var match = await FindAsciiMatchAsync(full, GodotRuntimeRegex(), cancellationToken);
            if (match == null) continue;
            runtime = match.Groups["version"].Value;
            flavor = match.Groups["flavor"].Success ? match.Groups["flavor"].Value.Trim('.') : null;
            AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.Runtime, runtime, relative,
                DetectionConfidence.Exact, match.Value));
            break;
        }

        foreach (var relative in files.Where(path => path.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)))
        {
            var pckVersion = await ReadGodotPckVersionAsync(ToFullPath(root, relative), cancellationToken);
            if (pckVersion == null) continue;
            content ??= pckVersion;
            AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.Content, pckVersion, relative,
                DetectionConfidence.High, "Godot PCK 头声明的引擎版本。"));
        }

        foreach (var relative in files.Where(path => path.EndsWith("project.godot", StringComparison.OrdinalIgnoreCase)))
        {
            var projectVersion = await ReadGodotProjectFeatureAsync(ToFullPath(root, relative), cancellationToken);
            if (projectVersion == null) continue;
            content ??= projectVersion;
            AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.Content, projectVersion, relative,
                DetectionConfidence.Approximate, "config/features 仅声明项目格式主次版本。"));
        }

        var gdreLog = taskRoot == null ? null : Path.Combine(taskRoot, "Logs", "gdre-tools.log");
        if (gdreLog != null && File.Exists(gdreLog))
        {
            var log = await File.ReadAllTextAsync(gdreLog, cancellationToken);
            var engineMatch = GdreEngineRegex().Match(log);
            if (engineMatch.Success)
            {
                content ??= engineMatch.Groups["version"].Value;
                AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.Content,
                    engineMatch.Groups["version"].Value, gdreLog, DetectionConfidence.High, "GDRETools 恢复日志。"));
            }
            var bytecodeMatch = GdreBytecodeRegex().Match(log);
            if (bytecodeMatch.Success)
            {
                bytecode = bytecodeMatch.Groups["version"].Value;
                AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.Bytecode, bytecode, gdreLog,
                    DetectionConfidence.High, "GDRETools GDScript 字节码检测。"));
            }
        }

        AddVersionConflictWarnings(runtime, content, bytecode, warnings);
        var confidence = warnings.Count > 0
            ? DetectionConfidence.Conflict
            : runtime != null ? DetectionConfidence.Exact : HighestConfidence(evidence);
        return new EngineVersionInfo(runtime, content, bytecode, runtime ?? content ?? bytecode,
            flavor, confidence, evidence, warnings);
    }

    private static async Task<EngineVersionInfo> AnalyzeUnityAsync(
        string root,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var evidence = new List<VersionEvidence>();
        string? runtime = null;
        string? content = null;
        foreach (var relative in files.Where(IsUnityVersionCandidate)
                     .OrderBy(UnityCandidatePriority).ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var match = await FindAsciiMatchAsync(ToFullPath(root, relative), UnityVersionRegex(), cancellationToken);
            if (match == null) continue;
            var version = match.Groups["version"].Value;
            var isRuntime = Path.GetFileName(relative).Equals("libunity.so", StringComparison.OrdinalIgnoreCase);
            if (isRuntime) runtime ??= version; else content ??= version;
            AddEvidence(evidence, new VersionEvidence(
                isRuntime ? VersionEvidenceRole.Runtime : VersionEvidenceRole.Content,
                version, relative, isRuntime ? DetectionConfidence.Exact : DetectionConfidence.High,
                isRuntime ? "Unity 原生运行时版本。" : "Unity 序列化文件或 UnityFS 头版本。"));
        }
        var warnings = new List<string>();
        if (runtime != null && content != null && !VersionsCompatible(runtime, content))
            warnings.Add($"Unity 运行时版本 {runtime} 与资源版本 {content} 不同，请按证据文件复核。 ");
        var confidence = warnings.Count > 0
            ? DetectionConfidence.Conflict
            : runtime != null ? DetectionConfidence.Exact : HighestConfidence(evidence);
        return new EngineVersionInfo(runtime, content, null, runtime ?? content, null,
            confidence, evidence, warnings);
    }

    private static async Task<EngineVersionInfo> AnalyzeUnrealAsync(
        string root,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var evidence = new List<VersionEvidence>();
        string? runtime = null;
        string? content = null;

        foreach (var relative in files.Where(path => Path.GetFileName(path).Equals("Build.version", StringComparison.OrdinalIgnoreCase)))
        {
            var version = await ReadUnrealBuildVersionAsync(ToFullPath(root, relative), cancellationToken);
            if (version == null) continue;
            runtime ??= version;
            AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.Runtime, version, relative,
                DetectionConfidence.Exact, "Unreal Build.version。"));
        }

        foreach (var relative in files.Where(path =>
                     Path.GetFileName(path).Equals("libUE4.so", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(path).Equals("libUnreal.so", StringComparison.OrdinalIgnoreCase)))
        {
            var full = ToFullPath(root, relative);
            var branch = await FindAsciiMatchAsync(full, UnrealBranchRegex(), cancellationToken);
            var named = branch == null
                ? await FindAsciiMatchAsync(full, UnrealNamedVersionRegex(), cancellationToken)
                : null;
            var match = branch ?? named;
            if (match == null) continue;
            runtime ??= match.Groups["version"].Value;
            AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.Runtime,
                match.Groups["version"].Value, relative, DetectionConfidence.High, match.Value));
        }

        if (files.Any(path => path.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)))
        {
            content = "UE5.x（IoStore）";
            AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.Container, content, "*.utoc/*.ucas",
                DetectionConfidence.Approximate, "IoStore 表明 UE5 系列，但不足以确定补丁版本。"));
        }
        else if (files.Any(path => path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
        {
            content = files.Any(path => Path.GetFileName(path).Equals("libUE4.so", StringComparison.OrdinalIgnoreCase))
                ? "UE4.x（PAK）" : "UE4/UE5（PAK）";
            AddEvidence(evidence, new VersionEvidence(VersionEvidenceRole.Container, content, "*.pak",
                DetectionConfidence.Approximate, "PAK 格式只能提供兼容系列信息。"));
        }
        var confidence = runtime != null ? HighestConfidence(evidence.Where(item => item.Role == VersionEvidenceRole.Runtime))
            : HighestConfidence(evidence);
        return new EngineVersionInfo(runtime, content, null, runtime ?? content, null,
            confidence, evidence, []);
    }

    internal static ScriptRuntimeInfo DetectScriptRuntime(GameEngine engine, IReadOnlyList<string> files)
    {
        var normalized = files.Select(path => path.Replace('\\', '/').ToLowerInvariant()).ToArray();
        return engine switch
        {
            GameEngine.Unity when normalized.Any(path => path.EndsWith("libil2cpp.so")) =>
                new ScriptRuntimeInfo("IL2CPP", "资源交给 AssetRipper；代码分析需要 libil2cpp.so 与 global-metadata.dat。",
                    Evidence: ["libil2cpp.so"]),
            GameEngine.Unity when normalized.Any(path => path.EndsWith("assembly-csharp.dll") || path.Contains("/managed/") && path.EndsWith(".dll")) =>
                new ScriptRuntimeInfo("Mono", "可使用 AssetRipper 恢复资源，并使用 ILSpy/dnSpy 查看 Managed DLL。",
                    Evidence: ["Managed DLL"]),
            GameEngine.Godot => DetectGodotRuntime(normalized),
            GameEngine.Unreal when normalized.Any(path => path.EndsWith(".uasset") || path.EndsWith(".umap")) =>
                new ScriptRuntimeInfo("C++ / Blueprint 资源", "使用匹配的 UE/FModel 版本浏览资源；静态文件不足以恢复原始 C++ 源码。"),
            GameEngine.Unreal => new ScriptRuntimeInfo("原生 C++ / Blueprint（待解包）", "先处理 PAK/IoStore，再判断 Blueprint 资源。"),
            _ => ScriptRuntimeInfo.Unknown
        };
    }

    internal static RecoveryReadiness DetectReadiness(GameEngine engine, IReadOnlyList<string> files)
    {
        var normalized = files.Select(path => path.Replace('\\', '/').ToLowerInvariant()).ToArray();
        var present = new List<string>();
        var missing = new List<string>();
        var warnings = new List<string>();
        switch (engine)
        {
            case GameEngine.Unity:
                var hasUnityData = normalized.Any(path => Path.GetFileName(path) == "globalgamemanagers"
                    || path.EndsWith(".assets") || path.EndsWith(".bundle") || path.EndsWith(".unity3d"));
                var hasIl2Cpp = normalized.Any(path => path.EndsWith("libil2cpp.so"));
                var hasMetadata = normalized.Any(path => path.EndsWith("global-metadata.dat"));
                if (hasUnityData) present.Add("Unity 序列化资源"); else missing.Add("Unity 序列化资源");
                if (hasIl2Cpp) present.Add("libil2cpp.so");
                if (hasMetadata) present.Add("global-metadata.dat");
                if (hasIl2Cpp != hasMetadata)
                {
                    missing.Add(hasIl2Cpp ? "global-metadata.dat" : "libil2cpp.so");
                    warnings.Add("IL2CPP 关键文件不成对，代码元数据分析会受限。 ");
                }
                return BuildReadiness(hasUnityData, missing, present, warnings,
                    hasUnityData ? "Unity 资源可交给 AssetRipper。" : "缺少可识别的 Unity 资源数据。");

            case GameEngine.Godot:
                var hasGodotInput = normalized.Any(path => path.EndsWith(".pck") || path.Contains("/.godot/")
                    || path.StartsWith(".godot/") || path.EndsWith(".scn") || path.EndsWith(".res")
                    || path.EndsWith("project.godot") || path.EndsWith("project.binary"));
                if (hasGodotInput) present.Add("Godot 项目资源/PCK"); else missing.Add("Godot 项目资源或 PCK");
                if (normalized.Any(path => Path.GetFileName(path).Contains("godot") && path.EndsWith(".so")))
                    present.Add("Godot 原生运行时");
                return BuildReadiness(hasGodotInput, missing, present, warnings,
                    hasGodotInput ? "已找到可交给 GDRETools 的 Godot 输入。" : "只检测到运行时，缺少可恢复项目资源。");

            case GameEngine.Unreal:
                var paks = normalized.Where(path => path.EndsWith(".pak")).ToArray();
                var utocs = normalized.Where(path => path.EndsWith(".utoc")).ToArray();
                var ucas = normalized.Where(path => path.EndsWith(".ucas")).ToArray();
                if (paks.Length > 0) present.Add($"PAK × {paks.Length}");
                if (utocs.Length > 0) present.Add($"UTOC × {utocs.Length}");
                foreach (var utoc in utocs)
                {
                    var stem = Path.GetFileNameWithoutExtension(utoc);
                    if (!ucas.Any(path => Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase)))
                        missing.Add($"{stem}.ucas");
                }
                if (paks.Length == 0 && utocs.Length == 0) missing.Add("PAK 或 UTOC/UCAS");
                var usable = paks.Length > 0 || utocs.Length > 0 && missing.Count == 0;
                return BuildReadiness(usable, missing, present, warnings,
                    usable ? "Unreal 容器已准备，可检查或解包。" : "Unreal 容器不完整。");

            default:
                return RecoveryReadiness.Unknown;
        }
    }

    private static RecoveryReadiness BuildReadiness(bool hasCoreInput, List<string> missing,
        List<string> present, List<string> warnings, string summary)
    {
        var status = !hasCoreInput ? RecoveryReadinessStatus.Blocked
            : missing.Count > 0 ? RecoveryReadinessStatus.Partial
            : RecoveryReadinessStatus.Ready;
        return new RecoveryReadiness(status, summary, present, missing, warnings);
    }

    private static ScriptRuntimeInfo DetectGodotRuntime(IReadOnlyList<string> files)
    {
        var hasCSharp = files.Any(path => path.Contains("godotsharp") || path.EndsWith(".cs")
            || path.EndsWith(".dll") && !path.EndsWith("resources.dll"));
        var hasGdScript = files.Any(path => path.EndsWith(".gd") || path.EndsWith(".gdc"))
            || files.Any(path => path.Contains("/.godot/") || path.StartsWith(".godot/"));
        var hasExtension = files.Any(path => path.EndsWith(".gdextension"));
        if (hasCSharp && (hasGdScript || hasExtension))
            return new ScriptRuntimeInfo("C#/.NET + GDScript/GDExtension", "推荐使用相同版本的 Godot .NET 版。", true);
        if (hasCSharp) return new ScriptRuntimeInfo("C#/.NET", "推荐使用相同版本的 Godot .NET 版。", true);
        if (hasExtension) return new ScriptRuntimeInfo("GDScript / GDExtension", "推荐使用相同版本的标准版 Godot，并准备对应原生扩展。 ");
        if (hasGdScript) return new ScriptRuntimeInfo("GDScript", "推荐使用相同版本的标准版 Godot。 ");
        return new ScriptRuntimeInfo("Godot 脚本类型未知", "优先尝试相同版本的标准版 Godot。 ");
    }

    private static bool IsUnityVersionCandidate(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase)
               || name.Equals("libunity.so", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ProjectVersion.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static int UnityCandidatePriority(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals("ProjectVersion.txt", StringComparison.OrdinalIgnoreCase)) return 1;
        if (path.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static async Task<string?> ReadGodotPckVersionAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[20];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (await stream.ReadAsync(buffer, cancellationToken) < buffer.Length) return null;
        if (Encoding.ASCII.GetString(buffer, 0, 4) != "GDPC") return null;
        var major = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8, 4));
        var minor = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12, 4));
        var patch = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16, 4));
        if (major is < 2 or > 9 || minor > 99 || patch > 999) return null;
        return $"{major}.{minor}.{patch}";
    }

    private static async Task<string?> ReadGodotProjectFeatureAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 32 * 1024, false);
        for (var i = 0; i < 300 && !reader.EndOfStream; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null || !line.Contains("config/features", StringComparison.OrdinalIgnoreCase)) continue;
            var match = GodotFeatureRegex().Match(line);
            if (match.Success) return match.Groups["version"].Value;
        }
        return null;
    }

    private static async Task<string?> ReadUnrealBuildVersionAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("MajorVersion", out var major)
                || !root.TryGetProperty("MinorVersion", out var minor)) return null;
            var patch = root.TryGetProperty("PatchVersion", out var patchNode) && patchNode.TryGetInt32(out var patchValue)
                ? patchValue : 0;
            return $"{major.GetInt32()}.{minor.GetInt32()}.{patch}";
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private static async Task<Match?> FindAsciiMatchAsync(string path, Regex regex, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var buffer = new byte[128 * 1024];
        var current = new StringBuilder(256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                if (value is >= 0x20 and <= 0x7E)
                {
                    if (current.Length < 1024) current.Append((char)value);
                    continue;
                }
                var match = MatchAndClear(current, regex);
                if (match != null) return match;
            }
        }
        return MatchAndClear(current, regex);
    }

    private static Match? MatchAndClear(StringBuilder current, Regex regex)
    {
        if (current.Length == 0) return null;
        var value = current.ToString();
        current.Clear();
        var match = regex.Match(value);
        return match.Success ? match : null;
    }

    private static void AddVersionConflictWarnings(string? runtime, string? content, string? bytecode, List<string> warnings)
    {
        if (runtime != null && content != null && !VersionsCompatible(runtime, content))
            warnings.Add($"运行时版本 {runtime} 与资源版本 {content} 不同。 ");
        if (runtime != null && bytecode != null && !VersionsCompatible(runtime, bytecode))
            warnings.Add($"运行时版本 {runtime} 与脚本字节码版本 {bytecode} 不同。 ");
    }

    private static bool VersionsCompatible(string left, string right)
    {
        var leftMatch = NumericVersionRegex().Match(left);
        var rightMatch = NumericVersionRegex().Match(right);
        return leftMatch.Success && rightMatch.Success
               && leftMatch.Groups["version"].Value.Equals(rightMatch.Groups["version"].Value, StringComparison.OrdinalIgnoreCase);
    }

    private static DetectionConfidence HighestConfidence(IEnumerable<VersionEvidence> evidence)
    {
        var values = evidence.Select(item => item.Confidence).Where(value => value != DetectionConfidence.Conflict).ToArray();
        return values.Length == 0 ? DetectionConfidence.Unknown : values.Max();
    }

    private static void AddEvidence(List<VersionEvidence> list, VersionEvidence value)
    {
        if (!list.Any(item => item.Role == value.Role && item.Value == value.Value
                              && item.Source.Equals(value.Source, StringComparison.OrdinalIgnoreCase)))
            list.Add(value);
    }

    private static string ToFullPath(string root, string relative) =>
        Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string TrimDiagnostic(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 240 ? trimmed : trimmed[..237] + "…";
    }

    [GeneratedRegex(@"Godot Engine v(?<version>\d+\.\d+(?:\.\d+)?)(?<flavor>(?:\.[A-Za-z0-9_+\-]+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GodotRuntimeRegex();

    [GeneratedRegex(@"(?<!\d)(?<version>(?:(?:20\d{2})|6000)\.\d+\.\d+[abcfp]\d+(?:\.\d+)?)(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnityVersionRegex();

    [GeneratedRegex(@"\+\+UE[45]\+Release-(?<version>[45]\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnrealBranchRegex();

    [GeneratedRegex(@"Unreal Engine\s+(?<version>[45]\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnrealNamedVersionRegex();

    [GeneratedRegex(@"Detected Engine Version:\s*(?<version>\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GdreEngineRegex();

    [GeneratedRegex(@"Detected Bytecode Revision:\s*(?<version>\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GdreBytecodeRegex();

    [GeneratedRegex("\"(?<version>\\d+\\.\\d+(?:\\.\\d+)?)\"")]
    private static partial Regex GodotFeatureRegex();

    [GeneratedRegex(@"(?<version>\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex NumericVersionRegex();
}
