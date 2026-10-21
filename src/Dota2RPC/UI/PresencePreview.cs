using System.Drawing.Drawing2D;
using Dota2RPC.Discord;

namespace Dota2RPC.UI;

/// <summary>Предпросмотр статуса в стиле карточки профиля Discord. Таймер считается так же, как в Discord.</summary>
internal sealed class PresencePreview : Control
{
    PresenceActivity? _activity;
    string _appName = "Dota 2";
    string _emptyText = "Статус не отображается";
    string? _emptyHint;

    public PresencePreview()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Card;
        Height = (int)(150 * DeviceDpi / 96f);
        Dock = DockStyle.Top;
        Margin = new Padding(0, 0, 0, 10);
        ImageCache.ImageLoaded += OnImageLoaded;
    }

    void OnImageLoaded()
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(Invalidate);
    }

    public void SetAppName(string? name)
    {
        var next = string.IsNullOrWhiteSpace(name) ? "Dota 2" : name;
        if (next == _appName) return;
        _appName = next;
        Invalidate();
    }

    public void SetActivity(PresenceActivity? activity, string emptyText, string? emptyHint)
    {
        var changed = _activity?.Signature != activity?.Signature || _emptyText != emptyText || _emptyHint != emptyHint;
        _activity = activity;
        _emptyText = emptyText;
        _emptyHint = emptyHint;
        // Таймер «прошло» меняется каждую секунду — перерисовываем всегда, когда есть время.
        if (changed || activity?.StartUnix is not null || activity?.EndUnix is not null) Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ImageCache.ImageLoaded -= OnImageLoaded;
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Theme.HighQuality(g);
        var s = DeviceDpi / 96f;

        var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.RoundRect(card, 10 * s))
        using (var brush = new SolidBrush(Theme.DiscordCard))
        {
            g.FillPath(brush, path);
        }

        using var headerFont = Theme.Font(8f, FontStyle.Bold);
        using var nameFont = Theme.Font(10.5f, FontStyle.Bold);
        using var textFont = Theme.Font(9.5f);

        var pad = 14 * s;
        TextRenderer.DrawText(g, "ИГРАЕТ", headerFont, new Point((int)pad, (int)(10 * s)), Theme.DiscordMuted);

        if (_activity is null)
        {
            var r = new Rectangle((int)pad, (int)(40 * s), Width - (int)(2 * pad), (int)(30 * s));
            TextRenderer.DrawText(g, _emptyText, nameFont, r, Theme.DiscordMuted, TextFormatFlags.EndEllipsis);
            if (_emptyHint is not null)
            {
                var hint = new Rectangle((int)pad, (int)(66 * s), Width - (int)(2 * pad), Height - (int)(76 * s));
                TextRenderer.DrawText(g, _emptyHint, textFont, hint, Theme.DiscordMuted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }
            return;
        }

        var big = 92 * s;
        var imgRect = new RectangleF(pad, 34 * s, big, big);
        DrawImage(g, ImageCache.TryGet(PreviewUrl(_activity.LargeImage)), imgRect, 10 * s, Color.FromArgb(49, 51, 56));
        if (IsAnimation(_activity.LargeImage))
        {
            using var badgeFont = Theme.Font(7f, FontStyle.Bold);
            var badge = new Rectangle((int)(imgRect.X + 4 * s), (int)(imgRect.Y + 4 * s), (int)(34 * s), (int)(15 * s));
            using (var bg = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
            using (var path = Theme.RoundRect(badge, 4 * s))
                g.FillPath(bg, path);
            TextRenderer.DrawText(g, "▶", badgeFont, badge, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (!string.IsNullOrEmpty(_activity.SmallImage))
        {
            var small = 30 * s;
            var smallRect = new RectangleF(imgRect.Right - small + 6 * s, imgRect.Bottom - small + 6 * s, small, small);
            using (var ring = new SolidBrush(Theme.DiscordCard))
                g.FillEllipse(ring, RectangleF.Inflate(smallRect, 3 * s, 3 * s));
            DrawImage(g, ImageCache.TryGet(_activity.SmallImage), smallRect, small / 2, Color.FromArgb(49, 51, 56));
        }

        var x = (int)(imgRect.Right + 16 * s);
        var w = Width - x - (int)pad;
        var y = (int)(imgRect.Y + 2 * s);
        var line = (int)(21 * s);
        TextRenderer.DrawText(g, _appName, nameFont, new Rectangle(x, y, w, line), Theme.DiscordText, TextFormatFlags.EndEllipsis);
        y += line;
        TextRenderer.DrawText(g, _activity.Details, textFont, new Rectangle(x, y, w, line), Theme.DiscordText, TextFormatFlags.EndEllipsis);
        y += line;
        if (!string.IsNullOrWhiteSpace(_activity.State))
        {
            TextRenderer.DrawText(g, _activity.State, textFont, new Rectangle(x, y, w, line), Theme.DiscordText, TextFormatFlags.EndEllipsis);
            y += line;
        }
        if (TimerText(_activity) is { } timer)
            TextRenderer.DrawText(g, timer, textFont, new Rectangle(x, y, w, line), Theme.Green, TextFormatFlags.EndEllipsis);
    }

    static bool IsAnimation(string? url) =>
        url is not null && url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Для анимации (WebP) показываем статичную иконку героя: WinForms не умеет WebP.</summary>
    static string? PreviewUrl(string? url) =>
        IsAnimation(url) ? Core.HeroCatalog.HeroImageUrl(Path.GetFileNameWithoutExtension(new Uri(url!).AbsolutePath)) : url;

    internal static string? TimerText(PresenceActivity a)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (a.EndUnix is { } end)
            return Format(Math.Max(0, end - now)) + " осталось";
        if (a.StartUnix is { } start)
            return Format(Math.Max(0, now - start)) + " прошло";
        return null;
    }

    static string Format(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    static void DrawImage(Graphics g, Image? image, RectangleF rect, float radius, Color placeholder)
    {
        using var path = Theme.RoundRect(rect, radius);
        var state = g.Save();
        g.SetClip(path);
        if (image is null)
        {
            using var brush = new SolidBrush(placeholder);
            g.FillPath(brush, path);
        }
        else
        {
            // Как и Discord, заполняем квадрат с обрезкой по центру.
            var scale = Math.Max(rect.Width / image.Width, rect.Height / image.Height);
            var w = image.Width * scale;
            var h = image.Height * scale;
            g.DrawImage(image, rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
        }
        g.Restore(state);
    }
}
