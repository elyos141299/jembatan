using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Automation;

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
/// Hasil pindai terakhir disimpan statis; /uia/click memakai indeks
/// dari daftar itu. Semua fungsi di sini dipanggil dari thread UI
/// (AgentServer mengurus lewat OnUi) agar konsisten dengan injeksi
/// input lain dan menghindari masalah apartemen COM.
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

        AutomationElement root;
        try { root = AutomationElement.FromHandle(fg); }
        catch { return _last; }
        if (root == null) return _last;

        AutomationElementCollection all;
        try
        {
            var cond = new PropertyCondition(AutomationElement.IsControlElementProperty, true);
            all = root.FindAll(TreeScope.Descendants, cond);
        }
        catch { return _last; }

        foreach (AutomationElement el in all)
        {
            if (_last.Count >= maxItems) break;
            try
            {
                var cur = el.Current;
                if (!Interesting.Contains(cur.ControlType)) continue;
                if (cur.IsOffscreen) continue;
                var r = cur.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0) continue;
                _last.Add(new Item
                {
                    Name = cur.Name ?? "",
                    Type = cur.ControlType.ProgrammaticName.Replace("ControlType.", ""),
                    Rect = new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height),
                    Enabled = cur.IsEnabled,
                });
            }
            catch { /* elemen hilang di tengah pindai dsb. — lewati */ }
        }
        return _last;
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
