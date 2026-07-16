using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GooglePlayApkDownloader;

var root = Path.Combine(Path.GetTempPath(), "ApkResourceAssistantTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await TestUnitySplitAsync(root);
    await TestZipTraversalAsync(root);
    await TestCorruptApkAsync(root);
    await TestExternalApkCreatesNewTaskWithoutChangingSourceAsync(root);
    await TestDownloadedTaskContinuesInPlaceAsync(root);
    await TestExistingDirectoryScanIsReadOnlyAsync(root);
    await TestV3TaskManifestCompatibilityAsync(root);
    await TestV3SettingsMigrationAsync(root);
    await TestAssetRipperAutoLoadAndReuseAsync(root);
    await TestAssetRipperFallbackAsync(root);
    await TestExternalToolVerificationAsync(root);
    await TestExternalToolZipTraversalAsync(root);
    await TestExternalToolDownloadFailureAsync(root);
    await TestProcessRedactionAndCancellationAsync(root);
    await TestGodotRecoveryAsync(root);
    await TestGodotRecoveryFallbackAsync(root);
    await TestUnrealPakRecoveryAsync(root);
    await TestUnrealIoStoreRecoveryAsync(root);
    await TestUnrealEncryptedAndDamagedContainerAsync(root);
    await TestRecoveryCancellationAsync(root);
    await TestGodotSampleAsync(root);
    Console.WriteLine("ALL TESTS PASSED");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

static async Task TestExternalToolVerificationAsync(string root)
{
    var archivePath = Path.Combine(root, "tool-fixture.zip");
    CreateZip(archivePath, new Dictionary<string, string> { ["tool.exe"] = "fixture" });
    var bytes = await File.ReadAllBytesAsync(archivePath);
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    var spec = new ExternalToolSpec(ExternalToolId.GdreTools, "fixture", "1.0", "https://fixture/tool.zip", hash, "tool.exe");
    using var http = new HttpClient(new StaticContentHandler(bytes));
    using var manager = new ExternalToolManager(Path.Combine(root, "tool-cache"), http,
        new Dictionary<ExternalToolId, ExternalToolSpec> { [ExternalToolId.GdreTools] = spec });
    var installed = await manager.EnsureInstalledAsync(ExternalToolId.GdreTools);
    Assert(File.Exists(installed.ExecutablePath), "verified tool installed");
    await File.WriteAllTextAsync(installed.ExecutablePath, "tampered");
    installed = await manager.EnsureInstalledAsync(ExternalToolId.GdreTools);
    Assert(File.ReadAllText(installed.ExecutablePath) == "fixture", "tampered tool cache repaired");

    var bad = spec with { ArchiveSha256 = new string('0', 64) };
    using var badHttp = new HttpClient(new StaticContentHandler(bytes));
    using var badManager = new ExternalToolManager(Path.Combine(root, "bad-tool-cache"), badHttp,
        new Dictionary<ExternalToolId, ExternalToolSpec> { [ExternalToolId.GdreTools] = bad });
    var threw = false;
    try { await badManager.EnsureInstalledAsync(ExternalToolId.GdreTools); }
    catch (InvalidDataException) { threw = true; }
    Assert(threw, "tool SHA-256 mismatch rejected");
    Console.WriteLine("PASS external tool verification");
}

static async Task TestV3TaskManifestCompatibilityAsync(string root)
{
    var taskRoot = Path.Combine(root, "v3-manifest");
    Directory.CreateDirectory(taskRoot);
    await File.WriteAllTextAsync(Path.Combine(taskRoot, "task.json"), $$"""
    {
      "SchemaVersion": 1,
      "TaskId": "legacy",
      "PackageName": "com.example.legacy",
      "Mode": "OpenInAssetRipper",
      "Status": "Completed",
      "CurrentStage": "ReadyForAssetRipper",
      "JobRoot": "{{taskRoot.Replace("\\", "\\\\")}}",
      "OriginalApksDirectory": "{{Path.Combine(taskRoot, "Original_APKs").Replace("\\", "\\\\")}}"
    }
    """);
    var manifest = await TaskManifestStore.TryLoadAsync(taskRoot);
    Assert(manifest != null && (int)manifest.Mode == (int)WorkflowMode.RecoverResources,
        "v3 OpenInAssetRipper manifest migrates to recovery stage");
    Console.WriteLine("PASS v3 task manifest compatibility");
}

static async Task TestV3SettingsMigrationAsync(string root)
{
    var path = Path.Combine(root, "settings-v3.json");
    await File.WriteAllTextAsync(path, """
    {
      "SchemaVersion": 3,
      "Email": "fixture@example.com",
      "OutputDir": "C:\\APK",
      "Split": false,
      "Source": 2,
      "AssetRipperPath": "C:\\Tools\\AssetRipper.exe",
      "RememberCredentials": false,
      "LastMode": 2
    }
    """);
    var settings = SettingsStore.LoadFromFile(path);
    Assert(settings.SchemaVersion == 3 && settings.Source == 2 && !settings.Split,
        "v3 settings values loaded");
    Assert(settings.LastMode == (int)WorkflowMode.RecoverResources &&
           settings.AssetRipperPath?.EndsWith("AssetRipper.exe", StringComparison.OrdinalIgnoreCase) == true,
        "v3 settings migrate third stage and AssetRipper path");
    Console.WriteLine("PASS v3 settings migration");
}

static async Task TestExternalToolDownloadFailureAsync(string root)
{
    var spec = new ExternalToolSpec(ExternalToolId.Repak, "fixture", "1.0", "https://fixture/fail.zip",
        new string('0', 64), "tool.exe");
    using var http = new HttpClient(new StatusCodeHandler(HttpStatusCode.ServiceUnavailable));
    using var manager = new ExternalToolManager(Path.Combine(root, "failed-download-cache"), http,
        new Dictionary<ExternalToolId, ExternalToolSpec> { [ExternalToolId.Repak] = spec });
    var threw = false;
    try { await manager.EnsureInstalledAsync(ExternalToolId.Repak); }
    catch (HttpRequestException) { threw = true; }
    Assert(threw, "external tool HTTP failure reported");
    Assert(!Directory.EnumerateFiles(Path.Combine(root, "failed-download-cache"), "*.download", SearchOption.AllDirectories).Any(),
        "failed tool download leaves no partial archive");
    Console.WriteLine("PASS external tool download failure cleanup");
}

static Task TestExternalToolZipTraversalAsync(string root)
{
    var archive = Path.Combine(root, "malicious-tool.zip");
    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(zip.CreateEntry("../tool.exe").Open());
        writer.Write("bad");
    }
    var destination = Path.Combine(root, "safe-tool-extract");
    Directory.CreateDirectory(destination);
    var threw = false;
    try { ExternalToolManager.ExtractZipSafely(archive, destination, CancellationToken.None); }
    catch (InvalidDataException) { threw = true; }
    Assert(threw && !File.Exists(Path.Combine(root, "tool.exe")), "external tool ZIP traversal rejected");
    Console.WriteLine("PASS external tool ZIP traversal protection");
    return Task.CompletedTask;
}

