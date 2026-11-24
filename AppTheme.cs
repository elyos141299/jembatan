using System;
using System.Drawing;
using Microsoft.Win32;

namespace Jembatan;

/// <summary>
/// Tema tampilan MuseLink (v2.5.0, pilihan user): Terang / Gelap /
/// Perangkat (mengikuti tema aplikasi Windows). Palet terang adalah
/// tema iCloud sejak v2.3.3; palet gelap pasangannya bergaya iOS
/// dark (latar nyaris hitam, kartu abu gelap, aksen biru terang).
///
/// MainForm membaca warna lewat properti di sini (bukan konstanta
/// lagi), dan ApplyTheme() mengecat ulang semua kontrol saat tema
/// berganti — termasuk mengikuti perubahan tema Windows secara
/// langsung bila pilihan = Perangkat.
/// </summary>
public static class AppTheme
{
    public enum Choice { Device, Light, Dark }

    /// <summary>Pilihan tersimpan saat ini (dari Settings).</summary>
    public static Choice Current
    {
        get
        {
            switch (Settings.Theme)
            {
                case "light": return Choice.Light;
                case "dark": return Choice.Dark;
                default: return Choice.Device;
            }
        }
    }

    /// <summary>True bila tema EFEKTIF saat ini gelap.</summary>
    public static bool IsDark
    {
        get
        {
            switch (Current)
            {
                case Choice.Dark: return true;
                case Choice.Light: return false;
                default: return !SystemUsesLightTheme();
            }
        }
    }

    /// <summary>Tema aplikasi Windows: AppsUseLightTheme 1 = terang, 0 = gelap.</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
            }
        }
        catch { /* anggap terang */ }
        return true;
    }

    // ---------- Palet ----------
    public static Color Bg => IsDark ? Color.FromArgb(16, 16, 18) : Color.FromArgb(245, 245, 247);
    public static Color Card => IsDark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(255, 255, 255);
    public static Color Ink => IsDark ? Color.FromArgb(245, 245, 247) : Color.FromArgb(29, 29, 31);
    public static Color InkDim => IsDark ? Color.FromArgb(152, 152, 159) : Color.FromArgb(110, 110, 115);
    public static Color InkFaint => IsDark ? Color.FromArgb(94, 94, 99) : Color.FromArgb(174, 174, 178);
    public static Color Hairline => IsDark ? Color.FromArgb(56, 56, 58) : Color.FromArgb(227, 227, 232);
    public static Color Accent => IsDark ? Color.FromArgb(10, 132, 255) : Color.FromArgb(0, 113, 227);
    public static Color Green => IsDark ? Color.FromArgb(48, 209, 88) : Color.FromArgb(52, 199, 89);
    public static Color Red => IsDark ? Color.FromArgb(255, 69, 58) : Color.FromArgb(255, 59, 48);
    public static Color LightFill => IsDark ? Color.FromArgb(44, 44, 46) : Color.FromArgb(232, 232, 237);
    public static Color DotWaiting => Color.FromArgb(255, 159, 10);
    public static Color DotStopped => IsDark ? Color.FromArgb(99, 99, 102) : Color.FromArgb(174, 174, 178);
    public static Color ErrorBg => IsDark ? Color.FromArgb(66, 54, 16) : Color.FromArgb(255, 244, 204);
    public static Color ErrorInk => IsDark ? Color.FromArgb(255, 214, 102) : Color.FromArgb(138, 109, 0);
}
