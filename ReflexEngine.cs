using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Jembatan;

/// <summary>
/// Mesin Refleks (v5.13.0): aturan "bila X terjadi, lakukan Y"
/// yang berjalan sendiri di PC tanpa menunggu perintah agent.
///
/// Pemicu: window (jendela baru yang judulnya cocok), process
/// (proses mulai/berhenti), file (berkas baru/berubah di folder
/// terpantau). Aksi — aman secara bawaan: log (catat), event
/// (masuk aliran /events), tasknote (catatan Task Memory),
/// routine:&lt;nama&gt; (jalankan rutinitas tersimpan),
/// dismiss:&lt;teks&gt; (klik tombol berteks persis itu pada
/// jendela baru, via UIA — untuk dialog pengganggu yang dikenal).
///
/// Aturan tersimpan di reflex-rules.json (folder data aplikasi),
/// tiap aturan punya cooldown agar tidak membanjir. Semua
/// pemicuan tercatat di log pemicuan (100 terakhir, GET
/// /reflex/log). Eksekusi aksi berat (routine/dismiss)
/// diserahkan ke callback AgentServer agar mesin ini tetap
/// ramping dan tidak berputar balik ke server.
/// </summary>
internal static class ReflexEngine
{
    public sealed class Rule
    {
        public string Id { get; set; } = "";
        public string Trigger { get; set; } = "";   // window | process | file
        public string Match { get; set; } = "";     // window: subteks judul; process: nama proses; file: folder
        public string Pattern { get; set; } = "";   // window: subteks proses (opsional); file: subteks nama berkas
        public string On { get; set; } = "";        // process: start|exit|any; file: created|changed|any
        public string Action { get; set; } = "log"; // log|event|tasknote|routine:<nama>|dismiss:<teks>
        public int CooldownSec { get; set; } = 30;
    }

    public sealed class Callbacks
    {
        public Action<string> Log;
        public Action<string> EventAdd;                 // (detail) -> EventLog
        public Action<string> TaskNote;                 // (teks) -> TaskContext.Note
        public Func<string, bool> RunRoutine;           // (nama) -> jalan?
        public Func<IntPtr, string, bool> Dismiss;      // (hwnd, teks tombol) -> terklik?
    }

    private sealed class FireEntry
    {
        public string Time, RuleId, Trigger, Detail, Action, Result;
    }

    private static readonly object _lock = new();
    private static List<Rule> _rules;
    private static readonly List<FireEntry> _fires = new();
    private static readonly Dictionary<string, long> _lastFireTick = new();
    private static Callbacks _cb;
    private static Thread _poll;
    private static volatile bool _on;
    private static HashSet<long> _seenHwnd = new();
    private static HashSet<string> _seenProcs = new();
    private static readonly List<FileSystemWatcher> _watchers = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jembatan", "reflex-rules.json");

    // ---------- penyimpanan ----------