static async Task TestProcessRedactionAndCancellationAsync(string root)
{
    const string secret = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    var log = Path.Combine(root, "redaction.log");
    var runner = new ExternalProcessRunner();
    var result = await runner.RunAsync(new ExternalProcessRequest("powershell.exe",
        ["-NoProfile", "-Command", $"Write-Output '{secret}'"], root, log, [secret]));
    Assert(result.ExitCode == 0 && !result.StandardOutput.Contains(secret), "process output secret redacted");
    Assert(!File.ReadAllText(log).Contains(secret), "process log secret redacted");
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    var cancelled = false;
    try
    {
        await runner.RunAsync(new ExternalProcessRequest("powershell.exe",
            ["-NoProfile", "-Command", "Start-Sleep -Seconds 10"], root), cancellationToken: cts.Token);
    }
    catch (OperationCanceledException) { cancelled = true; }
    Assert(cancelled, "external process cancellation kills process tree");
    Console.WriteLine("PASS process redaction and cancellation");
}

static async Task TestGodotRecoveryAsync(string root)
{
    var input = Path.Combine(root, "godot-recovery", "Extracted");
    Directory.CreateDirectory(Path.Combine(input, "assetpack", "assets", ".godot"));
    await File.WriteAllTextAsync(Path.Combine(input, "assetpack", "assets", ".godot", "uid_cache.bin"), "cache");
    await File.WriteAllTextAsync(Path.Combine(input, "assetpack", "assets", "main.scn"), "scene");
    var tools = new FakeToolProvider(Path.Combine(root, "fake-tools"));
    var processes = new FakeRecoveryProcessRunner();
    var service = new EngineRecoveryService(new AnalysisService(), tools, processes);
    var key = new string('A', 64);
    var result = await service.RecoverAsync(new EngineRecoveryRequest(input, key));
    Assert(result.Engine == GameEngine.Godot && File.Exists(Path.Combine(result.OutputDirectory, "project.godot")),
        "GDRETools Godot recovery output");
    Assert(processes.Requests.Single().Arguments.Any(arg => arg.StartsWith("--recover=")), "GDRETools recover argument");
    Assert(!File.ReadAllText(Path.Combine(Directory.GetParent(result.OutputDirectory)!.FullName, "engine-recovery.json")).Contains(key),
        "Godot temporary key not persisted");
    Console.WriteLine("PASS Godot recovery dispatcher");
}

