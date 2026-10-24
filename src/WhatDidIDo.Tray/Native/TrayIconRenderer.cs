using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;
using WhatDidIDo.Tray.Core.Models;

namespace WhatDidIDo.Tray.Native;

/// <summary>
/// Draws the tray icon from the system icon font (TRAY.md T7, section 3.1):
/// the glyph in the taskbar's foreground, red while recording and amber while
/// paused (the Windows critical and caution colors), plus the microphone badge.
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly string FontFamilyName =
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "SegoeIcons.ttf"))
            ? "Segoe Fluent Icons"
            : "Segoe MDL2 Assets";

    private static char Glyph(TrayIconKind kind) => kind switch
    {
        TrayIconKind.Recording => '',  // Record
        TrayIconKind.Paused => '',     // Pause
        TrayIconKind.Stopped => '',    // Stop
        TrayIconKind.Busy => '',       // ProgressRingDots
        TrayIconKind.Recovering => '', // Refresh
        TrayIconKind.Failed => '',     // Error
        _ => '',                       // PowerButton: not running
    };

    private const char Microphone = '';

    /// <summary>The taskbar is light (Settings → Personalization → Colors → "Choose your mode": Windows).</summary>
    public static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }

    private static Color Tint(TrayIconKind kind, bool light) => kind switch
    {
        // SystemFillColorCritical / SystemFillColorCaution, light and dark.
        TrayIconKind.Recording => light ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0xFF, 0x99, 0xA4),
        TrayIconKind.Paused => light ? Color.FromArgb(0x9D, 0x5D, 0x00) : Color.FromArgb(0xFC, 0xE1, 0x00),
        _ => light ? Color.Black : Color.White,
    };

    /// <returns>An HICON the caller owns.</returns>
    public static nint Render(TrayView view, int size, bool lightTaskbar)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            var fg = lightTaskbar ? Color.Black : Color.White;
            // The glyph fills the box; with the badge it shrinks to the top-left 3/4.
            var box = view.MicBadge ? size * 0.78f : size;
            DrawGlyph(g, Glyph(view.Kind), Tint(view.Kind, lightTaskbar), 0, 0, box);
            if (view.MicBadge)
            {
                var badge = size * 0.6f;
                var x = size - badge;
                // A disc in the taskbar's background color keeps the badge readable over the glyph.
                using (var cut = new SolidBrush(lightTaskbar ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x20, 0x20, 0x20)))
                    g.FillEllipse(cut, x - 1, x - 1, badge + 2, badge + 2);
                DrawGlyph(g, Microphone, fg, x, x, badge);
            }
        }
        return bitmap.GetHicon();
    }

    private static void DrawGlyph(Graphics g, char glyph, Color color, float x, float y, float box)
    {
        using var font = new Font(FontFamilyName, box * 0.8f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(glyph.ToString(), font, brush, new RectangleF(x, y, box, box), format);
    }
}