    private static void EnsureLoaded()
    {
        if (_rules != null) return;
        _rules = new List<Rule>();
        try
        {
            if (File.Exists(FilePath))
                _rules = JsonSerializer.Deserialize<List<Rule>>(File.ReadAllText(FilePath)) ?? new List<Rule>();
        }
        catch { _rules = new List<Rule>(); }
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_rules,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* gagal simpan tidak menjatuhkan server */ }
    }

    // ---------- CRUD (dipakai endpoint) ----------

    public static List<Rule> Rules()
    {
        lock (_lock) { EnsureLoaded(); return new List<Rule>(_rules); }
    }

    public static Rule Add(Rule r)
    {
        lock (_lock)
        {
            EnsureLoaded();
            r.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            if (r.CooldownSec < 0) r.CooldownSec = 0;
            if (r.CooldownSec > 3600) r.CooldownSec = 3600;
            if (string.IsNullOrWhiteSpace(r.Action)) r.Action = "log";
            _rules.Add(r);
            Persist();
            RebuildWatchers();
            return r;
        }
    }

    public static bool Delete(string id)
    {
        lock (_lock)
        {
            EnsureLoaded();
            int n = _rules.RemoveAll(x => x.Id == id);
            if (n == 0) return false;
            Persist();
            RebuildWatchers();
            return true;
        }
    }

    public static List<Dictionary<string, object>> Fires()
    {
        lock (_lock)
        {
            var hasil = new List<Dictionary<string, object>>();
            for (int i = _fires.Count - 1; i >= 0; i--)
            {
                var f = _fires[i];
                hasil.Add(new Dictionary<string, object>
                {
                    ["time"] = f.Time, ["ruleId"] = f.RuleId, ["trigger"] = f.Trigger,
                    ["detail"] = f.Detail, ["action"] = f.Action, ["result"] = f.Result,
                });
            }
            return hasil;
        }
    }

    public static (int rules, int fires) Counts()
    {
        lock (_lock) { EnsureLoaded(); return (_rules.Count, _fires.Count); }
    }

    // ---------- mesin ----------

    public static void Start(Callbacks cb)
    {
        if (_on) return;
        _cb = cb;
        lock (_lock) { EnsureLoaded(); RebuildWatchers(); }
        // Potret awal: jendela & proses yang sudah ada BUKAN
        // kejadian baru — jangan picu apa pun saat mulai.
        try { foreach (var w in NativeInput.ListWindows()) _seenHwnd.Add((long)w.Handle); } catch { }
        try { foreach (var p in Process.GetProcesses()) _seenProcs.Add(NormProc(p.ProcessName)); } catch { }
        _on = true;
        _poll = new Thread(PollLoop) { IsBackground = true, Name = "ReflexPoll" };
        _poll.Start();
    }

    private static string NormProc(string name)
    {
        name = (name ?? "").ToLowerInvariant();
        if (name.EndsWith(".exe")) name = name.Substring(0, name.Length - 4);
        return name;
    }

    private static void PollLoop()
    {
        int putaran = 0;
        while (_on)
        {
            try
            {
                PollWindows();
                if (putaran % 2 == 0) PollProcesses();
            }
            catch { /* satu putaran gagal tidak membunuh mesin */ }
            putaran++;
            Thread.Sleep(1500);
        }
    }

    private static void PollWindows()
    {
        var kini = new HashSet<long>();
        List<Rule> rules = Rules();
        bool adaAturan = false;
        foreach (var r in rules) if (r.Trigger == "window") { adaAturan = true; break; }
        foreach (var w in NativeInput.ListWindows())
        {
            long h = (long)w.Handle;
            kini.Add(h);
            if (!adaAturan) continue;
            if (_seenHwnd.Contains(h)) continue;
            if (string.IsNullOrWhiteSpace(w.Title)) continue;
            foreach (var r in rules)
            {
                if (r.Trigger != "window") continue;
                if (w.Title.IndexOf(r.Match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(r.Pattern) &&
                    (w.Process ?? "").IndexOf(r.Pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Fire(r, "jendela baru: '" + w.Title + "' (" + w.Process + ")", w.Handle);
            }
        }
        _seenHwnd = kini;
    }

    private static void PollProcesses()
    {
        var kini = new HashSet<string>();
        foreach (var p in Process.GetProcesses())
        {
            try { kini.Add(NormProc(p.ProcessName)); } catch { }
        }
        List<Rule> rules = Rules();
        foreach (var r in rules)
        {
            if (r.Trigger != "process") continue;
            string target = NormProc(r.Match);
            string on = string.IsNullOrEmpty(r.On) ? "any" : r.On.ToLowerInvariant();
            bool dulu = _seenProcs.Contains(target), sekarang = kini.Contains(target);
            if ((on == "start" || on == "any") && sekarang && !dulu)
                Fire(r, "proses mulai: " + target, IntPtr.Zero);
            else if ((on == "exit" || on == "any") && !sekarang && dulu)
                Fire(r, "proses berhenti: " + target, IntPtr.Zero);
        }
        _seenProcs = kini;
    }

    private static void RebuildWatchers()
    {
        // Dipanggil dalam _lock (dari Start/Add/Delete).
        foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        _watchers.Clear();
        if (_cb == null) return; // belum Start — dibangun ulang saat Start
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rules)
            if (r.Trigger == "file" && !string.IsNullOrWhiteSpace(r.Match) && Directory.Exists(r.Match))
                folders.Add(r.Match);
        foreach (var folder in folders)
        {
            try
            {
                var w = new FileSystemWatcher(folder) { EnableRaisingEvents = true };
                w.Created += (s, e) => OnFileEvent(folder, "created", e.FullPath);
                w.Changed += (s, e) => OnFileEvent(folder, "changed", e.FullPath);
                _watchers.Add(w);
            }
            catch { /* folder tak bisa dipantau — lewati */ }
        }
    }

    private static void OnFileEvent(string folder, string kind, string fullPath)
    {
        string nama = Path.GetFileName(fullPath);
        foreach (var r in Rules())
        {
            if (r.Trigger != "file") continue;
            if (!string.Equals(r.Match, folder, StringComparison.OrdinalIgnoreCase)) continue;
            string on = string.IsNullOrEmpty(r.On) ? "created" : r.On.ToLowerInvariant();
            if (on != "any" && on != kind) continue;
            if (!string.IsNullOrEmpty(r.Pattern) &&
                nama.IndexOf(r.Pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Fire(r, "berkas " + kind + ": " + fullPath, IntPtr.Zero);
        }
    }

    private static void Fire(Rule r, string detail, IntPtr hwnd)
    {
        string hasil = "dicatat";
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (_lastFireTick.TryGetValue(r.Id, out long terakhir) &&
                now - terakhir < (long)r.CooldownSec * 1000)
                return; // masih cooldown — jangan membanjir
            _lastFireTick[r.Id] = now;
        }
        try
        {
            string aksi = r.Action ?? "log";
            if (aksi == "log")
            {
                _cb?.Log?.Invoke("refleks [" + r.Id + "]: " + detail);
            }
            else if (aksi == "event")
            {
                _cb?.EventAdd?.Invoke(detail);
                hasil = "masuk /events";
            }
            else if (aksi == "tasknote")
            {
                _cb?.TaskNote?.Invoke("refleks: " + detail);
                hasil = "catatan tugas";
            }
            else if (aksi.StartsWith("routine:"))
            {
                string nama = aksi.Substring("routine:".Length).Trim();
                bool ok = _cb != null && _cb.RunRoutine != null && _cb.RunRoutine(nama);
                hasil = ok ? "rutinitas '" + nama + "' dijalankan" : "rutinitas '" + nama + "' GAGAL jalan";
            }
            else if (aksi.StartsWith("dismiss:"))
            {
                string teks = aksi.Substring("dismiss:".Length).Trim();
                if (hwnd == IntPtr.Zero) hasil = "dismiss dilewati (bukan pemicu jendela)";
                else
                {
                    bool ok = _cb != null && _cb.Dismiss != null && _cb.Dismiss(hwnd, teks);
                    hasil = ok ? "tombol '" + teks + "' diklik" : "tombol '" + teks + "' tidak ditemukan";
                }
            }
            _cb?.Log?.Invoke("refleks [" + r.Id + "] " + detail + " -> " + hasil);
        }
        catch (Exception ex) { hasil = "ralat: " + ex.Message; }
        lock (_lock)
        {
            _fires.Add(new FireEntry
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                RuleId = r.Id, Trigger = r.Trigger, Detail = detail,
                Action = r.Action, Result = hasil,
            });
            if (_fires.Count > 100) _fires.RemoveAt(0);
        }
    }
}
