using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
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
        // Nilai IsOffscreen UIA apa adanya (v5.17.0): hasil
        // pindai memang sudah melewati saringan offscreen, jadi
        // di hasil /uia/find bidang ini terbaca false — ia
        // diekspos agar pemanggil melihat nilai mentah UIA,
        // bukan menebak keterlihatan dari koordinat.
        public bool Offscreen;
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

    // Kunci daftar hasil pindai (v3.11.0 — DSA-17): _last ditulis
    // thread UI saat pindai dan bisa dibaca thread HTTP (Find dari
    // handler, Last dari pemanggil mana pun) — semua akses kini
    // lewat satu kunci; Last mengembalikan SALINAN, bukan daftar
    // hidup yang bisa berubah di tengah enumerasi pembaca.
    private static readonly object _lastLock = new object();

    /// <summary>Hasil pindai terakhir (kosong sebelum pindai pertama).</summary>
    public static IReadOnlyList<Item> Last
    {
        get { lock (_lastLock) { return new List<Item>(_last); } }
    }

    // Metadata pindai terakhir (v3.6.0): /uia/click menolak
    // mengklik hasil pindai yang jendela depannya sudah berganti
    // atau yang umurnya lewat batas (BUG-02 audit).
    public static long LastScanTick;
    public static IntPtr LastScanHwnd;

    // Satu UIA3Automation per thread, dipakai ulang (BUG-22
    // audit): konstruksi COM per panggilan membuang latensi tetap
    // di tiap operasi UIA. Semua pemakaian UiaScan berjalan di
    // thread UI lewat OnUi, jadi praktisnya satu instans.
    private static readonly ThreadLocal<UIA3Automation> _auto =
        new ThreadLocal<UIA3Automation>(() => new UIA3Automation());
    private static UIA3Automation SharedAutomation => _auto.Value;

    /// <summary>Salinan hasil pindai + cap waktu (v3.6.0): daftar
    /// hidup _last tidak lagi dibaca thread HTTP saat pindaian
    /// berikutnya membersihkannya (BUG-19 audit).</summary>
    private static List<Item> Snapshot()
    {
        LastScanTick = Environment.TickCount64;
        lock (_lastLock) { return new List<Item>(_last); }
    }

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
    /// menghasilkan daftar ribuan baris. Mengembalikan SALINAN
    /// daftar yang tersimpan di Last (v3.6.0 — aman dibaca thread
    /// lain) beserta cap waktu & handle jendela pindaian.
    /// </summary>
    public static List<Item> ScanForeground(int maxItems)
    {
      lock (_lastLock)
      {
        _last.Clear();
        var fg = NativeInput.ForegroundHandle();
        LastScanHwnd = fg;
        if (fg == IntPtr.Zero) return Snapshot();

        try
        {
            var automation = SharedAutomation;
            {
                var root = automation.FromHandle(fg);
                if (root == null) return Snapshot();
                var all = root.FindAllDescendants();
                foreach (var el in all)
                {
                    if (_last.Count >= maxItems) break;
                    try
                    {
                        if (!Interesting.Contains(el.ControlType)) continue;
                        bool off = el.IsOffscreen;
                        if (off) continue;
                        var r = el.BoundingRectangle;
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        var item = new Item
                        {
                            Name = el.Name ?? "",
                            Type = el.ControlType.ToString(),
                            Rect = r,
                            Enabled = el.IsEnabled,
                            Offscreen = off,
                        };
                        FillDetails(el, item);
                        _last.Add(item);
                    }
                    catch { /* elemen hilang di tengah pindai dsb. — lewati */ }
                }
            }
        }
        catch { /* UIA gagal total (jendela aman dsb.) — daftar kosong */ }
        return Snapshot();
      }
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
            var automation = SharedAutomation;
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
                        bool off = el.IsOffscreen;
                        if (off) continue;
                        var r = el.BoundingRectangle;
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        var item = new Item
                        {
                            Name = el.Name ?? "",
                            Type = el.ControlType.ToString(),
                            Rect = r,
                            Enabled = el.IsEnabled,
                            Offscreen = off,
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
        lock (_lastLock)
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
    public static bool ReadAtPoint(int x, int y, out string value, out string name, out string type, out bool hasValue)
    {
        value = ""; name = ""; type = ""; hasValue = false;
        try
        {
            var automation = SharedAutomation;
            {
                var el = automation.FromPoint(new Point(x, y));
                if (el == null) return false;
                name = el.Name ?? "";
                type = el.ControlType.ToString();
                try
                {
                    var vp = el.Patterns.Value.PatternOrDefault;
                    if (vp != null && !string.IsNullOrEmpty(vp.Value)) { value = vp.Value; hasValue = true; return true; }
                }
                catch { }
                try
                {
                    var tp = el.Patterns.Text.PatternOrDefault;
                    if (tp != null)
                    {
                        var t = tp.DocumentRange.GetText(512);
                        if (!string.IsNullOrEmpty(t)) { value = t.Trim(); hasValue = true; return true; }
                    }
                }
                catch { }
                // v3.6.0 (BUG-01 audit): elemen memang tidak punya
                // nilai — value tetap "" dan hasValue false. Dulu
                // nama elemen dikembalikan sebagai "nilai", membuat
                // /readvalue berbohong pada kolom kosong.
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
            var automation = SharedAutomation;
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
            var automation = SharedAutomation;
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
        // Titik tengah elemen (v5.14.0 — untuk verifikasi
        // tangga /act) + judul jendela pemilik (lintas jendela).
        public int CenterX;
        public int CenterY;
        public string WindowTitle = "";
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
            var automation = SharedAutomation;
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
                            bool off = e.IsOffscreen;
                            if (off) continue;
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
                try
                {
                    var rr = el.BoundingRectangle;
                    oc.CenterX = (int)(rr.Left + rr.Width / 2);
                    oc.CenterY = (int)(rr.Top + rr.Height / 2);
                }
                catch { /* elemen tanpa kotak batas — titik 0 */ }
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

    // ---------- Pencarian LINTAS JENDELA (v5.14.0 — /uia3) ----------
    // /uia & Act biasa hanya memindai JENDELA DEPAN. Jalur ini
    // memindai SEMUA jendela tingkat atas lewat desktop UIA —
    // modus gagalnya berbeda (elemen di jendela latar tetap
    // ketemu), makanya ia anak tangga sendiri di tangga /act.

    /// <summary>Satu temuan lintas jendela.</summary>
    public sealed class CrossHit
    {
        public string Name = "";
        public string Type = "";
        public string AutomationId = "";
        public string WindowTitle = "";
        public string Process = "";
        public Rectangle Rect;
        // Nilai IsOffscreen UIA apa adanya (v5.17.0) — lihat
        // catatan di Item.Offscreen.
        public bool Offscreen;
        public int CenterX => Rect.Left + Rect.Width / 2;
        public int CenterY => Rect.Top + Rect.Height / 2;
    }

    private static bool WindowMatches(AutomationElement win, string windowTitle, string processName, out string title, out string proc)
    {
        title = "";
        proc = "";
        try { title = win.Name ?? ""; } catch { }
        try
        {
            int pid = win.Properties.ProcessId.Value;
            proc = System.Diagnostics.Process.GetProcessById(pid).ProcessName ?? "";
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(windowTitle) &&
            title.IndexOf(windowTitle, StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (!string.IsNullOrWhiteSpace(processName) &&
            proc.IndexOf(processName, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    /// <summary>Cari elemen di semua jendela (coba ulang sampai
    /// timeoutMs — elemen bisa baru muncul). Maks 20 temuan.</summary>
    public static List<CrossHit> FindAcrossWindows(string nameQuery, string automationId,
        string windowTitle, string processName, int timeoutMs)
    {
        var hasil = new List<CrossHit>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            hasil.Clear();
            try
            {
                var desktop = SharedAutomation.GetDesktop();
                foreach (var win in desktop.FindAllChildren())
                {
                    if (!WindowMatches(win, windowTitle, processName, out string wTitle, out string wProc)) continue;
                    AutomationElement[] descs;
                    try { descs = win.FindAllDescendants(); } catch { continue; }
                    foreach (var e in descs)
                    {
                        try
                        {
                            if (!Interesting.Contains(e.ControlType)) continue;
                            bool off = e.IsOffscreen;
                            if (off) continue;
                            var r = e.BoundingRectangle;
                            if (r.Width <= 0 || r.Height <= 0) continue;
                            string nm = e.Name ?? "";
                            string aid = "";
                            try { aid = e.AutomationId ?? ""; } catch { }
                            // Kedua saringan berlaku BERSAMA
                            // bila keduanya diisi (v5.14.1):
                            // aid saja bisa menabrak elemen lain
                            // ber-aid sama (address bar Explorer
                            // juga "TextBox"); nama saja bisa
                            // menabrak item daftar ("Searches").
                            bool cocok = true;
                            if (!string.IsNullOrWhiteSpace(automationId)) cocok = aid == automationId;
                            if (cocok && !string.IsNullOrWhiteSpace(nameQuery)) cocok = nm.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!cocok) continue;
                            hasil.Add(new CrossHit
                            {
                                Name = nm,
                                Type = e.ControlType.ToString(),
                                AutomationId = aid,
                                WindowTitle = wTitle,
                                Process = wProc,
                                Rect = new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height),
                                Offscreen = off,
                            });
                            if (hasil.Count >= 20) return hasil;
                        }
                        catch { /* elemen hilang di tengah pindai */ }
                    }
                }
            }
            catch { /* desktop belum siap — coba lagi sampai batas */ }
            if (hasil.Count > 0 || sw.ElapsedMilliseconds >= timeoutMs) return hasil;
            Thread.Sleep(200);
        }
    }

    /// <summary>Temukan elemen lintas jendela lalu operasikan
    /// lewat Act berbasis titik (memakai ulang seluruh logika
    /// pola Act yang sudah terbukti).</summary>
    public static ActOutcome ActAcrossWindows(string nameQuery, string automationId,
        string windowTitle, string processName, string action, string text, int timeoutMs)
    {
        var hits = FindAcrossWindows(nameQuery, automationId, windowTitle, processName, timeoutMs);
        if (hits.Count == 0)
            return new ActOutcome { Error = "elemen tidak ditemukan di jendela mana pun" };
        var h = hits[0];
        var oc = Act(h.CenterX, h.CenterY, "", "", -1, action, text);
        oc.WindowTitle = h.WindowTitle;
        if (oc.CenterX == 0 && oc.CenterY == 0) { oc.CenterX = h.CenterX; oc.CenterY = h.CenterY; }
        return oc;
    }
}
