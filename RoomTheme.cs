using System.Drawing;

namespace Jembatan;

/// <summary>
/// Token warna Control Room v4.0.0 (rombak UI mengikuti brief
/// user 26 Nov 2026, mockup disetujui user): kanvas arang gelap
/// netral dengan aksen Pasangan 4 yang terkunci (violet, sage,
/// emas pucat) dibawa sebagai aksen — bukan kanvas. Token lama
/// AppTheme TIDAK diubah: flyout Chat/Log dan SettingsForm
/// mempertahankan tampilannya sampai tahap rombak masing-masing.
/// </summary>
public static class RoomTheme
{
    /// <summary>Kanvas utama #131316.</summary>
    public static Color Bg => Color.FromArgb(0x13, 0x13, 0x16);
    /// <summary>Sidebar #17171B.</summary>
    public static Color Sidebar => Color.FromArgb(0x17, 0x17, 0x1B);
    /// <summary>Permukaan panel #1B1B20.</summary>
    public static Color Panel => Color.FromArgb(0x1B, 0x1B, 0x20);
    /// <summary>Kartu #212129.</summary>
    public static Color Card => Color.FromArgb(0x21, 0x21, 0x29);
    /// <summary>Tepi kartu #2C2C36.</summary>
    public static Color CardEdge => Color.FromArgb(0x2C, 0x2C, 0x36);
    /// <summary>Teks utama #ECECF1.</summary>
    public static Color Text => Color.FromArgb(0xEC, 0xEC, 0xF1);
    /// <summary>Teks sekunder #A6A6B0.</summary>
    public static Color Dim => Color.FromArgb(0xA6, 0xA6, 0xB0);
    /// <summary>Teks keterangan #6E6E78.</summary>
    public static Color Faint => Color.FromArgb(0x6E, 0x6E, 0x78);
    /// <summary>Violet Pasangan 4 diperterang untuk latar gelap #8B6CC1.</summary>
    public static Color Accent => Color.FromArgb(0x8B, 0x6C, 0xC1);
    /// <summary>Violet pekat Pasangan 4 #7851A9.</summary>
    public static Color AccentDeep => Color.FromArgb(0x78, 0x51, 0xA9);
    /// <summary>Sage Pasangan 4 #9CAF88.</summary>
    public static Color Sage => Color.FromArgb(0x9C, 0xAF, 0x88);
    /// <summary>Emas pucat Pasangan 4 #F2D492.</summary>
    public static Color Gold => Color.FromArgb(0xF2, 0xD4, 0x92);
    /// <summary>Hijau live (sama dengan titik status lama) #5BBB63.</summary>
    public static Color Green => Color.FromArgb(0x5B, 0xBB, 0x63);
    /// <summary>Merah teks galat di latar gelap #E5534B.</summary>
    public static Color Red => Color.FromArgb(0xE5, 0x53, 0x4B);
    /// <summary>Isian tombol Emergency Stop #8C2B23.</summary>
    public static Color RedFill => Color.FromArgb(0x8C, 0x2B, 0x23);
    /// <summary>Latar strip banner HALT #4A1D18.</summary>
    public static Color RedBanner => Color.FromArgb(0x4A, 0x1D, 0x18);
    /// <summary>Hover baris navigasi #26262E.</summary>
    public static Color Hover => Color.FromArgb(0x26, 0x26, 0x2E);
    /// <summary>Garis rambut #2A2A32.</summary>
    public static Color Hairline => Color.FromArgb(0x2A, 0x2A, 0x32);
}