static async Task TestGodotRecoveryFallbackAsync(string root)
{
    var input = Path.Combine(root, "godot-fallback", "Extracted");
    Directory.CreateDirectory(Path.Combine(input, ".godot"));
    await File.WriteAllTextAsync(Path.Combine(input, ".godot", "uid_cache.bin"), "cache");
    var launched = 0;
    var service = new EngineRecoveryService(new AnalysisService(),
        new FakeToolProvider(Path.Combine(root, "fake-tools-godot-fallback")),
        new FakeRecoveryProcessRunner { ExitCode = 9 }, (_, _) => launched++);
    var result = await service.RecoverAsync(new EngineRecoveryRequest(input));
    Assert(result.Outcome == EngineRecoveryOutcome.ManualFallback && result.ToolLaunched && launched == 1,
        "GDRETools headless failure launches GUI fallback");
    Console.WriteLine("PASS Godot recovery GUI fallback");
}

static async Task TestUnrealPakRecoveryAsync(string root)
{
    var input = Path.Combine(root, "ue4-recovery", "Extracted");
    Directory.CreateDirectory(Path.Combine(input, "base", "assets"));
    await File.WriteAllTextAsync(Path.Combine(input, "base", "assets", "game.pak"), "pak");
    await File.WriteAllTextAsync(Path.Combine(input, "base", "libUE4.so"), "ue");
    var processes = new FakeRecoveryProcessRunner();
    var launched = 0;
    var service = new EngineRecoveryService(new AnalysisService(), new FakeToolProvider(Path.Combine(root, "fake-tools-ue4")),
        processes, (_, _) => launched++);
    var result = await service.RecoverAsync(new EngineRecoveryRequest(input));
    Assert(result.Engine == GameEngine.Unreal && result.ProcessedContainers == 1 && launched == 1,
        "UE4 PAK recovery and FModel launch");
    Assert(processes.Requests.Any(request => request.Arguments.Contains("info"))
           && processes.Requests.Any(request => request.Arguments.Contains("unpack")), "repak info and unpack commands");
    Console.WriteLine("PASS Unreal PAK recovery dispatcher");
}

static async Task TestUnrealEncryptedAndDamagedContainerAsync(string root)
{
    var input = Path.Combine(root, "ue-encrypted-damaged", "Extracted");
    Directory.CreateDirectory(input);
    await File.WriteAllTextAsync(Path.Combine(input, "encrypted.pak"), "damaged fixture");
    var key = new string('B', 64);
    var processes = new FakeRecoveryProcessRunner { ExitCode = 7 };
    var launched = 0;
    var service = new EngineRecoveryService(new AnalysisService(),
        new FakeToolProvider(Path.Combine(root, "fake-tools-ue-damaged")), processes, (_, _) => launched++);
    var result = await service.RecoverAsync(new EngineRecoveryRequest(input, key));
    Assert(result.Outcome == EngineRecoveryOutcome.ManualFallback && result.FailedContainers == 1 && launched == 1,
        "damaged encrypted PAK falls back to FModel");
    Assert(processes.Requests.Any(request => request.Arguments.Contains("--aes-key") && request.Arguments.Contains(key)),
        "temporary Unreal AES key passed to repak");
    var recoveryJson = Path.Combine(Directory.GetParent(result.OutputDirectory)!.FullName, "engine-recovery.json");
    Assert(File.Exists(recoveryJson) && !File.ReadAllText(recoveryJson).Contains(key),
        "Unreal temporary AES key not persisted");
    Console.WriteLine("PASS encrypted and damaged Unreal container fallback");
}

