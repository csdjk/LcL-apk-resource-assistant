using System.Diagnostics;

namespace GooglePlayApkDownloader;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly Func<CancellationToken, Task<(string Email, string Auth)>> _getAnonymous;
    private readonly Func<string, string, CancellationToken, Task<string>> _exchangeOAuth;
    private readonly TextBox _email = UiTheme.TextInput();
    private readonly TextBox _token = UiTheme.TextInput();
    private readonly TextBox _oauth = UiTheme.TextInput();
    private readonly TextBox _assetRipper = UiTheme.TextInput(readOnly: true);
    private readonly CheckBox _remember = new() { Text = "使用 Windows 加密保存个人账号凭据", AutoSize = true };
    private readonly Label _status = UiTheme.Caption("设置只保存在本机。账号 token 使用当前 Windows 用户的 DPAPI 加密。");
    private readonly Button _anonymous = UiTheme.SecondaryButton("获取匿名凭据");
    private readonly Button _exchange = UiTheme.SecondaryButton("兑换 AAS token");
    private readonly Button _chooseAssetRipper = UiTheme.SecondaryButton("选择…");
    private readonly Button _testAssetRipper = UiTheme.SecondaryButton("测试启动");
    private readonly Button _save = UiTheme.PrimaryButton("保存设置");
    private readonly CancellationTokenSource _closingCts = new();

    public SettingsForm(
        AppSettings settings,
        Func<CancellationToken, Task<(string Email, string Auth)>> getAnonymous,
        Func<string, string, CancellationToken, Task<string>> exchangeOAuth)
    {
        _settings = settings;
        _getAnonymous = getAnonymous;
        _exchangeOAuth = exchangeOAuth;

        Text = "设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(690, 610);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = UiTheme.Window;
        Padding = new Padding(22);

        _email.Text = settings.Email ?? string.Empty;
        _token.Text = settings.Token;
        _token.UseSystemPasswordChar = true;
        _oauth.UseSystemPasswordChar = true;
        _assetRipper.Text = settings.AssetRipperPath ?? string.Empty;
        _remember.Checked = settings.RememberCredentials;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = UiTheme.Window };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 310));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(root);

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = UiTheme.Window };
        header.Controls.Add(UiTheme.Heading("设置", 17F));
        header.Controls.Add(UiTheme.Caption("下载凭据与 AssetRipper 路径集中管理，主界面保持简洁。"));
        root.Controls.Add(header, 0, 0);

        root.Controls.Add(BuildAccountCard(), 0, 1);
        root.Controls.Add(BuildAssetRipperCard(), 0, 2);
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_status, 0, 3);
        root.Controls.Add(BuildFooter(), 0, 4);

        _anonymous.Click += async (_, _) => await GetAnonymousAsync();
        _exchange.Click += async (_, _) => await ExchangeAsync();
        _chooseAssetRipper.Click += (_, _) => ChooseAssetRipper();
        _testAssetRipper.Click += (_, _) => TestAssetRipper();
        _save.Click += (_, _) => SaveAndClose();
        FormClosed += (_, _) =>
        {
            _closingCts.Cancel();
            _closingCts.Dispose();
        };
        AcceptButton = _save;
    }

    private Control BuildAccountCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(18) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 6 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        for (var i = 1; i <= 3; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(table);

        var heading = UiTheme.Heading("Google Play 凭据", 11F);
        table.Controls.Add(heading, 0, 0);
        table.SetColumnSpan(heading, 3);
        AddRow(table, 1, "Google 邮箱", _email);
        AddRow(table, 2, "AAS/AUTH token", _token, _anonymous);
        AddRow(table, 3, "一次性 OAuth", _oauth, _exchange);
        table.Controls.Add(_remember, 1, 4);
        table.SetColumnSpan(_remember, 2);
        var hint = UiTheme.Caption("匿名模式会自动申请临时凭据；个人账号模式使用这里保存的邮箱与 token。");
        hint.Dock = DockStyle.Fill;
        table.Controls.Add(hint, 1, 5);
        table.SetColumnSpan(hint, 2);
        return card;
    }

    private Control BuildAssetRipperCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(18) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(table);
        var heading = UiTheme.Heading("AssetRipper", 11F);
        table.Controls.Add(heading, 0, 0);
        table.SetColumnSpan(heading, 4);
        var label = new Label { Text = "程序路径", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = UiTheme.Text };
        table.Controls.Add(label, 0, 1);
        _assetRipper.Dock = DockStyle.Fill;
        table.Controls.Add(_assetRipper, 1, 1);
        _chooseAssetRipper.Dock = DockStyle.Fill;
        _testAssetRipper.Dock = DockStyle.Fill;
        _chooseAssetRipper.Margin = new Padding(8, 4, 0, 4);
        _testAssetRipper.Margin = new Padding(8, 4, 0, 4);
        table.Controls.Add(_chooseAssetRipper, 2, 1);
        table.Controls.Add(_testAssetRipper, 3, 1);
        return card;
    }

    private Control BuildFooter()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = UiTheme.Window };
        _save.Width = 120;
        var cancel = UiTheme.SecondaryButton("取消", 40);
        cancel.Width = 90;
        cancel.Margin = new Padding(10, 0, 0, 0);
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        CancelButton = cancel;
        panel.Controls.Add(_save);
        panel.Controls.Add(cancel);
        return panel;
    }

    private static void AddRow(TableLayoutPanel table, int row, string labelText, Control input, Control? action = null)
    {
        table.Controls.Add(new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = UiTheme.Text }, 0, row);
        input.Dock = DockStyle.Fill;
        table.Controls.Add(input, 1, row);
        if (action == null)
        {
            table.SetColumnSpan(input, 2);
        }
        else
        {
            action.Dock = DockStyle.Fill;
            action.Margin = new Padding(8, 4, 0, 4);
            table.Controls.Add(action, 2, row);
        }
    }

    private async Task GetAnonymousAsync()
    {
        await RunBusyAsync(async token =>
        {
            _status.Text = "正在申请匿名 Google Play 凭据…";
            var credentials = await _getAnonymous(token);
            _status.Text = $"匿名服务可用（{MaskEmail(credentials.Email)}）。实际下载时会重新申请临时凭据，不会覆盖个人账号。";
        });
    }

    private async Task ExchangeAsync()
    {
        await RunBusyAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(_email.Text)) throw new InvalidOperationException("请填写 Google 邮箱。");
            if (string.IsNullOrWhiteSpace(_oauth.Text))
            {
                Process.Start(new ProcessStartInfo("https://accounts.google.com/EmbeddedSetup") { UseShellExecute = true });
                _status.Text = "已打开 OAuth 获取页面。复制一次性 token 后再点击兑换。";
                return;
            }
            _status.Text = "正在兑换 AAS token…";
            _token.Text = await _exchangeOAuth(_email.Text.Trim(), _oauth.Text.Trim(), token);
            _oauth.Clear();
            _status.Text = "AAS token 已兑换并填入。";
        });
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        SetBusy(true);
        try
        {
            await action(_closingCts.Token);
        }
        catch (OperationCanceledException) when (_closingCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _status.ForeColor = UiTheme.Danger;
            _status.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _anonymous.Enabled = !busy;
        _exchange.Enabled = !busy;
        _chooseAssetRipper.Enabled = !busy;
        _testAssetRipper.Enabled = !busy;
        _save.Enabled = !busy;
        UseWaitCursor = busy;
        if (!busy && _status.ForeColor != UiTheme.Danger) _status.ForeColor = UiTheme.Muted;
    }

    private void ChooseAssetRipper()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 AssetRipper 可执行文件",
            Filter = "AssetRipper (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _assetRipper.Text = dialog.FileName;
    }

    private void TestAssetRipper()
    {
        if (!File.Exists(_assetRipper.Text))
        {
            _status.ForeColor = UiTheme.Danger;
            _status.Text = "请先选择有效的 AssetRipper 可执行文件。";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_assetRipper.Text) { UseShellExecute = true });
            _status.ForeColor = UiTheme.Success;
            _status.Text = "AssetRipper 已启动。";
        }
        catch (Exception ex)
        {
            _status.ForeColor = UiTheme.Danger;
            _status.Text = "测试启动失败：" + ex.Message;
        }
    }

    private void SaveAndClose()
    {
        if (!string.IsNullOrWhiteSpace(_assetRipper.Text) &&
            (!Path.GetExtension(_assetRipper.Text).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(_assetRipper.Text)))
        {
            _status.ForeColor = UiTheme.Danger;
            _status.Text = "请选择有效的 AssetRipper.exe，或清空程序路径。";
            return;
        }
        _settings.Email = _email.Text.Trim();
        _settings.Token = _token.Text.Trim();
        _settings.RememberCredentials = _remember.Checked;
        _settings.AssetRipperPath = _assetRipper.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "***" + (at >= 0 ? email[at..] : string.Empty);
        return email[..1] + "***" + email[(at - 1)..];
    }
}
