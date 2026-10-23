using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Dota2RPC.UI;

/// <summary>Карточка со скруглёнными углами и заголовком.</summary>
internal sealed class Card : Panel
{
    public Card(string title, string? subtitle = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Card;
        Padding = new Padding(18, 14, 18, 16);
        Margin = new Padding(0, 0, 0, 14);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        Body = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Card,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        Body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Body.Controls.Add(new Label
        {
            Text = title,
            Font = Theme.Font(11.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, subtitle is null ? 10 : 2),
        });
        if (subtitle is not null)
        {
            Body.Controls.Add(new WrapLabel
            {
                Text = subtitle,
                ForeColor = Theme.Muted,
                Font = Theme.Font(9f),
                Margin = new Padding(0, 0, 0, 10),
            });
        }
        Controls.Add(Body);
    }

    public TableLayoutPanel Body { get; }

    public T Add<T>(T control) where T : Control
    {
        Body.Controls.Add(control);
        return control;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        Theme.HighQuality(e.Graphics);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var s = DeviceDpi / 96f;
        using var path = Theme.RoundRect(r, 12 * s);
        using var fill = new SolidBrush(Theme.Card);
        using var pen = new LinearGradientBrush(r, Color.FromArgb(70, 54, 82), Color.FromArgb(36, 29, 44), LinearGradientMode.Vertical);
        using var border = new Pen(pen, 1);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        // Короткая огненная полоса по верхнему краю, тающая вправо
        var line = new RectangleF(14 * s, 0.5f, Math.Min(Width * 0.45f, 220 * s), 2.5f * s);
        using var glow = new LinearGradientBrush(line, Theme.AccentA, Color.FromArgb(0, Theme.AccentB), LinearGradientMode.Horizontal);
        e.Graphics.FillRectangle(glow, line);
    }
}

/// <summary>Панель прокрутки, которая не «прыгает» к элементу при получении им фокуса.</summary>
internal sealed class ScrollPanel : Panel
{
    public ScrollPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    /// <summary>
    /// Держать страницу вверху, пока пользователь сам не прокрутит: после показа окна карточки
    /// пересчитывают высоту текста, и без этого панель иногда «уезжает» вниз.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool KeepAtTop { get; set; }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (KeepAtTop && AutoScrollPosition != Point.Empty) AutoScrollPosition = Point.Empty;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        KeepAtTop = false;
        base.OnMouseWheel(e);
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        KeepAtTop = false;
        base.OnScroll(se);
    }
}

/// <summary>Метка с переносом строк по ширине родителя.</summary>
internal class WrapLabel : Label
{
    public WrapLabel()
    {
        AutoSize = true;
        BackColor = Color.Transparent;
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent is not null) Parent.Resize += (_, _) => UpdateMaxWidth();
        UpdateMaxWidth();
    }

    void UpdateMaxWidth()
    {
        if (Parent is null) return;
        var w = Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal - 2;
        if (w > 50) MaximumSize = new Size(w, 0);
    }
}

/// <summary>Переключатель в стиле Windows 11.</summary>
[DefaultEvent(nameof(CheckedChanged))]
internal sealed class ToggleSwitch : Control
{
    bool _checked;
    bool _hover;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(46, 24);
        Cursor = Cursors.Hand;
        TabStop = true;
        BackColor = Color.Transparent;
    }

    public event EventHandler? CheckedChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Установка значения без события (при загрузке настроек).</summary>
    public void SetSilently(bool value)
    {
        _checked = value;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) Checked = !Checked;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.HighQuality(g);
        var h = Math.Min(Height - 2, (int)(22 * DeviceDpi / 96f));
        var w = Math.Min(Width - 2, h * 2);
        var track = new RectangleF(1, (Height - h) / 2f, w, h);
        using (var path = Theme.RoundRect(track, h / 2f))
        {
            if (_checked && Enabled)
            {
                using var brush = Theme.AccentBrush(track, lighter: _hover);
                g.FillPath(brush, path);
            }
            else
            {
                var color = !Enabled ? Color.FromArgb(40, 34, 48) : _hover ? Color.FromArgb(68, 56, 80) : Color.FromArgb(52, 43, 62);
                using var brush = new SolidBrush(color);
                g.FillPath(brush, path);
            }
            if (Focused)
            {
                using var pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1);
                g.DrawPath(pen, path);
            }
        }
        var knob = h - 6f;
        var x = _checked ? track.Right - knob - 3 : track.X + 3;
        using var knobBrush = new SolidBrush(Enabled ? Color.White : Color.FromArgb(120, 120, 120));
        g.FillEllipse(knobBrush, x, track.Y + 3, knob, knob);
    }
}