static async Task TestRecoveryCancellationAsync(string root)
{
    var input = Path.Combine(root, "cancel-recovery", "Extracted");
    Directory.CreateDirectory(Path.Combine(input, ".godot"));
    await File.WriteAllTextAsync(Path.Combine(input, ".godot", "uid_cache.bin"), "cache");
    var processes = new FakeRecoveryProcessRunner { BlockUntilCancelled = true };
    var service = new EngineRecoveryService(new AnalysisService(),
        new FakeToolProvider(Path.Combine(root, "fake-tools-cancel")), processes);
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    var cancelled = false;
    try { await service.RecoverAsync(new EngineRecoveryRequest(input), cancellationToken: cts.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Assert(cancelled && processes.Requests.Count == 1, "engine recovery cancellation reaches child process");
    Console.WriteLine("PASS engine recovery cancellation");
}

static async Task TestUnrealIoStoreRecoveryAsync(string root)
{
    var input = Path.Combine(root, "ue5-recovery", "Extracted");
    Directory.CreateDirectory(Path.Combine(input, "base", "assets"));
    await File.WriteAllTextAsync(Path.Combine(input, "base", "assets", "global.utoc"), "utoc");
    await File.WriteAllTextAsync(Path.Combine(input, "base", "assets", "global.ucas"), "ucas");
    var processes = new FakeRecoveryProcessRunner();
    var service = new EngineRecoveryService(new AnalysisService(), new FakeToolProvider(Path.Combine(root, "fake-tools-ue5")),
        processes, (_, _) => { });
    var result = await service.RecoverAsync(new EngineRecoveryRequest(input));
    Assert(result.ProcessedContainers == 1, "UE5 IoStore container count");
    Assert(processes.Requests.Any(request => request.Arguments.Contains("verify"))
           && processes.Requests.Any(request => request.Arguments.Contains("unpack")), "retoc verify and unpack commands");
    Console.WriteLine("PASS Unreal IoStore recovery dispatcher");
}

static async Task TestUnitySplitAsync(string root)
{
    var job = Path.Combine(root, "unity-job");
    var apkDir = Path.Combine(job, "Original_APKs");
    Directory.CreateDirectory(apkDir);
    CreateZip(Path.Combine(apkDir, "com.example.unity.apk"), new Dictionary<string, string>
    {
        ["assets/bin/Data/globalgamemanagers"] = "unity",
        ["assets/bin/Data/Managed/Metadata/global-metadata.dat"] = "metadata"
    });
    CreateZip(Path.Combine(apkDir, "com.example.unity.assetPackInstallTime.apk"), new Dictionary<string, string>
    {
        ["assets/aa/Android/game.bundle"] = "bundle"
    });
    CreateZip(Path.Combine(apkDir, "com.example.unity.config.arm64_v8a.apk"), new Dictionary<string, string>
    {
        ["lib/arm64-v8a/libil2cpp.so"] = "native",
        ["lib/arm64-v8a/libunity.so"] = "unity-native"
    });
    var result = await AnalysisPipeline.ExtractAndAnalyzeAsync("com.example.unity", "fixture", job);
    Assert(result.Engine == GameEngine.Unity, "Unity engine detection");
    Assert(result.ScriptingBackend == "IL2CPP", "IL2CPP detection");
    Assert(result.Splits.Count == 3, "Unity split count");
    Assert(result.KeyFiles.Any(x => x.EndsWith("libil2cpp.so")), "libil2cpp location");
    Assert(result.KeyFiles.Any(x => x.EndsWith("global-metadata.dat")), "metadata location");
    Assert(result.KeyFiles.Any(x => x.EndsWith("game.bundle")), "asset bundle location");
    Assert(File.Exists(result.JsonPath) && File.Exists(result.ReportPath), "reports created");
    Console.WriteLine("PASS Unity split fixture");
}

static async Task TestZipTraversalAsync(string root)
{
    var apk = Path.Combine(root, "traversal.apk");
    using (var archive = ZipFile.Open(apk, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("../escape.txt");
        await using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync("bad");
    }
    var threw = false;
    try { await AnalysisPipeline.ExtractApkSafelyAsync(apk, Path.Combine(root, "safe")); }
    catch (InvalidDataException) { threw = true; }
    Assert(threw, "ZIP traversal rejected");
    Assert(!File.Exists(Path.Combine(root, "escape.txt")), "ZIP traversal wrote nothing");
    Console.WriteLine("PASS ZIP traversal protection");
}

static async Task TestCorruptApkAsync(string root)
{
    var job = Path.Combine(root, "corrupt-job");
    var apkDir = Path.Combine(job, "Original_APKs");
    Directory.CreateDirectory(apkDir);
    await File.WriteAllTextAsync(Path.Combine(apkDir, "com.example.bad.apk"), "not a zip");
    var threw = false;
    try { await AnalysisPipeline.ExtractAndAnalyzeAsync("com.example.bad", "fixture", job); }
    catch (InvalidDataException) { threw = true; }
    Assert(threw, "corrupt APK rejected");
    Console.WriteLine("PASS corrupt APK handling");
}

static async Task TestGodotSampleAsync(string root)
{
    var configuredSample = Environment.GetEnvironmentVariable("APK_RESOURCE_ASSISTANT_GODOT_FIXTURE");
    var sample = string.IsNullOrWhiteSpace(configuredSample)
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "google-download-stack-test"))
        : Path.GetFullPath(configuredSample);
    if (!Directory.Exists(sample))
    {
        Console.WriteLine("SKIP Godot sample (fixture directory absent)");
        return;
    }
    var sourceApks = Directory.EnumerateFiles(sample, "*.apk", SearchOption.AllDirectories).ToList();
    if (sourceApks.Count == 0)
    {
        Console.WriteLine("SKIP Godot sample (no APKs)");
        return;
    }
    var job = Path.Combine(root, "godot-job");
    var apkDir = Path.Combine(job, "Original_APKs");
    Directory.CreateDirectory(apkDir);
    foreach (var apk in sourceApks) File.Copy(apk, Path.Combine(apkDir, Path.GetFileName(apk)));
    var result = await AnalysisPipeline.ExtractAndAnalyzeAsync("com.oakever.meowdoku", "Google Play fixture", job);
    Assert(result.Engine == GameEngine.Godot, "Godot engine detection");
    Assert(result.Splits.Count == 5, "Godot split count");
    Assert(Directory.EnumerateFiles(result.InputDirectory, "*.scn", SearchOption.AllDirectories).Any(), "Godot scenes extracted");
    Assert(File.ReadAllText(result.ReportPath).Contains("GDRETools"), "Godot recovery recommendation");
    Console.WriteLine("PASS real Godot split sample");
}

static async Task TestExternalApkCreatesNewTaskWithoutChangingSourceAsync(string root)
{
    var sourceDirectory = Path.Combine(root, "external-source");
    Directory.CreateDirectory(sourceDirectory);
    var sourceApk = Path.Combine(sourceDirectory, "com.example.external.apk");
    CreateZip(sourceApk, new Dictionary<string, string>
    {
        ["assets/bin/Data/globalgamemanagers"] = "unity-external",
        ["assets/bin/Data/Managed/Assembly-CSharp.dll"] = "managed"
    });
    var sourceBefore = CaptureFileSnapshot(sourceApk);
    var destination = Path.Combine(root, "external-jobs");
    var coordinator = new WorkflowCoordinator();

    var first = await coordinator.ExtractAndAnalyzeExternalAsync(
        "com.example.external", "本地测试 APK", destination, [sourceApk]);
    var second = await coordinator.ExtractAndAnalyzeExternalAsync(
        "com.example.external", "本地测试 APK", destination, [sourceApk]);

    Assert(CaptureFileSnapshot(sourceApk) == sourceBefore, "external APK source remains unchanged");
    Assert(!PathsEqual(first.JobRoot, second.JobRoot), "external APK repeated run creates new task");
    Assert(IsPathInside(destination, first.JobRoot) && IsPathInside(destination, second.JobRoot),
        "external APK tasks stay under destination root");
    Assert(first.Engine == GameEngine.Unity && second.Engine == GameEngine.Unity,
        "external APK tasks are analyzed");
    Assert(File.Exists(Path.Combine(first.JobRoot, "task.json"))
           && File.Exists(Path.Combine(second.JobRoot, "task.json")),
        "external APK task manifests created");
    Assert(Directory.EnumerateFiles(first.OriginalApksDirectory, "*.apk").Count() == 1,
        "external APK copied into first task");
    Assert(Directory.EnumerateFiles(second.OriginalApksDirectory, "*.apk").Count() == 1,
        "external APK copied into second task");
    Assert(CaptureFileSnapshot(Directory.EnumerateFiles(first.OriginalApksDirectory, "*.apk").Single()).Hash
           == sourceBefore.Hash,
        "external APK copied bytes match source");
    Console.WriteLine("PASS external APK source preservation and unique tasks");
}

static async Task TestDownloadedTaskContinuesInPlaceAsync(string root)
{
    var jobRoot = Path.Combine(root, "downloaded-jobs", "com.example.downloaded", "20260716-001000");
    var originalApks = Path.Combine(jobRoot, "Original_APKs");
    Directory.CreateDirectory(originalApks);
    var downloadedApk = Path.Combine(originalApks, "com.example.downloaded.apk");
    CreateZip(downloadedApk, new Dictionary<string, string>
    {
        ["assets/bin/Data/globalgamemanagers"] = "unity-downloaded",
        ["lib/arm64-v8a/libil2cpp.so"] = "native"
    });
    var apkBefore = CaptureFileSnapshot(downloadedApk);
    var coordinator = new WorkflowCoordinator();

    var result = await coordinator.ContinueDownloadedTaskAsync(
        "com.example.downloaded", "下载测试", jobRoot);

    Assert(PathsEqual(result.JobRoot, jobRoot), "downloaded task continues in same job root");
    Assert(PathsEqual(result.OriginalApksDirectory, originalApks),
        "downloaded task reuses Original_APKs");
    Assert(PathsEqual(result.InputDirectory, Path.Combine(jobRoot, "AssetRipper_Input")),
        "downloaded task creates input beside originals");
    Assert(CaptureFileSnapshot(downloadedApk) == apkBefore, "downloaded APK remains unchanged");
    Assert(result.Engine == GameEngine.Unity && result.ScriptingBackend == "IL2CPP",
        "downloaded task analyzed in place");
    Assert(File.Exists(Path.Combine(jobRoot, "task.json"))
           && File.Exists(Path.Combine(jobRoot, "analysis.json"))
           && File.Exists(Path.Combine(jobRoot, "分析说明.txt")),
        "downloaded task writes artifacts in same job root");
    Assert(Directory.GetDirectories(Path.GetDirectoryName(jobRoot)!).Length == 1,
        "downloaded task does not create sibling task");
    Console.WriteLine("PASS downloaded task continues in place");
}

static async Task TestExistingDirectoryScanIsReadOnlyAsync(string root)
{
    var taskRoot = Path.Combine(root, "existing-readonly-task");
    var input = Path.Combine(taskRoot, "AssetRipper_Input");
    var dataDirectory = Path.Combine(input, "base", "assets", "bin", "Data");
    var nativeDirectory = Path.Combine(input, "config.arm64_v8a", "lib", "arm64-v8a");
    Directory.CreateDirectory(dataDirectory);
    Directory.CreateDirectory(nativeDirectory);
    await File.WriteAllTextAsync(Path.Combine(dataDirectory, "globalgamemanagers"), "unity-existing");
    await File.WriteAllTextAsync(Path.Combine(nativeDirectory, "libil2cpp.so"), "native-existing");
    await File.WriteAllTextAsync(
        Path.Combine(taskRoot, "task.json"),
        JsonSerializer.Serialize(new
        {
            PackageName = "com.example.existing",
            Source = "旧任务测试",
            InputDirectory = input
        }));
    var before = CaptureDirectorySnapshot(taskRoot);
    var coordinator = new WorkflowCoordinator();

    var result = await coordinator.ScanExistingDirectoryAsync(taskRoot);
    var after = CaptureDirectorySnapshot(taskRoot);

    Assert(PathsEqual(result.InputDirectory, input), "existing task resolves AssetRipper_Input");
    Assert(PathsEqual(result.TaskRoot!, taskRoot) && result.IsExistingTask,
        "existing task metadata recognized");
    Assert(result.PackageName == "com.example.existing" && result.Source == "旧任务测试",
        "existing task metadata preserved");
    Assert(result.Engine == GameEngine.Unity && result.ScriptingBackend == "IL2CPP",
        "existing directory analyzed read-only");
    Assert(SnapshotsEqual(before, after), "existing directory scan does not mutate files");
    Assert(!File.Exists(Path.Combine(taskRoot, "analysis.json"))
           && !File.Exists(Path.Combine(taskRoot, "分析说明.txt")),
        "existing directory scan creates no reports");
    Console.WriteLine("PASS existing directory read-only scan");
}

static async Task TestAssetRipperAutoLoadAndReuseAsync(string root)
{
    var fixture = CreateAssetRipperFixture(root, "asset-ripper-auto");
    var handler = new RecordingAssetRipperHandler();
    using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var platform = new FakeAssetRipperPlatform(43121);
    var launcher = CreateTestAssetRipperLauncher(platform, http);

    try
    {
        var first = await launcher.LaunchAsync(fixture.Executable, fixture.InputDirectory);
        Assert(first.Outcome == AssetRipperLaunchOutcome.AutoLoaded, "AssetRipper first launch auto-loaded");
        Assert(first.Port == platform.Port, "AssetRipper first launch port");
        Assert(first.ProcessId == platform.ManagedProcess.Id, "AssetRipper first launch process id");
        Assert(!first.ShouldCopyInputPath && !first.ShouldOpenInputDirectory,
            "AssetRipper auto-load needs no manual handoff");
        Assert(platform.ManagedStarts == 1 && platform.FallbackStarts == 0,
            "AssetRipper first launch uses managed instance");
        Assert(handler.Requests.Count == 2, "AssetRipper first launch HTTP count");
        AssertRequest(handler.Requests[0], HttpMethod.Get, "/", null);
        AssertRequest(handler.Requests[1], HttpMethod.Post, "/LoadFolder", fixture.InputDirectory);

        handler.Requests.Clear();
        var reused = await launcher.LaunchAsync(fixture.Executable, fixture.InputDirectory);
        Assert(reused.Outcome == AssetRipperLaunchOutcome.AutoLoaded, "AssetRipper reuse auto-loaded");
        Assert(reused.ProcessId == first.ProcessId, "AssetRipper reuse keeps process");
        Assert(platform.ManagedStarts == 1 && platform.FallbackStarts == 0,
            "AssetRipper reuse does not launch another process");
        Assert(handler.Requests.Count == 2, "AssetRipper reuse HTTP count");
        AssertRequest(handler.Requests[0], HttpMethod.Post, "/Reset", null);
        AssertRequest(handler.Requests[1], HttpMethod.Post, "/LoadFolder", fixture.InputDirectory);
    }
    finally
    {
        launcher.Dispose();
    }

    Assert(platform.ManagedProcess.DisposeCalls == 1, "AssetRipper Dispose releases managed process handle");
    Assert(!platform.ManagedProcess.HasExited, "AssetRipper Dispose does not stop managed process");
    Console.WriteLine("PASS AssetRipper auto-load and reuse");
}

static async Task TestAssetRipperFallbackAsync(string root)
{
    var fixture = CreateAssetRipperFixture(root, "asset-ripper-fallback");
    var handler = new RecordingAssetRipperHandler(failLoadFolder: true);
    using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var platform = new FakeAssetRipperPlatform(43122);
    using var launcher = CreateTestAssetRipperLauncher(platform, http);

    var result = await launcher.LaunchAsync(fixture.Executable, fixture.InputDirectory);
    Assert(result.Outcome == AssetRipperLaunchOutcome.Fallback, "AssetRipper interface failure falls back");
    Assert(result.Port == platform.Port, "AssetRipper fallback reports attempted managed port");
    Assert(result.ProcessId == platform.ManagedProcess.Id, "AssetRipper fallback reuses started process id");
    Assert(result.ShouldCopyInputPath && result.ShouldOpenInputDirectory,
        "AssetRipper fallback requests manual path handoff");
    Assert(result.AutomaticLoadError?.Contains("/LoadFolder", StringComparison.Ordinal) == true,
        "AssetRipper fallback reports failed endpoint");
    Assert(platform.ManagedStarts == 1 && platform.FallbackStarts == 0,
        "AssetRipper fallback avoids launching a duplicate instance");
    Assert(handler.Requests.Count == 2, "AssetRipper fallback HTTP count");
    AssertRequest(handler.Requests[0], HttpMethod.Get, "/", null);
    AssertRequest(handler.Requests[1], HttpMethod.Post, "/LoadFolder", fixture.InputDirectory);
    Assert(platform.ManagedProcess.DisposeCalls == 1,
        "AssetRipper fallback releases process handle without stopping process");
    Assert(!platform.ManagedProcess.HasExited, "AssetRipper fallback process remains running");
    Console.WriteLine("PASS AssetRipper compatibility fallback");
}

static AssetRipperLauncher CreateTestAssetRipperLauncher(
    IAssetRipperLaunchPlatform platform,
    HttpClient http) => new(
        new AssetRipperLauncherOptions(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(1),
            3),
        platform,
        http,
        Task.Delay);

static (string Executable, string InputDirectory) CreateAssetRipperFixture(string root, string name)
{
    var directory = Path.Combine(root, name);
    var input = Path.Combine(directory, "AssetRipper Input 空格");
    Directory.CreateDirectory(input);
    var executable = Path.Combine(directory, "AssetRipper.exe");
    File.WriteAllBytes(executable, []);
    return (executable, input);
}

static void AssertRequest(
    RecordedAssetRipperRequest request,
    HttpMethod method,
    string path,
    string? expectedInputDirectory)
{
    Assert(request.Method == method, $"AssetRipper {path} method");
    Assert(request.Host == "127.0.0.1", $"AssetRipper {path} loopback host");
    Assert(request.Path == path, $"AssetRipper {path} endpoint");

    if (method == HttpMethod.Post)
        Assert(request.MediaType == "application/x-www-form-urlencoded", $"AssetRipper {path} form media type");

    if (expectedInputDirectory is null)
    {
        Assert(string.IsNullOrEmpty(request.Body), $"AssetRipper {path} empty form");
        return;
    }

    Assert(request.Body.StartsWith("Path=", StringComparison.Ordinal), $"AssetRipper {path} Path field");
    var decoded = Uri.UnescapeDataString(request.Body[5..].Replace("+", " ", StringComparison.Ordinal));
    Assert(decoded == Path.GetFullPath(expectedInputDirectory), $"AssetRipper {path} decoded input path");
}

static FileSnapshot CaptureFileSnapshot(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return new FileSnapshot(
        stream.Length,
        File.GetLastWriteTimeUtc(path).Ticks,
        Convert.ToHexString(SHA256.HashData(stream)));
}

static IReadOnlyDictionary<string, FileSnapshot> CaptureDirectorySnapshot(string directory) =>
    Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(
            path => Path.GetRelativePath(directory, path).Replace('\\', '/'),
            CaptureFileSnapshot,
            StringComparer.OrdinalIgnoreCase);

static bool SnapshotsEqual(
    IReadOnlyDictionary<string, FileSnapshot> left,
    IReadOnlyDictionary<string, FileSnapshot> right) =>
    left.Count == right.Count
    && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

static bool PathsEqual(string left, string right) =>
    string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

static bool IsPathInside(string parent, string candidate)
{
    var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
    return relative != ".."
           && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
           && !Path.IsPathRooted(relative);
}

static void CreateZip(string path, IReadOnlyDictionary<string, string> entries)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var pair in entries)
    {
        var entry = archive.CreateEntry(pair.Key);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(pair.Value);
    }
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
}

