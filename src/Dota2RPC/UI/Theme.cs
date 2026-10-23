using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Dota2RPC.UI;

internal static class Theme
{
    // Тема «тлеющие угли»: фиолетово-чёрный фон, огненно-малиновые градиенты и золото — в тон логотипу.
    public static readonly Color Background = Color.FromArgb(13, 11, 18);
    public static readonly Color Sidebar = Color.FromArgb(18, 14, 24);
    public static readonly Color Card = Color.FromArgb(26, 21, 33);
    public static readonly Color CardBorder = Color.FromArgb(48, 38, 58);
    public static readonly Color Input = Color.FromArgb(14, 11, 19);
    public static readonly Color Hover = Color.FromArgb(36, 29, 46);
    public static readonly Color Text = Color.FromArgb(242, 236, 242);
    public static readonly Color Muted = Color.FromArgb(160, 150, 170);
    public static readonly Color Accent = Color.FromArgb(240, 86, 58);
    public static readonly Color AccentHover = Color.FromArgb(255, 112, 80);
    public static readonly Color AccentA = Color.FromArgb(255, 110, 60);   // начало градиента (угли)
    public static readonly Color AccentB = Color.FromArgb(214, 38, 90);    // конец градиента (малиновый)
    public static readonly Color Gold = Color.FromArgb(246, 200, 110);
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

    /// <summary>Фирменный градиент слева направо; alpha — для полупрозрачных подсветок.</summary>
    public static LinearGradientBrush AccentBrush(RectangleF r, int alpha = 255, bool lighter = false)
    {
        var rect = r.Width < 1 || r.Height < 1 ? new RectangleF(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)) : r;
        var a = lighter ? ControlPaint.Light(AccentA, 0.25f) : AccentA;
        var b = lighter ? ControlPaint.Light(AccentB, 0.25f) : AccentB;
        return new LinearGradientBrush(rect, Color.FromArgb(alpha, a), Color.FromArgb(alpha, b), LinearGradientMode.Horizontal);
    }

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
