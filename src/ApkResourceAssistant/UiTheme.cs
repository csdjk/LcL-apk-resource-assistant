using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace GooglePlayApkDownloader;

internal static class UiTheme
{
    public static readonly Color Window = Color.FromArgb(245, 247, 250);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(222, 226, 232);
    public static readonly Color Text = Color.FromArgb(28, 35, 45);
    public static readonly Color Muted = Color.FromArgb(101, 111, 125);
    public static readonly Color Primary = Color.FromArgb(37, 99, 235);
    public static readonly Color PrimaryHover = Color.FromArgb(29, 78, 216);
    public static readonly Color PrimarySoft = Color.FromArgb(239, 246, 255);
    public static readonly Color Success = Color.FromArgb(22, 163, 74);
    public static readonly Color Warning = Color.FromArgb(217, 119, 6);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);

    public static Button PrimaryButton(string text, int height = 40)
    {
        var button = BaseButton(text, height);
        button.BackColor = Primary;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = PrimaryHover;
        button.FlatAppearance.MouseDownBackColor = PrimaryHover;
        return button;
    }

    public static Button SecondaryButton(string text, int height = 36)
    {
        var button = BaseButton(text, height);
        button.BackColor = Color.White;
        button.ForeColor = Text;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(248, 250, 252);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(241, 245, 249);
        return button;
    }

    public static Button LinkButton(string text)
    {
        var button = BaseButton(text, 30);
        button.AutoSize = true;
        button.BackColor = Color.Transparent;
        button.ForeColor = Primary;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = PrimarySoft;
        return button;
    }

    public static Label Heading(string text, float size = 15F) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", size, FontStyle.Bold),
        ForeColor = Text,
        Margin = new Padding(0)
    };

    public static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 9F),
        ForeColor = Muted,
        Margin = new Padding(0)
    };

    public static TextBox TextInput(string? text = null, bool readOnly = false) => new()
    {
        Text = text ?? string.Empty,
        ReadOnly = readOnly,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = readOnly ? Color.FromArgb(248, 250, 252) : Color.White,
        ForeColor = Text,
        Font = new Font("Microsoft YaHei UI", 9.5F),
        Margin = new Padding(0, 4, 0, 4)
    };

    private static Button BaseButton(string text, int height) => new()
    {
        Text = text,
        Height = height,
        Cursor = Cursors.Hand,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
        UseVisualStyleBackColor = false,
        Margin = new Padding(0)
    };
}

internal sealed class CardPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = UiTheme.Border;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 12;

    public CardPanel()
    {
        BackColor = UiTheme.Card;
        Padding = new Padding(20);
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = RoundedRectangle(rect, CornerRadius);
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawPath(pen, path);
    }

    internal static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var diameter = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ModeCard : Button
{
    private bool _selected;
    private bool _hovered;

    public int ModeIndex { get; }
    public string TitleText { get; }
    public string SubtitleText { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    public ModeCard(int modeIndex, string title, string subtitle)
    {
        ModeIndex = modeIndex;
        TitleText = title;
        SubtitleText = subtitle;
        Height = 104;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiTheme.Window);
        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        using var path = CardPanel.RoundedRectangle(rect, 12);
        using var fill = new SolidBrush(Selected ? UiTheme.PrimarySoft : _hovered ? Color.FromArgb(250, 252, 255) : UiTheme.Card);
        using var border = new Pen(Selected ? UiTheme.Primary : UiTheme.Border, Selected ? 1.8F : 1F);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        var circle = new Rectangle(18, 20, 36, 36);
        using var circleBrush = new SolidBrush(Selected ? UiTheme.Primary : Color.FromArgb(238, 241, 245));
        e.Graphics.FillEllipse(circleBrush, circle);
        using var numberFont = new Font("Segoe UI", 11F, FontStyle.Bold);
        using var titleFont = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        using var subtitleFont = new Font("Microsoft YaHei UI", 8.5F);
        TextRenderer.DrawText(e.Graphics, (ModeIndex + 1).ToString(), numberFont, circle,
            Selected ? Color.White : UiTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var titleRect = new Rectangle(68, 17, Width - 84, 28);
        TextRenderer.DrawText(e.Graphics, TitleText, titleFont, titleRect, UiTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var subtitleRect = new Rectangle(68, 47, Width - 84, 40);
        TextRenderer.DrawText(e.Graphics, SubtitleText, subtitleFont, subtitleRect, UiTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        if (Focused)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(rect, -5, -5));
    }
}