internal sealed record RecordedAssetRipperRequest(
    HttpMethod Method,
    string Host,
    string Path,
    string? MediaType,
    string Body);

internal sealed record FileSnapshot(long Length, long LastWriteUtcTicks, string Hash);

internal sealed class RecordingAssetRipperHandler(bool failLoadFolder = false) : HttpMessageHandler
{
    public List<RecordedAssetRipperRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedAssetRipperRequest(
            request.Method,
            request.RequestUri!.Host,
            request.RequestUri.AbsolutePath,
            request.Content?.Headers.ContentType?.MediaType,
            body));

        var response = failLoadFolder && request.RequestUri.AbsolutePath == "/LoadFolder"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("/", UriKind.Relative) }
                };
        response.RequestMessage = request;
        return response;
    }
}

internal sealed class StaticContentHandler(byte[] content) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
            RequestMessage = request
        });
}

internal sealed class StatusCodeHandler(HttpStatusCode statusCode) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(statusCode) { RequestMessage = request });
}

internal sealed class FakeToolProvider(string root) : IExternalToolProvider
{
    public Task<ExternalToolInstallation> EnsureInstalledAsync(ExternalToolId id,
        IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var spec = ExternalToolManager.Catalog[id];
        var directory = Path.Combine(root, id.ToString());
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, spec.ExecutableName);
        File.WriteAllBytes(executable, []);
        return Task.FromResult(new ExternalToolInstallation(spec, directory, executable));
    }
}

