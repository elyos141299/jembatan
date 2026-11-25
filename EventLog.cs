using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jembatan;

/// <summary>
/// Catatan kejadian PC (v2.5.0, fitur /events): Muse selama ini
/// hanya tahu keadaan saat ia memanggil — kejadian di antaranya
/// (proses mulai/berhenti, jendela buka/tutup, clipboard berubah,
/// PC menganggur, layar dikunci) hilang begitu saja. Kelas ini
/// memantau di latar dan menyimpan 200 kejadian terakhir dalam
/// cincin memori; endpoint GET /events membacanya (boleh sejak
/// nomor urut tertentu), jadi Muse bisa "balik dan melihat apa
/// yang terjadi" tanpa menahan panggilan tunggu.
///
/// Pemantau: satu thread latar, siklus 2 detik — bedakan daftar
/// proses sesi user &amp; daftar jendela dari cuplikan sebelumnya.
/// Proses sistem yang berisik (conhost dsb.) disaring. Kejadian
/// clipboard &amp; kunci layar datang dari MainForm (listener
/// jendela + SystemEvents), dicatat lewat Add() yang sama.
/// Isi clipboard TIDAK dicatat — hanya panjang teksnya; isinya
/// tetap urusan endpoint /clipboard atas permintaan.
/// </summary>
public static class EventLog
{
    /// <summary>Satu kejadian tercatat.</summary>
    public sealed class Entry
    {
        public long Seq;
        public string Time = "";
        public string Kind = "";
        public string Detail = "";
        // Identitas jendela terkait (v3.9.0 — kejadian hook WinEvent
        // membawa hwnd & pid; kejadian polling lama mengisi 0).
        public long Hwnd;
        public int Pid;
    }

    private const int Kapasitas = 200;
    private static readonly List<Entry> _items = new List<Entry>();
    private static readonly object _lock = new object();
    private static long _seq;

    /// <summary>Nomor urut kejadian terakhir.</summary>
    public static long LastSeq
    {
        get { lock (_lock) { return _seq; } }
    }

    /// <summary>Catat satu kejadian (aman dari thread mana pun).</summary>
    public static void Add(string kind, string detail)
    {
        Add(kind, detail, 0, 0);
    }

    /// <summary>Catat satu kejadian dengan identitas jendela
    /// (v3.9.0). Setiap Add membangunkan penunggu long-poll.</summary>
    public static void Add(string kind, string detail, long hwnd, int pid)
    {
        lock (_lock)
        {
            _seq++;
            _items.Add(new Entry
            {
                Seq = _seq,
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Kind = kind,
                Detail = detail ?? "",
                Hwnd = hwnd,
                Pid = pid,
            });
            if (_items.Count > Kapasitas)
                _items.RemoveRange(0, _items.Count - Kapasitas);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Nomor urut kejadian tertua yang masih tersimpan
    /// (0 bila kosong) — dasar bendera truncated di /events
    /// (v3.9.0, DSA-06).</summary>
    public static long FirstSeq
    {
        get { lock (_lock) { return _items.Count > 0 ? _items[0].Seq : 0; } }
    }

    /// <summary>Tunggu sampai ada kejadian sesudah sinceSeq atau
    /// waktu habis (long-poll /events?wait=, v3.9.0 — DSA-07).
    /// True bila ada kejadian baru.</summary>
    public static bool WaitForNew(long sinceSeq, int timeoutMs)
    {
        lock (_lock)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (_seq <= sinceSeq)
            {
                long sisa = deadline - Environment.TickCount64;
                if (sisa <= 0) return false;
                Monitor.Wait(_lock, (int)Math.Min(sisa, int.MaxValue));
            }
            return true;
        }
    }

    /// <summary>Kejadian sesudah sinceSeq (0 = semua yang tersimpan), terbaru terakhir.</summary>
    public static List<Entry> Since(long sinceSeq, int max)
    {
        lock (_lock)
        {
            var hasil = new List<Entry>();
            foreach (var e in _items)
            {
                if (e.Seq <= sinceSeq) continue;
                hasil.Add(e);
                if (hasil.Count >= max) break;
            }
            return hasil;
        }
    }

    // ---------- Pemantau latar ----------

    private static Thread _monitor;
    private static volatile bool _monitorOn;

    // Proses bawaan Windows yang lahir-mati terus — bukan sinyal.
    private static readonly HashSet<string> Noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "conhost", "dllhost", "RuntimeBroker", "SearchProtocolHost",
        "SearchFilterHost", "smartscreen", "MpCmdRun", "TiWorker",
        "TrustedInstaller", "WmiPrvSE", "backgroundTaskHost", "svchost",
        "csrss", "winlogon", "fontdrvhost", "dwm", "sihost", "taskhostw",
        // Anak proses panel chat (WebView2) lahir-mati terus sepanjang
        // aplikasi jalan — sumber noise terbesar /events (v2.5.5).
        "msedgewebview2",
    };

