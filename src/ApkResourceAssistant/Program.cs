using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GooglePlayApkDownloader;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _action = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _url = new() { Text = "https://play.google.com/store/apps/details?id=com.oakever.meowdoku&hl=zh" };
    private readonly TextBox _output = new() { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "APK") };
    private readonly TextBox _email = new();
    private readonly TextBox _aasToken = new() { UseSystemPasswordChar = true };
    private readonly TextBox _oauthToken = new() { UseSystemPasswordChar = true };
    private readonly TextBox _assetRipper = new() { ReadOnly = true };
    private readonly CheckBox _split = new() { Text = "下载 Split APK（分析游戏推荐开启）", Checked = true, AutoSize = true };
    private readonly CheckBox _remember = new() { Text = "加密保存个人账号凭据", Checked = true, AutoSize = true };
    private readonly Button _download = new() { Text = "开始下载和处理", Height = 40 };
    private readonly Button _exchange = new() { Text = "兑换 AAS" };
    private readonly Button _anonymous = new() { Text = "一键匿名" };
    private readonly Button _browse = new() { Text = "浏览…" };
    private readonly Button _chooseAssetRipper = new() { Text = "选择…" };
    private readonly Button _testAssetRipper = new() { Text = "测试" };
    private readonly Button _openResult = new() { Text = "打开分析目录", Enabled = false };
    private readonly Button _copyPath = new() { Text = "复制路径", Enabled = false };
    private readonly Button _launchAssetRipper = new() { Text = "启动 AssetRipper", Enabled = false };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly Label _stage = new() { Text = "就绪", AutoSize = true, ForeColor = Color.DimGray };
    private readonly TextBox _summary = new() { Multiline = true, ReadOnly = true, BackColor = Color.White, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };

    private static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GooglePlayApkDownloader");
    private static readonly string SettingsPath = Path.Combine(AppDir, "settings.json");
    private static readonly string ApkeepPath = Path.Combine(AppDir, "tools", "apkeep.exe");
    private readonly HttpClient _http = new();
    private AnalysisResult? _lastAnalysis;
    private string? _lastResultPath;

    public MainForm()
    {
        Text = "APK 下载与资源分析助手";
        Width = 930;
        Height = 860;
        MinimumSize = new Size(820, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ApkResourceAssistant", "2.0"));

        _source.Items.AddRange(["APKPure（免登录）", "Google Play（一键匿名）", "Google Play（个人账号）"]);
        _action.Items.AddRange(["仅下载", "下载并解压", "下载、解压并准备逆向（推荐）"]);
        _source.SelectedIndex = 0;
        _action.SelectedIndex = 2;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var taskTab = new TabPage("下载与分析") { Padding = new Padding(14) };
        var logTab = new TabPage("运行日志") { Padding = new Padding(10) };
        tabs.TabPages.Add(taskTab);
        tabs.TabPages.Add(logTab);
        Controls.Add(tabs);

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 15 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        for (var i = 0; i < 11; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        taskTab.Controls.Add(table);

        AddRow(table, 0, "下载源", _source);
        AddRow(table, 1, "处理方式", _action);
        AddRow(table, 2, "商店链接/包名", _url);
        AddRow(table, 3, "保存根目录", _output, _browse);
        AddRow(table, 4, "Google 邮箱", _email);
        AddRow(table, 5, "AAS/AUTH token", _aasToken, _anonymous);
        AddRow(table, 6, "一次性 OAuth", _oauthToken, _exchange);
        AddRow(table, 7, "AssetRipper", _assetRipper, _chooseAssetRipper, _testAssetRipper);

        table.Controls.Add(_split, 1, 8);
        table.SetColumnSpan(_split, 3);
        table.Controls.Add(_remember, 1, 9);
        table.SetColumnSpan(_remember, 3);

        var actionButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        actionButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        actionButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        _download.Dock = DockStyle.Fill;
        _openResult.Dock = DockStyle.Fill;
        actionButtons.Controls.Add(_download, 0, 0);
        actionButtons.Controls.Add(_openResult, 1, 0);
        table.Controls.Add(actionButtons, 1, 10);
        table.SetColumnSpan(actionButtons, 3);

        _progress.Dock = DockStyle.Fill;
        table.Controls.Add(_progress, 1, 11);
        table.SetColumnSpan(_progress, 3);
        table.Controls.Add(_stage, 1, 12);
        table.SetColumnSpan(_stage, 3);

        var resultButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        resultButtons.Controls.Add(_copyPath);
        resultButtons.Controls.Add(_launchAssetRipper);
        table.Controls.Add(resultButtons, 1, 13);
        table.SetColumnSpan(resultButtons, 3);
        _summary.Dock = DockStyle.Fill;
        table.Controls.Add(_summary, 0, 14);
        table.SetColumnSpan(_summary, 4);
        _log.Dock = DockStyle.Fill;
        logTab.Controls.Add(_log);

        _browse.Click += BrowseOutputClicked;
        _chooseAssetRipper.Click += (_, _) => ChooseAssetRipper();
        _testAssetRipper.Click += (_, _) => TestAssetRipper();
        _download.Click += async (_, _) => await DownloadAndProcessAsync();
        _exchange.Click += async (_, _) => await ExchangeAsync();
        _anonymous.Click += async (_, _) => await AnonymousLoginAsync();
        _source.SelectedIndexChanged += (_, _) => UpdateUiState();
        _action.SelectedIndexChanged += (_, _) => UpdateUiState();
        _openResult.Click += (_, _) => OpenResult();
        _copyPath.Click += (_, _) => CopyResultPath();
        _launchAssetRipper.Click += (_, _) => LaunchAssetRipperForLastResult(true);
        FormClosed += (_, _) => _http.Dispose();
        Shown += async (_, _) => await PrepareBundledEngineAsync();

        LoadSettings();
        UpdateUiState();
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control input, Control? third = null, Control? fourth = null)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        input.Dock = DockStyle.Fill;
        table.Controls.Add(input, 1, row);
        if (third == null)
        {
            table.SetColumnSpan(input, 3);
            return;
        }
        third.Dock = DockStyle.Fill;
        table.Controls.Add(third, 2, row);
        if (fourth == null) table.SetColumnSpan(third, 2);
        else { fourth.Dock = DockStyle.Fill; table.Controls.Add(fourth, 3, row); }
    }

    private void BrowseOutputClicked(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _output.Text, ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) _output.Text = dialog.SelectedPath;
    }

    private bool ChooseAssetRipper()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 AssetRipper 可执行文件",
            Filter = "AssetRipper (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        _assetRipper.Text = dialog.FileName;
        SaveSettings();
        UpdateUiState();
        return true;
    }

    private void TestAssetRipper()
    {
        if (!EnsureAssetRipperSelected()) return;
        Process.Start(new ProcessStartInfo(_assetRipper.Text) { UseShellExecute = true });
        AppendLog("AssetRipper 已启动。");
    }

    private async Task DownloadAndProcessAsync()
    {
        try
        {
            SetBusy(true);
            ResetResult();
            var package = ParsePackageName(_url.Text);
            var root = _output.Text.Trim();
            if (root.Length == 0) throw new InvalidOperationException("请选择保存根目录。");
            var sourceLabel = _source.SelectedItem?.ToString() ?? "未知";
            var jobRoot = CreateUniqueJobDirectory(root, package);
            var originalDir = Path.Combine(jobRoot, "Original_APKs");
            Directory.CreateDirectory(originalDir);
            SaveSettings();

            SetStage("准备内置下载引擎");
            await EnsureApkeepAsync();
            await DownloadPackageAsync(package, originalDir);
            var downloadedApks = Directory.EnumerateFiles(originalDir, "*.apk", SearchOption.AllDirectories).ToList();
            if (downloadedApks.Count == 0) throw new InvalidOperationException("下载进程结束，但没有找到 APK 文件。");
            AppendLog($"下载完成，共 {downloadedApks.Count} 个 APK。");

            if (_action.SelectedIndex == 0)
            {
                _lastResultPath = jobRoot;
                ShowDownloadOnlyResult(package, sourceLabel, jobRoot, downloadedApks);
                FinishButtons(false);
                MessageBox.Show(this, "APK 下载完成。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var analysisProgress = new Progress<string>(SetStage);
            _lastAnalysis = await AnalysisPipeline.ExtractAndAnalyzeAsync(package, sourceLabel, jobRoot, analysisProgress);
            _lastResultPath = _lastAnalysis.InputDirectory;
            ShowAnalysisResult(_lastAnalysis);
            FinishButtons(_lastAnalysis.Engine == GameEngine.Unity);

            if (_action.SelectedIndex == 2 && _lastAnalysis.Engine == GameEngine.Unity)
                LaunchAssetRipperForLastResult(true);
            else
                OpenPath(jobRoot);

            var message = _lastAnalysis.Engine switch
            {
                GameEngine.Unity => "分析目录已生成，可交给 AssetRipper。",
                GameEngine.Godot => "检测到 Godot，已完整解压；AssetRipper 不适用于该引擎。",
                GameEngine.Unreal => "检测到 Unreal，已完整解压；AssetRipper 不适用于该引擎。",
                _ => "已完整解压，但没有识别出明确游戏引擎。"
            };
            MessageBox.Show(this, message, "处理完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog("错误：" + ex);
            MessageBox.Show(this, ex.Message, "处理失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetStage("就绪");
            SetBusy(false);
        }
    }

    private async Task DownloadPackageAsync(string package, string outputDirectory)
    {
        if (_source.SelectedIndex == 0)
        {
            SetStage("从 APKPure 下载");
            var code = await RunProcessAsync(ApkeepPath, ["-a", package, "-d", "apk-pure", outputDirectory]);
            if (code != 0) throw new InvalidOperationException($"APKPure 下载进程退出码：{code}。可切换到 Google Play（一键匿名）重试。");
            return;
        }

        string email;
        string token;
        if (_source.SelectedIndex == 1)
        {
            SetStage("申请匿名 Google Play 凭据");
            (email, token) = await RequestAnonymousCredentialsAsync();
            _email.Text = email;
            _aasToken.Text = token;
        }
        else
        {
            email = _email.Text.Trim();
            token = _aasToken.Text.Trim();
            if (email.Length == 0 || token.Length == 0) throw new InvalidOperationException("个人账号模式需要 Google 邮箱和 AAS/AUTH token。");
        }

        var configPath = Path.Combine(Path.GetTempPath(), $"apkeep-{Guid.NewGuid():N}.ini");
        try
        {
            var isAuthToken = token.StartsWith("ya29.", StringComparison.OrdinalIgnoreCase);
            var tokenKey = isAuthToken ? "auth_token" : "aas_token";
            await File.WriteAllTextAsync(configPath, $"[google]\nemail = {email}\n{tokenKey} = {token}\n", new UTF8Encoding(false));
            var options = "locale=zh_CN,timezone=Asia/Shanghai" + (_split.Checked ? ",split_apk=true" : "");
            var arguments = new List<string> { "-a", package, "-d", "google-play", "-i", configPath, "-o", options };
            if (isAuthToken) arguments.Add("--accept-tos");
            arguments.Add(outputDirectory);
            SetStage("从 Google Play 下载");
            var code = await RunProcessAsync(ApkeepPath, arguments);
            if (code != 0) throw new InvalidOperationException($"Google Play 下载进程退出码：{code}。");
        }
        finally { try { File.Delete(configPath); } catch { } }
    }

    private void ShowDownloadOnlyResult(string package, string source, string jobRoot, IReadOnlyList<string> apks)
    {
        var builder = new StringBuilder();
        builder.AppendLine("下载完成");
        builder.AppendLine($"包名：{package}");
        builder.AppendLine($"来源：{source}");
        builder.AppendLine($"APK 数量：{apks.Count}");
        builder.AppendLine($"任务目录：{jobRoot}");
        foreach (var apk in apks) builder.AppendLine($"- {Path.GetFileName(apk)} ({AnalysisPipeline.FormatBytes(new FileInfo(apk).Length)})");
        _summary.Text = builder.ToString();
    }

    private void ShowAnalysisResult(AnalysisResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("处理完成");
        builder.AppendLine($"包名：{result.PackageName}");
        builder.AppendLine($"来源：{result.Source}");
        builder.AppendLine($"APK：{result.Splits.Count} 个");
        builder.AppendLine($"引擎：{result.Engine}");
        builder.AppendLine($"脚本后端：{result.ScriptingBackend}");
        builder.AppendLine($"解压：{result.ExtractedFiles} 个文件，{AnalysisPipeline.FormatBytes(result.ExtractedBytes)}");
        builder.AppendLine($"关键文件：{result.KeyFiles.Count} 个");
        builder.AppendLine($"分析目录：{result.InputDirectory}");
        builder.AppendLine();
        foreach (var split in result.Splits) builder.AppendLine($"- [{split.Kind}] {split.FileName}");
        _summary.Text = builder.ToString();
        AppendLog($"引擎识别：{result.Engine}，后端：{result.ScriptingBackend}");
    }

    private void FinishButtons(bool unity)
    {
        _openResult.Enabled = true;
        _copyPath.Enabled = true;
        _launchAssetRipper.Enabled = unity;
    }

    private void ResetResult()
    {
        _lastAnalysis = null;
        _lastResultPath = null;
        _summary.Clear();
        _openResult.Enabled = false;
        _copyPath.Enabled = false;
        _launchAssetRipper.Enabled = false;
    }

    private void OpenResult()
    {
        if (_lastAnalysis != null) OpenPath(_lastAnalysis.JobRoot);
        else if (_lastResultPath != null) OpenPath(_lastResultPath);
    }

    private void CopyResultPath()
    {
        if (string.IsNullOrWhiteSpace(_lastResultPath)) return;
        Clipboard.SetText(_lastResultPath);
        SetStage("分析目录路径已复制到剪贴板");
    }

    private void LaunchAssetRipperForLastResult(bool openExplorer)
    {
        if (_lastAnalysis?.Engine != GameEngine.Unity) return;
        if (!EnsureAssetRipperSelected()) return;
        Clipboard.SetText(_lastAnalysis.InputDirectory);
        Process.Start(new ProcessStartInfo(_assetRipper.Text) { UseShellExecute = true });
        if (openExplorer) OpenPath(_lastAnalysis.InputDirectory);
        AppendLog("已启动 AssetRipper、打开输入目录并复制目录路径。");
    }

    private bool EnsureAssetRipperSelected()
    {
        if (File.Exists(_assetRipper.Text)) return true;
        MessageBox.Show(this, "请先选择本机 AssetRipper 可执行文件。", "配置 AssetRipper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return ChooseAssetRipper();
    }

    private async Task ExchangeAsync()
    {
        try
        {
            var email = _email.Text.Trim();
            var oauth = _oauthToken.Text.Trim();
            if (email.Length == 0) throw new InvalidOperationException("请填写 Google 邮箱。");
            if (oauth.Length == 0)
            {
                var answer = MessageBox.Show(this, "需要从 Google Embedded Setup 获取以 oauth2_4/ 开头的一次性 token。现在打开页面吗？", "获取 OAuth token", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) Process.Start(new ProcessStartInfo("https://accounts.google.com/EmbeddedSetup") { UseShellExecute = true });
                return;
            }
            SetBusy(true);
            await EnsureApkeepAsync();
            var captured = new StringBuilder();
            var code = await RunProcessAsync(ApkeepPath, ["-e", email, "--oauth-token", oauth], captured);
            if (code != 0) throw new InvalidOperationException($"token 兑换失败，退出码：{code}");
            var match = Regex.Match(captured.ToString(), @"AAS Token:\s*(\S+)", RegexOptions.IgnoreCase);
            if (!match.Success) throw new InvalidOperationException("输出中没有识别到 AAS token。");
            _aasToken.Text = match.Groups[1].Value;
            _oauthToken.Clear();
            SaveSettings();
            MessageBox.Show(this, "AAS token 已填入。", "兑换完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "兑换失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { SetBusy(false); }
    }

    private async Task AnonymousLoginAsync()
    {
        try
        {
            SetBusy(true);
            var (email, auth) = await RequestAnonymousCredentialsAsync();
            _email.Text = email;
            _aasToken.Text = auth;
            MessageBox.Show(this, "匿名凭据已填入。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "匿名登录失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { SetBusy(false); }
    }

    private async Task<(string Email, string Auth)> RequestAnonymousCredentialsAsync()
    {
        AppendLog("正在申请匿名 Google Play 临时凭据 …");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://auroraoss.com/api/auth/");
        request.Headers.UserAgent.ParseAdd("AuroraStore/4.6.5");
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"匿名服务返回 HTTP {(int)response.StatusCode}，请稍后再试。");
        using var json = JsonDocument.Parse(body);
        var email = json.RootElement.GetProperty("email").GetString();
        var auth = json.RootElement.GetProperty("auth").GetString();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("ya29.")) throw new InvalidOperationException("匿名服务返回的数据格式不正确。");
        return (email, auth);
    }

    private async Task EnsureApkeepAsync()
    {
        if (!File.Exists(ApkeepPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ApkeepPath)!);
            var temp = ApkeepPath + ".download";
            try
            {
                await using var source = typeof(MainForm).Assembly.GetManifestResourceStream("BundledApkeep.exe") ?? throw new InvalidOperationException("EXE 中缺少内置下载引擎。");
                await using var target = File.Create(temp);
                await source.CopyToAsync(target);
                target.Close();
                File.Move(temp, ApkeepPath, true);
            }
            finally { try { File.Delete(temp); } catch { } }
        }
        PatchPeStackReserve(ApkeepPath);
    }

    private async Task PrepareBundledEngineAsync()
    {
        try { await EnsureApkeepAsync(); }
        catch (Exception ex) { AppendLog("准备内置下载引擎失败：" + ex.Message); }
    }

    private static void PatchPeStackReserve(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 256 || BitConverter.ToUInt16(bytes, 0) != 0x5A4D) return;
        var pe = BitConverter.ToInt32(bytes, 0x3c);
        var optional = pe + 24;
        if (optional + 88 > bytes.Length || BitConverter.ToUInt16(bytes, optional) != 0x20B) return;
        var offset = optional + 72;
        const ulong desired = 64UL * 1024 * 1024;
        if (BitConverter.ToUInt64(bytes, offset) >= desired) return;
        BitConverter.GetBytes(desired).CopyTo(bytes, offset);
        File.WriteAllBytes(path, bytes);
    }

    private async Task<int> RunProcessAsync(string file, IReadOnlyList<string> arguments, StringBuilder? capture = null)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { capture?.AppendLine(e.Data); AppendLog(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { capture?.AppendLine(e.Data); AppendLog(e.Data); } };
        if (!process.Start()) throw new InvalidOperationException("启动下载引擎失败。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static string ParsePackageName(string value)
    {
        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (!uri.Host.Equals("play.google.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("www.play.google.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("链接必须来自 play.google.com。");
            var match = Regex.Match(uri.Query, @"(?:^|[?&])id=([^&]+)");
            if (!match.Success) throw new InvalidOperationException("链接中缺少应用 id 参数。");
            candidate = Uri.UnescapeDataString(match.Groups[1].Value);
        }
        if (!Regex.IsMatch(candidate, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$")) throw new InvalidOperationException("Android 包名格式不正确。");
        return candidate;
    }

    private static string CreateUniqueJobDirectory(string root, string package)
    {
        var packageRoot = Path.Combine(Path.GetFullPath(root), package);
        Directory.CreateDirectory(packageRoot);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var candidate = Path.Combine(packageRoot, timestamp);
        var suffix = 2;
        while (Directory.Exists(candidate)) candidate = Path.Combine(packageRoot, $"{timestamp}-{suffix++}");
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private void SetBusy(bool busy)
    {
        _download.Enabled = !busy;
        _source.Enabled = !busy;
        _action.Enabled = !busy;
        _browse.Enabled = !busy;
        _chooseAssetRipper.Enabled = !busy;
        _testAssetRipper.Enabled = !busy;
        _exchange.Enabled = false;
        _anonymous.Enabled = false;
        _progress.Visible = busy;
        UseWaitCursor = busy;
        if (!busy) UpdateUiState();
    }

    private void UpdateUiState()
    {
        var isGoogle = _source.SelectedIndex > 0;
        var isPersonal = _source.SelectedIndex == 2;
        var wantsAnalysis = _action.SelectedIndex == 2;
        _email.Enabled = isGoogle;
        _aasToken.Enabled = isGoogle;
        _oauthToken.Enabled = isPersonal;
        _exchange.Enabled = isPersonal;
        _anonymous.Enabled = _source.SelectedIndex == 1;
        _remember.Enabled = isPersonal;
        _split.Enabled = isGoogle;
        _assetRipper.Enabled = wantsAnalysis;
        _chooseAssetRipper.Enabled = wantsAnalysis;
        _testAssetRipper.Enabled = wantsAnalysis;
    }

    private void SetStage(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStage(text)); return; }
        _stage.Text = text;
        AppendLog(text);
    }

    private void AppendLog(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(text)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text.TrimEnd()}\r\n");
    }

    private static void OpenPath(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (settings == null) return;
            _email.Text = settings.Email ?? "";
            _output.Text = settings.OutputDir ?? _output.Text;
            _assetRipper.Text = settings.AssetRipperPath ?? "";
            _split.Checked = settings.Split;
            _source.SelectedIndex = Math.Clamp(settings.Source, 0, 2);
            _action.SelectedIndex = settings.Action.HasValue ? Math.Clamp(settings.Action.Value, 0, 2) : 2;
            if (!string.IsNullOrEmpty(settings.ProtectedToken)) _aasToken.Text = Dpapi.Unprotect(settings.ProtectedToken);
        }
        catch (Exception ex) { AppendLog("读取设置失败：" + ex.Message); }
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(AppDir);
        var protectedToken = _remember.Checked && !string.IsNullOrWhiteSpace(_aasToken.Text) ? Dpapi.Protect(_aasToken.Text.Trim()) : null;
        var settings = new Settings(_email.Text.Trim(), _output.Text.Trim(), _split.Checked, _source.SelectedIndex, _action.SelectedIndex,
            _assetRipper.Text.Trim(), protectedToken);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
    }

    private sealed record Settings(string? Email, string? OutputDir, bool Split, int Source, int? Action, string? AssetRipperPath, string? ProtectedToken);
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            DataBlob output;
            var ok = protect ? CryptProtectData(ref input, "ApkResourceAssistant", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
