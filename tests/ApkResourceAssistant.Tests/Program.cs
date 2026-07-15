using System.IO.Compression;
using GooglePlayApkDownloader;

var root = Path.Combine(Path.GetTempPath(), "ApkResourceAssistantTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await TestUnitySplitAsync(root);
    await TestZipTraversalAsync(root);
    await TestCorruptApkAsync(root);
    await TestGodotSampleAsync(root);
    Console.WriteLine("ALL TESTS PASSED");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
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
    var sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "google-download-stack-test"));
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
    Assert(File.ReadAllText(result.ReportPath).Contains("AssetRipper 面向 Unity"), "Godot AssetRipper warning");
    Console.WriteLine("PASS real Godot split sample");
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
