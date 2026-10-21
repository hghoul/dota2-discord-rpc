using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Dota2RPC.UI;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(15, 17, 21);
    public static readonly Color Sidebar = Color.FromArgb(20, 23, 29);
    public static readonly Color Card = Color.FromArgb(26, 30, 38);
    public static readonly Color CardBorder = Color.FromArgb(40, 45, 56);
    public static readonly Color Input = Color.FromArgb(14, 16, 21);
    public static readonly Color Hover = Color.FromArgb(34, 39, 49);
    public static readonly Color Text = Color.FromArgb(232, 234, 238);
    public static readonly Color Muted = Color.FromArgb(142, 150, 164);
    public static readonly Color Accent = Color.FromArgb(222, 70, 52);
    public static readonly Color AccentHover = Color.FromArgb(240, 92, 72);
    public static readonly Color Blurple = Color.FromArgb(88, 101, 242);
    public static readonly Color Green = Color.FromArgb(59, 165, 93);
    public static readonly Color Yellow = Color.FromArgb(240, 178, 50);
    public static readonly Color Red = Color.FromArgb(237, 66, 69);
    public static readonly Color Gray = Color.FromArgb(100, 108, 122);

    // Цвета карточки Discord для предпросмотра
    public static readonly Color DiscordCard = Color.FromArgb(35, 36, 40);
    public static readonly Color DiscordText = Color.FromArgb(242, 243, 245);
    public static readonly Color DiscordMuted = Color.FromArgb(181, 186, 193);

    public const string FontFamily = "Segoe UI";

    public static Font Font(float size, FontStyle style = FontStyle.Regular) => new(FontFamily, size, style);

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void HighQuality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Тёмные полосы прокрутки (тема DarkMode_Explorer, Windows 10 1809+).</summary>
    public static void ApplyDarkScrollBars(Control control)
    {
        void Apply()
        {
            try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch { /* не поддерживается */ }
        }
        if (control.IsHandleCreated) Apply();
        else control.HandleCreated += (_, _) => Apply();
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Тёмный заголовок окна и цвет рамки в Windows 11.</summary>
    public static void ApplyDarkTitleBar(Form form)
    {
        try
        {
            var on = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            var caption = ColorTranslator.ToWin32(Background);
            DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Windows 11)
        }
        catch
        {
            // старые версии Windows — оставляем стандартный заголовок
        }
    }
}
