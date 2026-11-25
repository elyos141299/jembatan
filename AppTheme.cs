using System;
using System.Drawing;
using Microsoft.Win32;

namespace Jembatan;

/// <summary>
/// Palet MuseLink v3.0.0 — SATU tampilan terkunci dari blueprint
/// yang disetujui user (25 Nov 2026): basis Lavender iPhone 17
/// dipasangkan "Split-Complement Sage + Pale Gold" (Pasangan 4).
/// Pilihan tema Terang/Gelap/Perangkat PENSIUN — blueprint adalah
/// satu desain final, bukan keluarga tema. Enum Choice, Current
/// dan IsDark dipertahankan sebagai shim kompatibilitas (IsDark
/// kini selalu false) agar sisa pemanggil lama tetap terkompilasi;
/// nilai Settings.Theme lama tidak lagi berpengaruh.
///
/// Aturan jatah warna blueprint: ±60% kanvas lavender pucat, ±30%
/// struktur violet, aksen jenuh hanya untuk aksi; sage &amp; emas
/// pucat HANYA permukaan aksen (teks sage di atas terang gagal
/// kontras) — status Terhubung memakai sage yang diperdalam.
/// </summary>
public static class AppTheme
{
    public enum Choice { Device, Light, Dark }

    /// <summary>Shim kompatibilitas: kini selalu Light.</summary>
    public static Choice Current => Choice.Light;

    /// <summary>Shim kompatibilitas: desain v3.0 selalu terang.</summary>
    public static bool IsDark => false;

    /// <summary>Tema aplikasi Windows (dipakai shim lama; kini tak berpengaruh).</summary>
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

    // ---------- Palet Pasangan 4 (blueprint final) ----------
    /// <summary>Kanvas lavender pucat #F3F0FF.</summary>
    public static Color Bg => Color.FromArgb(243, 240, 255);
    /// <summary>Permukaan panel #E6E6FA.</summary>
    public static Color Panel => Color.FromArgb(230, 230, 250);
    /// <summary>Kartu putih.</summary>
    public static Color Card => Color.FromArgb(255, 255, 255);
    /// <summary>Panel log = sumur tinta #3D3461.</summary>
    public static Color LogBg => Color.FromArgb(61, 52, 97);
    /// <summary>Kartu di atas permukaan tinta (panel kendali gelap +
    /// Pengaturan, v3.2.0): tinta yang terangkat sedikit dari LogBg,
    /// tepinya memakai RailHover — keluarga yang sama dengan log.</summary>
    public static Color InkCard => Color.FromArgb(73, 62, 119);
    /// <summary>Tinta violet pekat #3D3461 (teks + rel + strip kepala chat).</summary>
    public static Color Ink => Color.FromArgb(61, 52, 97);
    /// <summary>Teks sekunder violet #6B59A0.</summary>
    public static Color InkDim => Color.FromArgb(107, 89, 160);
    /// <summary>Teks keterangan pucat #9B89C0 (hanya keterangan kecil).</summary>
    public static Color InkFaint => Color.FromArgb(155, 137, 192);
    /// <summary>Garis rambut keunguan #D5CFEC.</summary>
    public static Color Hairline => Color.FromArgb(213, 207, 236);
    /// <summary>Aksen aksi #6B59A0 (teks putih di atasnya ±5,9:1).</summary>
    public static Color Accent => Color.FromArgb(107, 89, 160);
    /// <summary>Aksen tekan/hover #7851A9.</summary>
    public static Color AccentStrong => Color.FromArgb(120, 81, 169);
    /// <summary>Pasangan aksen permukaan: emas pucat #F2D492.</summary>
    public static Color AccentAlt => Color.FromArgb(242, 212, 146);
    /// <summary>Sage #9CAF88 — permukaan aksen saja, bukan teks.</summary>
    public static Color Sage => Color.FromArgb(156, 175, 136);
    /// <summary>Emas pucat #F2D492 — permukaan aksen saja.</summary>
    public static Color Gold => Color.FromArgb(242, 212, 146);
    /// <summary>Rel ikon — v3.3.2 (koreksi user): satu tingkat
    /// LEBIH GELAP dari LogBg #3D3461 agar rel dan panel/tab yang
    /// kebuka terbedakan oleh gradasi warna, bukan menyatu.</summary>
    public static Color Rail => Color.FromArgb(48, 41, 76);
    /// <summary>Garis pembatas rel &lt;-&gt; panel (v3.3.2): ungu
    /// menengah, lebih terang dari kedua tinta di sisinya.</summary>
    public static Color DividerInk => Color.FromArgb(101, 90, 150);
    /// <summary>Latar titik status bar judul (v3.3.2): sampel
    /// gradasi bar judul (#DDD6F5 -&gt; Bg) di posisi titik —
    /// menggantikan Transparan yang tampil sebagai kotak gelap.</summary>
    public static Color TitleDotBg => Color.FromArgb(222, 216, 246);
    /// <summary>Hover/aktif di atas rel tinta #54497A.</summary>
    public static Color RailHover => Color.FromArgb(84, 73, 122);
    /// <summary>Teks pucat di atas tinta #E9E6FA.</summary>
    public static Color InkOnDark => Color.FromArgb(233, 230, 250);
    /// <summary>Status terhubung: sage diperdalam #6B8F5E (lolos kontras).</summary>
    public static Color Green => Color.FromArgb(107, 143, 94);
    /// <summary>Galat #B3261E.</summary>
    public static Color Red => Color.FromArgb(179, 38, 30);

    // Merah terang untuk LATAR GELAP (log tinta): Red biasa di atas
    // LogBg kontrasnya hanya ±1,7:1 — baris galat nyaris tak terbaca
    // (temuan v3.1.0). Salmon ini ±5,7:1 di atas LogBg.
    public static Color RedOnDark => Color.FromArgb(242, 164, 156);
    /// <summary>Isian hover terang #E9E5F6.</summary>
    public static Color LightFill => Color.FromArgb(233, 229, 246);
    /// <summary>Titik menunggu: kuningan #C8A24A.</summary>
    public static Color DotWaiting => Color.FromArgb(200, 162, 74);
    /// <summary>Titik berhenti #9A94AD.</summary>
    public static Color DotStopped => Color.FromArgb(154, 148, 173);
    public static Color ErrorBg => Color.FromArgb(246, 227, 225);
    public static Color ErrorInk => Color.FromArgb(140, 43, 35);
}
