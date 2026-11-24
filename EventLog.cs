using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        lock (_lock)
        {
            _seq++;
            _items.Add(new Entry
            {
                Seq = _seq,
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Kind = kind,
                Detail = detail ?? "",
            });
            if (_items.Count > Kapasitas)
                _items.RemoveRange(0, _items.Count - Kapasitas);
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

                // Menganggur: ambang 5 menit tanpa masukan.
                bool nowIdle = NativeInput.IdleSeconds() >= 300;
                if (nowIdle && !idle) Add("idle-start", "tidak ada masukan selama 5 menit");
                if (!nowIdle && idle) Add("idle-end", "masukan kembali terdeteksi");
                idle = nowIdle;
            }
            catch { /* pemantau tidak boleh mati karena satu galat */ }
        }
    }
}
