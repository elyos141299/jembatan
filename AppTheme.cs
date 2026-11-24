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
    public static Color Bg => IsDark ? Color.FromArgb(22, 24, 43) : Color.FromArgb(245, 245, 247);
    public static Color Card => IsDark ? Color.FromArgb(35, 38, 66) : Color.FromArgb(255, 255, 255);
    // v2.8.0 (rombak gelap modern): permukaan panel (panel kiri, bar
    // judul, strip status) satu tingkat di atas Bg; area log adalah
    // "sumur" inset satu tingkat di bawah Bg. Terang: Panel = Bg dan
    // LogBg = putih kartu — tampang terang tidak berubah.
    public static Color Panel => IsDark ? Color.FromArgb(16, 18, 34) : Color.FromArgb(245, 245, 247);
    public static Color LogBg => IsDark ? Color.FromArgb(13, 14, 26) : Color.FromArgb(255, 255, 255);
    public static Color Ink => IsDark ? Color.FromArgb(237, 238, 247) : Color.FromArgb(29, 29, 31);
    public static Color InkDim => IsDark ? Color.FromArgb(181, 184, 214) : Color.FromArgb(110, 110, 115);
    public static Color InkFaint => IsDark ? Color.FromArgb(122, 126, 163) : Color.FromArgb(174, 174, 178);
    public static Color Hairline => IsDark ? Color.FromArgb(52, 56, 96) : Color.FromArgb(227, 227, 232);
    public static Color Accent => IsDark ? Color.FromArgb(88, 101, 242) : Color.FromArgb(0, 113, 227);
    // v2.9.0 (Midnight Rich): pasangan gradasi aksen — fuchsia,
    // hanya dipakai sebagai ujung gradasi (tombol & logo).
    public static Color AccentAlt => Color.FromArgb(235, 69, 158);
    public static Color Green => IsDark ? Color.FromArgb(35, 165, 90) : Color.FromArgb(52, 199, 89);
    public static Color Red => IsDark ? Color.FromArgb(242, 63, 67) : Color.FromArgb(255, 59, 48);
    public static Color LightFill => IsDark ? Color.FromArgb(42, 45, 79) : Color.FromArgb(232, 232, 237);
    public static Color DotWaiting => IsDark ? Color.FromArgb(240, 178, 50) : Color.FromArgb(255, 159, 10);
    public static Color DotStopped => IsDark ? Color.FromArgb(128, 132, 142) : Color.FromArgb(174, 174, 178);
    public static Color ErrorBg => IsDark ? Color.FromArgb(46, 24, 30) : Color.FromArgb(255, 244, 204);
    public static Color ErrorInk => IsDark ? Color.FromArgb(255, 150, 153) : Color.FromArgb(138, 109, 0);
}
