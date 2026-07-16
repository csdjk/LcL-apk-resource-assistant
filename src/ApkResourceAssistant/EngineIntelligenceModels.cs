namespace GooglePlayApkDownloader;

internal enum DetectionConfidence
{
    Unknown,
    Approximate,
    High,
    Exact,
    Conflict
}

internal enum VersionEvidenceRole
{
    Runtime,
    Content,
    Bytecode,
    Container,
    RecoveredProject
}

internal sealed record VersionEvidence(
    VersionEvidenceRole Role,
    string Value,
    string Source,
    DetectionConfidence Confidence,
    string? Note = null);

internal sealed record EngineDetectionInfo(
    GameEngine Engine,
    int Score,
    DetectionConfidence Confidence,
    IReadOnlyList<string> Evidence)
{
    public static EngineDetectionInfo Unknown { get; } = new(
        GameEngine.Unknown, 0, DetectionConfidence.Unknown, []);
}

internal sealed record EngineVersionInfo(
    string? RuntimeVersion,
    string? ContentVersion,
    string? BytecodeVersion,
    string? RecommendedEditorVersion,
    string? BuildFlavor,
    DetectionConfidence Confidence,
    IReadOnlyList<VersionEvidence> Evidence,
    IReadOnlyList<string> Warnings)
{
    public string DisplayVersion => RuntimeVersion != null
        ? string.IsNullOrWhiteSpace(BuildFlavor) ? RuntimeVersion : $"{RuntimeVersion} {BuildFlavor}"
        : ContentVersion ?? BytecodeVersion ?? "未知";

    public static EngineVersionInfo Unknown { get; } = new(
        null, null, null, null, null, DetectionConfidence.Unknown, [], []);
}

internal sealed record ScriptRuntimeInfo(
    string Kind,
    string Recommendation,
    bool RequiresDotNet = false,
    IReadOnlyList<string>? Evidence = null)
{
    public static ScriptRuntimeInfo Unknown { get; } = new("未知/不适用", "没有识别到明确的脚本运行时。");
}

internal enum RecoveryReadinessStatus
{
    Unknown,
    Ready,
    Partial,
    Blocked
}

internal sealed record RecoveryReadiness(
    RecoveryReadinessStatus Status,
    string Summary,
    IReadOnlyList<string> Present,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Warnings)
{
    public static RecoveryReadiness Unknown { get; } = new(
        RecoveryReadinessStatus.Unknown, "尚未判断恢复准备状态。", [], [], []);
}

internal sealed record RecoverySummary(
    int OutputFiles,
    long OutputBytes,
    int WarningCount,
    int ErrorCount,
    int ProcessedContainers,
    int FailedContainers,
    IReadOnlyList<string> Warnings);