    /// <summary>Mulai pemantau (sekali saja; dipanggil AgentServer).</summary>
    public static void StartMonitor()
    {
        if (_monitorOn) return;
        _monitorOn = true;
        _monitor = new Thread(MonitorLoop) { IsBackground = true, Name = "EventMonitor" };
        _monitor.Start();
    }

    private static Dictionary<int, string> SnapshotProcs()
    {
        var map = new Dictionary<int, string>();
        int sesi = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != sesi) continue;
                if (Noise.Contains(p.ProcessName)) continue;
                map[p.Id] = p.ProcessName;
            }
            catch { /* proses mati di tengah jalan — lewati */ }
            finally { p.Dispose(); }
        }
        return map;
    }

    private static Dictionary<IntPtr, string> SnapshotWins()
    {
        var map = new Dictionary<IntPtr, string>();
        foreach (var w in NativeInput.ListWindows())
            map[w.Handle] = w.Title;
        return map;
    }

    private static void MonitorLoop()
    {
        var procs = SnapshotProcs();
        var wins = SnapshotWins();
        bool idle = false;
        while (_monitorOn)
        {
            try
            {
                Thread.Sleep(2000);

                var nowP = SnapshotProcs();
                foreach (var kv in nowP)
                    if (!procs.ContainsKey(kv.Key)) Add("process-start", kv.Value);
                foreach (var kv in procs)
                    if (!nowP.ContainsKey(kv.Key)) Add("process-stop", kv.Value);
                procs = nowP;

                var nowW = SnapshotWins();
                foreach (var kv in nowW)
                    if (!wins.ContainsKey(kv.Key)) Add("window-open", kv.Value);
                foreach (var kv in wins)
                    if (!nowW.ContainsKey(kv.Key)) Add("window-close", kv.Value);
                wins = nowW;

                // Segarkan peta judul bersama untuk hook
                // NAMECHANGE (v3.9.0): hanya jendela yang ada di
                // peta ini yang perubahan judulnya dicatat hook.
                lock (_winLock)
                {
                    _winTitles.Clear();
                    foreach (var kv in nowW) _winTitles[kv.Key] = kv.Value;
                }

                // Rekonsiliasi fokus (v3.9.0): perubahan foreground
                // yang digerakkan input injeksi MuseLink sendiri
                // terbukti tidak selalu sampai ke hook-nya sendiri —
                // pemantau menambal: bila jendela depan berbeda dari
                // fokus terakhir yang tercatat hook, catat di sini
                // (latensi ≤2 dtk; perubahan dari user/aplikasi lain
                // tetap real-time lewat hook).
                try
                {
                    IntPtr fgNow = NativeInput.ForegroundHandle();
                    if (fgNow != IntPtr.Zero && fgNow != _lastFocusHwnd)
                    {
                        _lastFocusHwnd = fgNow;
                        _lastFocusTick = Environment.TickCount64;
                        Add("focus", NativeInput.TitleOf(fgNow), fgNow.ToInt64(), NativeInput.PidOf(fgNow));
                    }
                }
                catch { /* rekonsiliasi hanya pelengkap */ }

                // Menganggur: ambang 5 menit tanpa masukan.
                bool nowIdle = NativeInput.IdleSeconds() >= 300;
                if (nowIdle && !idle) Add("idle-start", "tidak ada masukan selama 5 menit");
                if (!nowIdle && idle) Add("idle-end", "masukan kembali terdeteksi");
                idle = nowIdle;
            }
            catch { /* pemantau tidak boleh mati karena satu galat */ }
        }
    }

    // ---------- WinEventHook (v3.9.0 — EventHub, DSA-02) ----------
    // Event OS sungguhan untuk dua sinyal yang tidak bisa diberikan
    // polling 2 detik: perubahan jendela depan (focus) dan perubahan
    // judul jendela (window-title). Hook dipasang di thread UI
    // (ber-message-loop) lewat StartHooks; callback dibuat sangat
    // ringan — baca judul/pid lalu Add, tidak ada kerja berat.

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectNameChange = 0x800C;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjidWindow = 0;

    private static WinEventDelegate _hookProc; // disimpan agar tidak di-GC
    private static IntPtr _hookFocus;
    private static IntPtr _hookName;
    private static readonly Dictionary<IntPtr, string> _winTitles = new Dictionary<IntPtr, string>();
    private static readonly object _winLock = new object();
    private static IntPtr _lastFocusHwnd;
    private static long _lastFocusTick;

    /// <summary>Pasang hook di thread UI (dipanggil AgentServer.Start).</summary>
    public static void StartHooks(IUiHost ui)
    {
        if (ui == null) return;
        try
        {
            ui.BeginInvoke((Action)(() =>
            {
                if (_hookProc == null) _hookProc = OnWinEvent;
                if (_hookFocus == IntPtr.Zero)
                    _hookFocus = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _hookProc, 0, 0, WineventOutOfContext);
                if (_hookName == IntPtr.Zero)
                    _hookName = SetWinEventHook(EventObjectNameChange, EventObjectNameChange, IntPtr.Zero, _hookProc, 0, 0, WineventOutOfContext);
            }));
        }
        catch { /* hook pelengkap — pemantau polling tetap jalan */ }
    }

    /// <summary>Lepas hook (dipanggil AgentServer.Stop).</summary>
    public static void StopHooks()
    {
        try { if (_hookFocus != IntPtr.Zero) { UnhookWinEvent(_hookFocus); _hookFocus = IntPtr.Zero; } } catch { }
        try { if (_hookName != IntPtr.Zero) { UnhookWinEvent(_hookName); _hookName = IntPtr.Zero; } } catch { }
    }

    private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            if (eventType == EventSystemForeground)
            {
                long now = Environment.TickCount64;
                if (hwnd == _lastFocusHwnd && now - _lastFocusTick < 400) return;
                _lastFocusHwnd = hwnd;
                _lastFocusTick = now;
                Add("focus", NativeInput.TitleOf(hwnd), hwnd.ToInt64(), NativeInput.PidOf(hwnd));
            }
            else if (eventType == EventObjectNameChange && idObject == ObjidWindow && idChild == 0)
            {
                string baru = NativeInput.TitleOf(hwnd);
                string lama = null;
                lock (_winLock)
                {
                    if (!_winTitles.TryGetValue(hwnd, out var lamaDalam)) return; // hanya jendela terlacak
                    if (lamaDalam == baru) return;
                    _winTitles[hwnd] = baru;
                    lama = lamaDalam;
                }
                Add("window-title", lama + " \u2192 " + baru, hwnd.ToInt64(), NativeInput.PidOf(hwnd));
            }
        }
        catch { /* callback hook tidak boleh meledak */ }
    }
}