internal enum ButtonKind { Primary, Secondary, Danger, Discord }

/// <summary>Плоская кнопка со скруглёнными углами.</summary>
internal sealed class FlatButton : Control
{
    bool _hover;
    bool _pressed;

    public FlatButton(string text, ButtonKind kind = ButtonKind.Secondary)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        Text = text;
        Kind = kind;
        Font = Theme.Font(9.75f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        TabStop = true;
        Margin = new Padding(0, 0, 8, 0);
        AutoSize = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get; set; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        var scale = DeviceDpi / 96f;
        return new Size(text.Width + (int)(32 * scale), Math.Max(text.Height + (int)(16 * scale), (int)(34 * scale)));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.HighQuality(g);
        var (bg, fg, border) = Kind switch
        {
            ButtonKind.Primary => (Theme.Accent, Color.White, Color.Empty),
            ButtonKind.Discord => (Theme.Blurple, Color.White, Color.Empty),
            ButtonKind.Danger => (Color.FromArgb(62, 24, 36), Color.FromArgb(255, 150, 160), Color.FromArgb(120, 44, 64)),
            _ => (Color.FromArgb(38, 31, 48), Theme.Text, Color.FromArgb(66, 54, 78)),
        };
        if (!Enabled)
        {
            bg = Color.FromArgb(32, 35, 42);
            fg = Theme.Gray;
            border = Color.Empty;
        }
        else if (_pressed) bg = ControlPaint.Dark(bg, 0.1f);
        else if (_hover) bg = ControlPaint.Light(bg, 0.15f);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundRect(r, 7 * DeviceDpi / 96f);
        if (Enabled && Kind is ButtonKind.Primary or ButtonKind.Discord)
        {
            var (a, b) = Kind == ButtonKind.Primary
                ? (Theme.AccentA, Theme.AccentB)
                : (Color.FromArgb(88, 101, 242), Color.FromArgb(128, 92, 255));
            if (_hover && !_pressed) (a, b) = (ControlPaint.Light(a, 0.2f), ControlPaint.Light(b, 0.2f));
            if (_pressed) (a, b) = (ControlPaint.Dark(a, 0.05f), ControlPaint.Dark(b, 0.05f));
            using var grad = new LinearGradientBrush(r, a, b, LinearGradientMode.Horizontal);
            g.FillPath(grad, path);
            // лёгкий блик сверху
            using var shine = new LinearGradientBrush(r, Color.FromArgb(45, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical);
            g.FillPath(shine, path);
        }
        else
        {
            using var brush = new SolidBrush(bg);
            g.FillPath(brush, path);
        }
        if (border != Color.Empty)
        {
            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Color.FromArgb(160, 255, 255, 255));
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Строка состояния: цветной индикатор, заголовок и значение.</summary>
internal sealed class StatusItem : Control
{
    Color _dot = Theme.Gray;
    string _value = "—";
    string? _hint;

    public StatusItem(string title)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Title = title;
        BackColor = Theme.Card;
        Dock = DockStyle.Top;
        Margin = new Padding(0, 0, 0, 6);
        Height = (int)(54 * DeviceDpi / 96f);
    }

    public string Title { get; }

    static readonly ToolTip Tip = new() { InitialDelay = 300, AutoPopDelay = 20000 };

    public void Set(Color dot, string value, string? hint = null)
    {
        if (_dot == dot && _value == value && _hint == hint) return;
        _dot = dot;
        _value = value;
        _hint = hint;
        Tip.SetToolTip(this, hint is null ? value : value + Environment.NewLine + hint);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Theme.HighQuality(g);
        var s = DeviceDpi / 96f;
        var dot = 10 * s;
        using (var halo = new SolidBrush(Color.FromArgb(50, _dot))) g.FillEllipse(halo, 0, 8 * s - 3 * s, dot + 6 * s, dot + 6 * s);
        using (var brush = new SolidBrush(_dot)) g.FillEllipse(brush, 3 * s, 8 * s, dot, dot);

        var x = (int)(24 * s);
        using var titleFont = Theme.Font(8.5f);
        using var valueFont = Theme.Font(10f, FontStyle.Bold);
        using var hintFont = Theme.Font(8.5f);
        TextRenderer.DrawText(g, Title.ToUpperInvariant(), titleFont, new Rectangle(x, 0, Width - x, (int)(18 * s)), Theme.Muted, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, _value, valueFont, new Rectangle(x, (int)(16 * s), Width - x, (int)(20 * s)), Theme.Text, TextFormatFlags.EndEllipsis);
        if (_hint is not null)
            TextRenderer.DrawText(g, _hint, hintFont, new Rectangle(x, (int)(35 * s), Width - x, (int)(18 * s)), Theme.Muted, TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Кнопка навигации боковой панели.</summary>
internal sealed class NavButton : Control
{
    bool _hover;
    bool _selected;

    public NavButton(string text, string glyph)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Text = text;
        Glyph = glyph;
        Cursor = Cursors.Hand;
        Dock = DockStyle.Top;
        Height = (int)(42 * DeviceDpi / 96f);
        BackColor = Theme.Sidebar;
        Font = Theme.Font(10f, FontStyle.Bold);
    }

    public string Glyph { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Theme.HighQuality(g);
        var s = DeviceDpi / 96f;
        var r = new RectangleF(10 * s, 3 * s, Width - 20 * s, Height - 6 * s);
        if (_selected)
        {
            using var path = Theme.RoundRect(r, 9 * s);
            using var brush = Theme.AccentBrush(r, alpha: 70);
            g.FillPath(brush, path);
            using var edge = new Pen(Color.FromArgb(90, Theme.AccentA), 1);
            g.DrawPath(edge, path);
            var barRect = new RectangleF(10 * s, Height / 2f - 10 * s, 3 * s, 20 * s);
            using var bar = Theme.RoundRect(barRect, 1.5f * s);
            using var barBrush = new LinearGradientBrush(barRect, Theme.Gold, Theme.AccentA, LinearGradientMode.Vertical);
            g.FillPath(barBrush, bar);
        }
        else if (_hover)
        {
            using var path = Theme.RoundRect(r, 9 * s);
            using var brush = new SolidBrush(Theme.Hover);
            g.FillPath(brush, path);
        }
        using var glyphFont = new Font("Segoe Fluent Icons", 11f);
        using var fallbackGlyph = new Font("Segoe MDL2 Assets", 11f);
        var gf = glyphFont.Name == "Segoe Fluent Icons" ? glyphFont : fallbackGlyph;
        var color = _selected ? Theme.Text : Theme.Muted;
        TextRenderer.DrawText(g, Glyph, gf, new Rectangle((int)(24 * s), 0, (int)(24 * s), Height), _selected ? Theme.Gold : color, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(56 * s), 0, Width - (int)(60 * s), Height), color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal static class Ui
{
    /// <summary>Строка «заголовок + описание + переключатель».</summary>
    public static TableLayoutPanel ToggleRow(string title, string description, ToggleSwitch toggle)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Theme.Card,
            Margin = new Padding(0, 4, 0, 8),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = Theme.Text,
            Font = Theme.Font(10f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 1),
        }, 0, 0);
        row.Controls.Add(new WrapLabel
        {
            Text = description,
            ForeColor = Theme.Muted,
            Font = Theme.Font(9f),
            Margin = new Padding(0),
        }, 0, 1);
        toggle.Anchor = AnchorStyles.Right;
        toggle.Margin = new Padding(16, 4, 0, 0);
        row.Controls.Add(toggle, 1, 0);
        row.SetRowSpan(toggle, 2);
        return row;
    }

    public static FlowLayoutPanel ButtonRow(params Control[] buttons)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            WrapContents = false,
            BackColor = Theme.Card,
            Margin = new Padding(0, 6, 0, 0),
        };
        foreach (var b in buttons)
        {
            b.Margin = new Padding(0, 0, 8, 6);
            flow.Controls.Add(b);
        }
        return flow;
    }

    public static Label Muted(string text, float size = 9f) => new WrapLabel
    {
        Text = text,
        ForeColor = Theme.Muted,
        Font = Theme.Font(size),
        Margin = new Padding(0, 2, 0, 4),
    };

    public static Label Value(string text) => new WrapLabel
    {
        Text = text,
        ForeColor = Theme.Text,
        Font = Theme.Font(9.75f),
        Margin = new Padding(0, 2, 0, 6),
    };

    public static void OpenUrl(string url)
    {
        try
        {
            using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })) { }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось открыть: " + url + "\n" + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })) { }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось открыть папку: " + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