internal sealed class FakeRecoveryProcessRunner : IExternalProcessRunner
{
    public List<ExternalProcessRequest> Requests { get; } = [];
    public int ExitCode { get; init; }
    public bool BlockUntilCancelled { get; init; }

    public async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        if (BlockUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var gdreOutput = request.Arguments.FirstOrDefault(arg => arg.StartsWith("--output=", StringComparison.Ordinal));
        if (gdreOutput != null)
        {
            var directory = gdreOutput[9..];
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "project.godot"), "[application]");
        }
        var outputIndex = request.Arguments.ToList().IndexOf("--output");
        if (outputIndex >= 0 && outputIndex + 1 < request.Arguments.Count)
        {
            Directory.CreateDirectory(request.Arguments[outputIndex + 1]);
            File.WriteAllText(Path.Combine(request.Arguments[outputIndex + 1], "asset.uasset"), "asset");
        }
        var unpackIndex = request.Arguments.ToList().IndexOf("unpack");
        if (unpackIndex >= 0 && request.ExecutablePath.EndsWith("retoc.exe", StringComparison.OrdinalIgnoreCase))
        {
            var directory = request.Arguments[^1];
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "chunk.bin"), "chunk");
        }
        if (request.LogPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.LogPath)!);
            File.WriteAllText(request.LogPath, "fixture log");
        }
        return new ExternalProcessResult(ExitCode, ExitCode == 0 ? "ok" : "", ExitCode == 0 ? "" : "damaged fixture");
    }
}

internal sealed class FakeAssetRipperPlatform(int port) : IAssetRipperLaunchPlatform
{
    public int Port { get; } = port;
    public FakeAssetRipperProcess ManagedProcess { get; } = new(6101);
    public FakeAssetRipperProcess FallbackProcess { get; } = new(6102);
    public int ManagedStarts { get; private set; }
    public int FallbackStarts { get; private set; }

    public int AllocateLoopbackPort() => Port;

    public IAssetRipperProcessHandle StartManaged(string executablePath, int port)
    {
        if (port != Port)
            throw new InvalidOperationException("FAILED: AssetRipper platform receives allocated port");
        ManagedStarts++;
        return ManagedProcess;
    }

    public IAssetRipperProcessHandle StartFallback(string executablePath)
    {
        FallbackStarts++;
        return FallbackProcess;
    }
}

internal sealed class FakeAssetRipperProcess(int id) : IAssetRipperProcessHandle
{
    public int Id { get; } = id;
    public bool HasExited { get; set; }
    public int DisposeCalls { get; private set; }
    public int TerminateCalls { get; private set; }

    public void TryTerminate()
    {
        TerminateCalls++;
        HasExited = true;
    }

    public void Dispose() => DisposeCalls++;
}
