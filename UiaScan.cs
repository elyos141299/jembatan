using System;
using System.Collections.Generic;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Jembatan;

/// <summary>
/// Peta elemen jendela aktif lewat UI Automation (v2.4.0).
///
/// Selama ini Muse mengklik titik koordinat hasil tebak dari
/// screenshot — meleset itu biasa. UI Automation adalah jalur resmi
/// Windows untuk "membaca" isi jendela: tombol, kolom teks, kotak
/// centang, item daftar, dst. lengkap dengan nama & kotak batasnya.
/// Muse meminta daftarnya (/uia), memilih elemen berdasarkan NAMA,
/// lalu mengklik titik tengahnya (/uia/click) — jauh lebih presisi.
///
/// Dipakai lewat pustaka FlaUI.UIA3 (NuGet): rujukan langsung ke
/// rakitan kerangka UIAutomationClient tidak terselesaikan di CI
/// (build #13 gagal karenanya) — FlaUI membungkus COM UI Automation
/// resmi dan memang dibuat untuk .NET modern.
///
/// Hasil pindai terakhir disimpan statis; /uia/click memakai indeks
/// dari daftar itu. Semua fungsi di sini dipanggil dari thread UI
/// (AgentServer mengurus lewat OnUi) agar konsisten dengan injeksi
/// input lain.
/// </summary>
public static class UiaScan
{
    /// <summary>Satu elemen interaktif hasil pindai.</summary>
    public sealed class Item
    {
        public string Name = "";
        public string Type = "";
        public Rectangle Rect;
        public bool Enabled;

        public int CenterX => Rect.Left + Rect.Width / 2;
        public int CenterY => Rect.Top + Rect.Height / 2;
    }

    private static readonly List<Item> _last = new List<Item>();

    /// <summary>Hasil pindai terakhir (kosong sebelum pindai pertama).</summary>
    public static IReadOnlyList<Item> Last => _last;

    // Tipe kontrol yang bisa diajak berinteraksi — elemen teks murni,
    // gambar, dan wadah tata letak sengaja tidak dimasukkan supaya
    // daftar tetap pendek dan berguna.
    private static readonly HashSet<ControlType> Interesting = new HashSet<ControlType>
    {
        ControlType.Button,
        ControlType.CheckBox,
        ControlType.RadioButton,
        ControlType.ComboBox,
        ControlType.Edit,
        ControlType.Hyperlink,
        ControlType.ListItem,
        ControlType.MenuItem,
        ControlType.TabItem,
        ControlType.TreeItem,
        ControlType.DataItem,
        ControlType.Spinner,
        ControlType.Slider,
        ControlType.Document,
        ControlType.SplitButton,
        ControlType.MenuBar,
    };

    /// <summary>
    /// Pindai jendela yang sedang di depan. Maksimal maxItems elemen
    /// (bawaan 120) supaya jendela raksasa seperti Chrome tidak
    /// menghasilkan daftar ribuan baris. Mengembalikan daftar yang
    /// sama dengan yang tersimpan di Last.
    /// </summary>
    public static List<Item> ScanForeground(int maxItems)
    {
        _last.Clear();
        var fg = NativeInput.ForegroundHandle();
        if (fg == IntPtr.Zero) return _last;

        try
        {
            using (var automation = new UIA3Automation())
            {
                var root = automation.FromHandle(fg);
                if (root == null) return _last;
                var all = root.FindAllDescendants();
                foreach (var el in all)
                {
                    if (_last.Count >= maxItems) break;
                    try
                    {
                        if (!Interesting.Contains(el.ControlType)) continue;
                        if (el.IsOffscreen) continue;
                        var r = el.BoundingRectangle;
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        _last.Add(new Item
                        {
                            Name = el.Name ?? "",
                            Type = el.ControlType.ToString(),
                            Rect = r,
                            Enabled = el.IsEnabled,
                        });
                    }
                    catch { /* elemen hilang di tengah pindai dsb. — lewati */ }
                }
            }
        }
        catch { /* UIA gagal total (jendela aman dsb.) — daftar kosong */ }
        return _last;
    }

    /// <summary>
    /// Pindai jendela tertentu dari handle-nya (v2.6.4) —
    /// untuk /inspect jendela latar. Tidak menyentuh Last
    /// (hasil pindai jendela depan tetap milik /uia/click).
    /// </summary>
    public static List<Item> ScanWindow(IntPtr handle, int maxItems)
    {
        var hasil = new List<Item>();
        if (handle == IntPtr.Zero) return hasil;
        try
        {
            using (var automation = new UIA3Automation())
            {
                var root = automation.FromHandle(handle);
                if (root == null) return hasil;
                var all = root.FindAllDescendants();
                foreach (var el in all)
                {
                    if (hasil.Count >= maxItems) break;
                    try
                    {
                        if (!Interesting.Contains(el.ControlType)) continue;
                        if (el.IsOffscreen) continue;
                        var r = el.BoundingRectangle;
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        hasil.Add(new Item
                        {
                            Name = el.Name ?? "",
                            Type = el.ControlType.ToString(),
                            Rect = r,
                            Enabled = el.IsEnabled,
                        });
                    }
                    catch { /* elemen hilang di tengah pindai dsb. — lewati */ }
                }
            }
        }
        catch { /* UIA gagal total — daftar kosong */ }
        return hasil;
    }

    /// <summary>
    /// Cari elemen hasil pindai terakhir: indeks persis bila index
    /// valid, selain itu elemen pertama yang namanya mengandung
    /// nameQuery (tidak peka huruf). Null bila tidak ketemu.
    /// </summary>
    public static Item Find(int index, string nameQuery)
    {
        if (index >= 0 && index < _last.Count) return _last[index];
        if (!string.IsNullOrWhiteSpace(nameQuery))
        {
            foreach (var item in _last)
            {
                if (item.Name.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                    return item;
            }
        }
        return null;
    }
}
