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
        // Profil aksesibilitas penuh (v3.5.0 — Accessibility
        // Intelligence): nilai, keadaan, pola yang didukung, dan
        // identitas stabil elemen.
        public string Value = "";
        public string State = "";
        public string Patterns = "";
        public string AutomationId = "";
        public string ClassName = "";

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
                        var item = new Item
                        {
                            Name = el.Name ?? "",
                            Type = el.ControlType.ToString(),
                            Rect = r,
                            Enabled = el.IsEnabled,
                        };
                        FillDetails(el, item);
                        _last.Add(item);
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
                        var item = new Item
                        {
                            Name = el.Name ?? "",
                            Type = el.ControlType.ToString(),
                            Rect = r,
                            Enabled = el.IsEnabled,
                        };
                        FillDetails(el, item);
                        hasil.Add(item);
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

    /// <summary>
    /// Profil aksesibilitas penuh satu elemen (v3.5.0 —
    /// Accessibility Intelligence): value, state, pola yang
    /// didukung, AutomationId, ClassName. Semua dibaca bertahan:
    /// properti yang tidak didukung elemen dilewati diam-diam.
    /// </summary>
    private static void FillDetails(AutomationElement el, Item item)
    {
        try { item.AutomationId = el.AutomationId ?? ""; } catch { }
        try { item.ClassName = el.ClassName ?? ""; } catch { }
        var pats = new List<string>();
        try { if (el.Patterns.Invoke.IsSupported) pats.Add("Invoke"); } catch { }
        try { if (el.Patterns.Value.IsSupported) pats.Add("Value"); } catch { }
        try { if (el.Patterns.Toggle.IsSupported) pats.Add("Toggle"); } catch { }
        try { if (el.Patterns.ExpandCollapse.IsSupported) pats.Add("ExpandCollapse"); } catch { }
        try { if (el.Patterns.SelectionItem.IsSupported) pats.Add("SelectionItem"); } catch { }
        try { if (el.Patterns.Text.IsSupported) pats.Add("Text"); } catch { }
        try { if (el.Patterns.RangeValue.IsSupported) pats.Add("RangeValue"); } catch { }
        try { if (el.Patterns.Scroll.IsSupported) pats.Add("Scroll"); } catch { }
        item.Patterns = string.Join(",", pats);
        try
        {
            var vp = el.Patterns.Value.PatternOrDefault;
            if (vp != null) item.Value = vp.Value ?? "";
        }
        catch { }
        var st = new List<string>();
        try
        {
            var tp = el.Patterns.Toggle.PatternOrDefault;
            if (tp != null) st.Add("toggle:" + tp.ToggleState);
        }
        catch { }
        try
        {
            var ep = el.Patterns.ExpandCollapse.PatternOrDefault;
            if (ep != null) st.Add("expand:" + ep.ExpandCollapseState);
        }
        catch { }
        try { if (el.Properties.HasKeyboardFocus.ValueOrDefault) st.Add("focused"); } catch { }
        try { if (el.Properties.IsKeyboardFocusable.ValueOrDefault) st.Add("focusable"); } catch { }
        item.State = string.Join(";", st);
    }

    /// <summary>
    /// Baca nilai elemen di satu titik layar TANPA menyentuhnya
    /// (v3.5.0 — /readvalue): Value, lalu Text, lalu Name.
    /// </summary>
    public static bool ReadAtPoint(int x, int y, out string value, out string name, out string type)
    {
        value = ""; name = ""; type = "";
        try
        {
            using (var automation = new UIA3Automation())
            {
                var el = automation.FromPoint(new Point(x, y));
                if (el == null) return false;
                name = el.Name ?? "";
                type = el.ControlType.ToString();
                try
                {
                    var vp = el.Patterns.Value.PatternOrDefault;
                    if (vp != null && !string.IsNullOrEmpty(vp.Value)) { value = vp.Value; return true; }
                }
                catch { }
                try
                {
                    var tp = el.Patterns.Text.PatternOrDefault;
                    if (tp != null)
                    {
                        var t = tp.DocumentRange.GetText(512);
                        if (!string.IsNullOrEmpty(t)) { value = t.Trim(); return true; }
                    }
                }
                catch { }
                value = name;
                return true;
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// Tulis nilai elemen di satu titik layar lewat ValuePattern —
    /// TANPA klik, TANPA fokus, TANPA mouse (v3.5.0 — jalur utama
    /// /setvalue, Background Interaction). False bila elemen tidak
    /// mendukung atau baca-saja.
    /// </summary>
    public static bool TrySetValueAtPoint(int x, int y, string text, out string readback, out string elemName)
    {
        readback = ""; elemName = "";
        try
        {
            using (var automation = new UIA3Automation())
            {
                var el = automation.FromPoint(new Point(x, y));
                if (el == null) return false;
                elemName = el.Name ?? "";
                var vp = el.Patterns.Value.PatternOrDefault;
                if (vp == null || vp.IsReadOnly) return false;
                vp.SetValue(text);
                try { readback = vp.Value ?? ""; } catch { readback = ""; }
                return true;
            }
        }
        catch { return false; }
    }

    /// <summary>Fokuskan elemen di titik layar lewat UIA (v3.5.0).</summary>
    public static bool FocusAtPoint(int x, int y)
    {
        try
        {
            using (var automation = new UIA3Automation())
            {
                var el = automation.FromPoint(new Point(x, y));
                if (el == null) return false;
                el.Focus();
                return true;
            }
        }
        catch { return false; }
    }

    /// <summary>Hasil operasi latar via pola UIA (v3.5.0).</summary>
    public sealed class ActOutcome
    {
        public bool Ok;
        public string ElementName = "";
        public string ElementType = "";
        public string Value = "";
        public string Error = "";
    }

    /// <summary>
    /// Operasikan elemen lewat POLA UIA (v3.5.0 — Background
    /// Interaction): invoke/toggle/expand/collapse/select/setvalue/
    /// focus tanpa menggerakkan mouse & keyboard fisik sama sekali.
    /// Sasaran: titik (px,py) bila diberi; selain itu dari pindaian
    /// elemen LANGSUNG jendela depan (filter sama dengan
    /// ScanForeground): AutomationId, lalu nama (mengandung), lalu
    /// indeks urutan pindai.
    /// </summary>
    public static ActOutcome Act(int? px, int? py, string automationId, string nameQuery, int index, string action, string text)
    {
        var oc = new ActOutcome();
        try
        {
            using (var automation = new UIA3Automation())
            {
                AutomationElement el = null;
                if (px.HasValue && py.HasValue)
                {
                    el = automation.FromPoint(new Point(px.Value, py.Value));
                }
                else
                {
                    var fg = NativeInput.ForegroundHandle();
                    if (fg == IntPtr.Zero) { oc.Error = "tidak ada jendela depan"; return oc; }
                    var root = automation.FromHandle(fg);
                    if (root == null) { oc.Error = "jendela depan tidak terbaca UIA"; return oc; }
                    var all = root.FindAllDescendants();
                    var cands = new List<AutomationElement>();
                    foreach (var e in all)
                    {
                        try
                        {
                            if (!Interesting.Contains(e.ControlType)) continue;
                            if (e.IsOffscreen) continue;
                            var r = e.BoundingRectangle;
                            if (r.Width <= 0 || r.Height <= 0) continue;
                            cands.Add(e);
                        }
                        catch { /* elemen hilang di tengah pindai — lewati */ }
                    }
                    if (!string.IsNullOrWhiteSpace(automationId))
                    {
                        foreach (var e in cands)
                        {
                            try { if (e.AutomationId == automationId) { el = e; break; } } catch { }
                        }
                    }
                    if (el == null && !string.IsNullOrWhiteSpace(nameQuery))
                    {
                        foreach (var e in cands)
                        {
                            try { if ((e.Name ?? "").IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) >= 0) { el = e; break; } } catch { }
                        }
                    }
                    if (el == null && index >= 0 && index < cands.Count) el = cands[index];
                }
                if (el == null) { oc.Error = "elemen tidak ditemukan"; return oc; }
                oc.ElementName = el.Name ?? "";
                oc.ElementType = el.ControlType.ToString();
                switch ((action ?? "invoke").Trim().ToLowerInvariant())
                {
                    case "invoke":
                    {
                        var p = el.Patterns.Invoke.PatternOrDefault;
                        if (p == null) { oc.Error = "pola Invoke tidak didukung"; return oc; }
                        p.Invoke();
                        break;
                    }
                    case "toggle":
                    {
                        var p = el.Patterns.Toggle.PatternOrDefault;
                        if (p == null) { oc.Error = "pola Toggle tidak didukung"; return oc; }
                        p.Toggle();
                        break;
                    }
                    case "expand":
                    {
                        var p = el.Patterns.ExpandCollapse.PatternOrDefault;
                        if (p == null) { oc.Error = "pola ExpandCollapse tidak didukung"; return oc; }
                        p.Expand();
                        break;
                    }
                    case "collapse":
                    {
                        var p = el.Patterns.ExpandCollapse.PatternOrDefault;
                        if (p == null) { oc.Error = "pola ExpandCollapse tidak didukung"; return oc; }
                        p.Collapse();
                        break;
                    }
                    case "select":
                    {
                        var p = el.Patterns.SelectionItem.PatternOrDefault;
                        if (p == null) { oc.Error = "pola SelectionItem tidak didukung"; return oc; }
                        p.Select();
                        break;
                    }
                    case "setvalue":
                    {
                        var p = el.Patterns.Value.PatternOrDefault;
                        if (p == null || p.IsReadOnly) { oc.Error = "pola Value tidak didukung/baca-saja"; return oc; }
                        p.SetValue(text ?? "");
                        try { oc.Value = p.Value ?? ""; } catch { }
                        break;
                    }
                    case "focus":
                    {
                        el.Focus();
                        break;
                    }
                    default:
                        oc.Error = "action tidak dikenal: " + action;
                        return oc;
                }
                oc.Ok = true;
                return oc;
            }
        }
        catch (Exception ex)
        {
            oc.Error = ex.GetType().Name;
            return oc;
        }
    }
}
