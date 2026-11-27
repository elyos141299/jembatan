using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jembatan;

/// <summary>
/// Layanan agen HTTP sederhana di port 8765, hidup hanya selama
/// jendela Jembatan terbuka dan status tersambung.
///
/// KENAPA TcpListener, BUKAN HttpListener?
/// HttpListener dengan prefix "http://+:8765/" butuh reservasi URL
/// (netsh http add urlacl) yang hanya bisa dibuat oleh administrator,
/// sedangkan Jembatan dipasang per-user TANPA admin. TcpListener tidak
/// butuh itu — cukup izin Windows Firewall satu kali (user klik Allow).
/// Protokol HTTP/JSON yang dilayani tetap sama.
///
/// KEAMANAN — TANPA password/token, dengan sadar:
/// Perlindungan layanan ini adalah jaringan Tailscale milik user sendiri.
/// Port 8765 hanya bisa dijangkau oleh perangkat yang SUDAH disetujui
/// user di admin Tailscale-nya (PC ini + VM Muse bernama "muse").
/// Jangan pernah mengekspos port ini ke internet publik (port forwarding).
/// Audit 25 Nov 2026 mengusulkan token auth + jail berkas; user
/// MENOLAKNYA dengan kesadaran penuh (±16:07) demi kebebasan operasi
/// agen di PC-nya — keputusan itu yang berlaku, bukan kelalaian.
/// </summary>
public sealed class AgentServer
{
    public const int Port = 8765;
    private const int DefaultExecTimeoutMs = 60000;
    private const int MaxExecTimeoutMs = 300000;
    private const int MaxOutputChars = 100000;

    // Versi aplikasi dari metadata rakitan (v3.6.0): dulu string
    // versi ditulis tangan di 4 titik dan satu terlewat ("3.4.1"
    // bertahan di /ping pada rilis 3.5.0 — BUG-14 audit). Kini
    // /ping, /sysinfo, /context, /diag membaca dari FileVersion.
    private static readonly string AppVersion = ComputeAppVersion();

    private static string ComputeAppVersion()
    {
        try
        {
            // v3.8.0: Assembly.Location KOSONG pada aplikasi
            // single-file sehingga bacaan lama selalu jatuh ke
            // string bawaan (itulah sebabnya label versi bisa
            // tertinggal satu rilis). Environment.ProcessPath
            // menunjuk exe yang benar-benar berjalan.
            string exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                exePath = typeof(AgentServer).Assembly.Location;
            string fv = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
            if (!string.IsNullOrEmpty(fv))
            {
                var parts = fv.Split('.');
                if (parts.Length >= 3) return parts[0] + "." + parts[1] + "." + parts[2];
                return fv;
            }
        }
        catch { /* abaikan — pakai bawaan di bawah */ }
        return "5.1.0";
    }

    private readonly IUiHost _ui;               // jendela utama, untuk Invoke ke thread UI
    private readonly Action<string> _log;       // tulis ke log jendela
    private readonly Action _onActivity;        // dipanggil tiap ada permintaan masuk

    private TcpListener _listener;
    private CancellationTokenSource _cts;

    // Shell persisten (v2.1): satu proses powershell.exe berumur panjang,
    // dipakai bergantian lewat kunci _shellLock (satu perintah per waktu).
    private readonly object _shellLock = new object();
    private ShellSession _shell;
    private int _shellSeq;

    public bool IsRunning { get; private set; }

    // Penghangat relay (v3.6.0): bendera berhenti — dulu thread-nya
    // while(true) abadi dan menumpuk satu per Start ulang (BUG-09).
    private volatile bool _warmerOn;

    // Tombol yang sedang ditahan lewat /keydown (v3.6.0): dicatat
    // supaya bisa dilepas semua lewat /keys/release dan otomatis
    // saat server berhenti (BUG-20 audit).
    private readonly HashSet<ushort> _heldKeys = new HashSet<ushort>();
    private readonly HashSet<string> _heldMouse = new HashSet<string>();
    private readonly object _heldLock = new object();

    // Mesin jadwal (v5.6.0 — Scheduler Engine): thread penilik
    // yang memeriksa jadwal tersimpan (Schedules/SQLite) dan
    // menjalankan rutinitas yang jatuh tempo lewat /routines/run.
    private volatile bool _schedOn;

    public AgentServer(IUiHost ui, Action<string> log, Action onActivity)
    {
        _ui = ui;
        _log = log;
        _onActivity = onActivity;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        IsRunning = true;
        var listener = _listener;
        var token = _cts.Token;
        _ = Task.Run(() => AcceptLoop(listener, token));
        // Pemantau kejadian /events (v2.5.0): proses & jendela
        // dipantau thread latar EventLog sejak listener jalan.
        EventLog.StartMonitor();
        // EventHub (v3.9.0): hook WinEvent di thread UI untuk
        // kejadian fokus & perubahan judul secara real-time.
        EventLog.StartHooks(_ui);
        // Arbitrase (v5.13.0): sensor sentuhan FISIK user —
        // fondasi yield & penjaga idle. Dipasang di thread UI.
        PhysicalInput.StartOnUi(_ui);
        // Refleks (v5.13.0): mesin aturan kejadian — aksi berat
        // (rutinitas/dismiss) memanggil balik ke server ini.
        ReflexEngine.Start(new ReflexEngine.Callbacks
        {
            Log = _log,
            EventAdd = (detail) => EventLog.Add("reflex", detail),
            TaskNote = (teks) => TaskContext.Note(teks),
            RunRoutine = (nama) =>
            {
                try
                {
                    var resp = Route(new Request { Method = "POST", Path = "/routines/run", Query = "", Body = "{\"name\":\"" + nama.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}" });
                    return resp.Status >= 200 && resp.Status < 300;
                }
                catch { return false; }
            },
            Dismiss = (hwnd, teks) => DismissButtonInWindow(hwnd, teks),
        });
        RotateJournalIfNeeded();
        // Penghangat cache relay Tailscale (v2.5.5): /sysinfo
        // pertama sesudah aplikasi mulai dulu lambat (query
        // `tailscale status` dingin). Thread ini mengisi cache
        // sejak start dan menyegarkannya tiap 50 detik (umur
        // cache 60 detik), jadi /sysinfo selalu dari cache.
        _warmerOn = true;
        var warmer = new Thread(() =>
        {
            while (_warmerOn)
            {
                try { TailscaleRelay(); } catch { /* hanya keterangan */ }
                Thread.Sleep(50000);
            }
        }) { IsBackground = true, Name = "RelayWarmer" };
        warmer.Start();
        // Mesin jadwal (v5.6.0): tilik berkala; slot yang
        // terlewat jauh (PC mati) dicatat "terlewat", tidak dikejar.
        // v5.16.0: jeda tilik 20 -> 5 detik (utas khusus ini —
        // SchedulerTick sendiri murah: satu kueri SQLite) agar
        // jadwal "once" tidak telat sampai ±21 detik seperti
        // terukur di program eksperimen Ronde 6.
        _schedOn = true;
        var sched = new Thread(() =>
        {
            while (_schedOn)
            {
                SchedulerTick();
                for (int i = 0; i < 5 && _schedOn; i++) Thread.Sleep(1000);
            }
        }) { IsBackground = true, Name = "Scheduler" };
        sched.Start();
    }

    public void Stop()
    {
        _warmerOn = false;
        _schedOn = false; // v5.6.0: mesin jadwal ikut berhenti
        EventLog.StopHooks(); // v3.9.0: lepas WinEventHook
        ReleaseHeldKeys(); // v3.6.0: jangan tinggalkan tombol tertahan
        MacroRecorder.Shutdown(); // v5.10.0: lepas hook makro & putar ulang
        try { _cts?.Cancel(); } catch { /* abaikan */ }
        try { _listener?.Stop(); } catch { /* abaikan */ }
        _listener = null;
        IsRunning = false;
        // PUTUSKAN / jendela ditutup = shell persisten ikut dimatikan.
        lock (_shellLock) { KillShell(); }
        AiBridge.Shutdown(); // v5.4.0: sidecar AI ikut mati bila kami yang memulai
        PwBridge.Shutdown(); // v5.14.0: sidecar Playwright juga
    }

    private static double GetDouble(JsonElement root, string name, double fallback)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d)) return d;
            if (el.ValueKind == JsonValueKind.String &&
                double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double ds)) return ds;
        }
        return fallback;
    }

    /// <summary>
    /// Satu putaran mesin jadwal (v5.6.0): untuk tiap jadwal aktif
    /// yang slotnya jatuh tempo — jalankan rutinitasnya lewat
    /// /routines/run. Aturan aman: input ter-halt → lewati putaran;
    /// input sedang disewa → tunda ke putaran berikut; slot lewat
    /// lebih dari 15 menit (PC mati/bridge berhenti) → dicatat
    /// "terlewat" dan dilompati ke slot berikutnya, tidak dikejar.
    /// </summary>
    private void SchedulerTick()
    {
        try
        {
            if (_inputHalted) return;
            var now = DateTime.Now;
            foreach (var s in Schedules.List())
            {
                if (!_schedOn) return;
                if (!s.Enabled) continue;
                if (!DateTime.TryParse(s.NextRun, out var next)) continue;
                if (now < next) continue;
                if ((now - next).TotalMinutes > 15)
                {
                    string nx = Schedules.ComputeNext(s.Kind, s.At, s.Day, s.Minutes, now);
                    Schedules.UpdateRun(s.Name, s.LastRun, "terlewat", nx);
                    _log($"jadwal '{s.Name}' terlewat (slot {s.NextRun}) — lompat ke {nx}");
                    continue;
                }
                if (!string.IsNullOrEmpty(ActiveLeaseOwner())) continue; // tunda
                if (!Routines.TryGet(s.Routine, out _))
                {
                    string nx = Schedules.ComputeNext(s.Kind, s.At, s.Day, s.Minutes, now);
                    Schedules.UpdateRun(s.Name, s.LastRun, "rutinitas hilang", nx);
                    _log($"jadwal '{s.Name}': rutinitas '{s.Routine}' tidak ditemukan — dilewati");
                    continue;
                }
                _log($"jadwal '{s.Name}' menjalankan rutinitas '{s.Routine}'");
                var resp = Route(new Request
                {
                    Method = "POST", Path = "/routines/run", Query = "",
                    Body = "{\"name\":" + JsonSerializer.Serialize(s.Routine) + "}",
                });
                if (resp.Status == 429) continue; // balapan sewa — coba putaran berikut
                string status = resp.Status == 200 ? "sukses" : "gagal";
                string nx2 = Schedules.ComputeNext(s.Kind, s.At, s.Day, s.Minutes, DateTime.Now);
                Schedules.UpdateRun(s.Name, Schedules.Iso(DateTime.Now), status, nx2);
                _log($"jadwal '{s.Name}' selesai: {status} (HTTP {resp.Status})");
            }
        }
        catch { /* mesin jadwal tidak boleh menjatuhkan server */ }
    }

    /// <summary>
    /// Jalankan satu aksi batch sebagai sub-permintaan internal
    /// (dipakai jalur utama /batch dan jalur fallback Recovery).
    /// Mengembalikan (status HTTP, hasil terurai).
    /// </summary>
    private (int Status, object Parsed) RunBatchAction(JsonElement act, string doName)
    {
        var subReq = new Request
        {
            Method = "POST",
            Path = "/" + doName.Replace('_', '/'),
            Query = "",
            Body = act.GetRawText(),
        };
        if (doName == "ocr")
        {
            subReq.Method = "GET";
            subReq.Query = "x=" + GetInt(act, "x", 0) +
                           "&y=" + GetInt(act, "y", 0) +
                           "&w=" + GetInt(act, "w", 0) +
                           "&h=" + GetInt(act, "h", 0);
            subReq.Body = "";
        }
        Response subResp;
        try { subResp = Route(subReq); }
        catch (Exception ex)
        {
            return (500, new Dictionary<string, object>
            {
                ["ok"] = false, ["error"] = ex.Message,
            });
        }
        object parsed;
        string rawText = Encoding.UTF8.GetString(subResp.BodyBytes);
        try
        {
            using (var rd = JsonDocument.Parse(rawText))
                parsed = rd.RootElement.Clone();
        }
        catch { parsed = rawText; }
        return (subResp.Status, parsed);
    }

    /// <summary>
    /// Verification Engine (v5.6.0): polling satu kondisi desktop
    /// sampai terbukti atau timeout. Kind: text (OCR wilayah),
    /// uia (nama elemen), value (nilai elemen bernama), window
    /// (judul jendela), process (proses jalan/hilang), stable
    /// (layar tidak berubah selama stableMs), file (berkas ada,
    /// opsional minSize). Mesin yang sama dipakai /verify dan
    /// field "verify" per aksi di /batch.
    /// </summary>
    private bool VerifyPoll(JsonElement verify, Action heartbeat, out string detail)
    {
        string kind = GetString(verify, "kind", "text");
        int timeoutMs = GetInt(verify, "timeoutMs", 3000);
        if (timeoutMs < 250) timeoutMs = 250;
        if (timeoutMs > 30000) timeoutMs = 30000;
        detail = "";
        var sw = Stopwatch.StartNew();
        string lastHash = null;
        long stableSince = -1;
        while (true)
        {
            try { heartbeat?.Invoke(); } catch { /* denyut sewa — abaikan */ }
            bool met = false;
            try { met = EvalVerifyOnce(kind, verify, ref lastHash, ref stableSince, sw, out detail); }
            catch { /* kondisi belum terbaca — lanjut polling */ }
            if (met) return true;
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            Thread.Sleep(250);
        }
    }

    private bool EvalVerifyOnce(string kind, JsonElement v,
        ref string lastHash, ref long stableSince, Stopwatch sw, out string detail)
    {
        detail = "";
        string text = GetString(v, "text", "");
        switch (kind)
        {
            case "text":
            {
                var scr = NativeInput.PrimaryBounds();
                int x = GetInt(v, "x", 0), y = GetInt(v, "y", 0);
                int w = GetInt(v, "w", 0), h = GetInt(v, "h", 0);
                var region = (w > 0 && h > 0) ? new Rectangle(x, y, w, h) : scr;
                using (var bmp = CaptureRegionBitmap(region, 0, out var eff))
                {
                    var rr = OcrReader.Read(bmp, eff.X, eff.Y);
                    detail = rr.Text == null ? "" :
                        (rr.Text.Length > 80 ? rr.Text.Substring(0, 80) : rr.Text);
                    return rr.Text != null &&
                        rr.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            case "uia":
            {
                var items = OnUi(() => UiaScan.ScanForeground(200));
                foreach (var it in items)
                    if (it.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    { detail = it.Name; return true; }
                detail = "elemen tak ditemukan";
                return false;
            }
            case "value":
            {
                string name = GetString(v, "name", text);
                string want = GetString(v, "value", "");
                bool exact = GetBool(v, "exact", false);
                var items = OnUi(() => UiaScan.ScanForeground(300));
                foreach (var it in items)
                {
                    if (it.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    detail = it.Value ?? "";
                    return exact
                        ? string.Equals((it.Value ?? "").Trim(), want.Trim(), StringComparison.OrdinalIgnoreCase)
                        : (it.Value ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                detail = "elemen tak ditemukan";
                return false;
            }
            case "window":
            {
                var wins = OnUi(NativeInput.ListWindows);
                foreach (var win in wins)
                    if (win.Title.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    { detail = win.Title; return true; }
                detail = "jendela tak ditemukan";
                return false;
            }
            case "process":
            {
                string pname = GetString(v, "process", text);
                if (pname.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    pname = pname.Substring(0, pname.Length - 4);
                bool exists = false;
                try { exists = Process.GetProcessesByName(pname).Length > 0; } catch { }
                detail = exists ? "proses berjalan" : "proses tidak ada";
                return GetBool(v, "gone", false) ? !exists : exists;
            }
            case "stable":
            {
                int stableMs = GetInt(v, "stableMs", 600);
                var scr = NativeInput.PrimaryBounds();
                int x = GetInt(v, "x", 0), y = GetInt(v, "y", 0);
                int w = GetInt(v, "w", 0), h = GetInt(v, "h", 0);
                var region = (w > 0 && h > 0) ? new Rectangle(x, y, w, h) : scr;
                string hash;
                using (var bmp = CaptureRegionBitmap(region, 320, out var eff2))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Jpeg);
                    var bytes = ms.ToArray();
                    using (var md5 = MD5.Create())
                        hash = Convert.ToBase64String(md5.ComputeHash(bytes));
                }
                if (hash == lastHash)
                {
                    if (stableSince < 0) stableSince = sw.ElapsedMilliseconds;
                    detail = "layar stabil";
                    return sw.ElapsedMilliseconds - stableSince >= stableMs;
                }
                lastHash = hash;
                stableSince = -1;
                detail = "layar masih berubah";
                return false;
            }
            case "file":
            {
                string path = GetString(v, "path", "");
                if (path.Length == 0) { detail = "path kosong"; return false; }
                if (!File.Exists(path)) { detail = "berkas belum ada"; return false; }
                long len = 0;
                try { len = new FileInfo(path).Length; } catch { }
                detail = "berkas ada (" + len + " byte)";
                int minSize = GetInt(v, "minSize", 0);
                return minSize <= 0 || len >= minSize;
            }
            default:
                detail = "kind verify tak dikenal: " + kind;
                return false;
        }
    }

    /// <summary>Lepas semua tombol yang ditahan /keydown (v3.6.0).</summary>
    private int ReleaseHeldKeys()
    {
        List<ushort> keys;
        lock (_heldLock)
        {
            keys = new List<ushort>(_heldKeys);
            _heldKeys.Clear();
        }
        foreach (var vk in keys)
        {
            try { NativeInput.KeyUp(vk); } catch { /* abaikan */ }
        }
        // v5.9.0: tombol mouse tertahan (/mousedown) dilepas
        // paksa di jaring pengaman yang sama — tidak ada tombol
        // (keyboard maupun mouse) boleh tertahan selamanya.
        List<string> mbtns;
        lock (_heldLock)
        {
            mbtns = new List<string>(_heldMouse);
            _heldMouse.Clear();
        }
        foreach (var b in mbtns)
        {
            try { NativeInput.ButtonUp(b); } catch { /* abaikan */ }
        }
        return keys.Count;
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch
            {
                break; // listener dihentikan / token dibatalkan
            }
            client.NoDelay = true; // v2.7.2: matikan Nagle (stream menulis potongan kecil)
            _ = Task.Run(() => HandleClient(client));
        }
    }

    // ---------- HTTP mini ----------

    private sealed class RequestHead
    {
        public string Method;
        public string Path;   // tanpa query string
        public string Query;  // query string mentah ("" bila tidak ada)
        public int ContentLength;
    }

    private sealed class Request
    {
        public string Method;
        public string Path;
        public string Query; // query string mentah ("" bila tidak ada) — sejak v2.4.0
        public string Body;
    }

    /// <summary>Satu kandidat hasil /locate (v3.6.0).</summary>
    private sealed class LocateHit
    {
        public string Via = "";
        public string Text = "";
        public string Type = "";
        public Rectangle Rect;
    }

    private sealed class ShellSession
    {
        public Process Proc;
        public StreamWriter In;
        public readonly StringBuilder Buffer = new StringBuilder();
        public readonly object BufferLock = new object();
    }

    private sealed class Response
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] BodyBytes = Array.Empty<byte>();

        public static Response Json(string json, int status = 200)
        {
            return new Response { Status = status, BodyBytes = Encoding.UTF8.GetBytes(json) };
        }

        public static Response JsonObject(object value, int status = 200)
        {
            return Json(JsonSerializer.Serialize(value), status);
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 15000;
                stream.WriteTimeout = 15000;
                var head = ReadRequestHead(stream);
                if (head == null) return;

                // Endpoint streaming v2 ditangani langsung di atas stream —
                // tidak lewat pola Request/Response biasa.
                if (head.Method == "GET" && head.Path == "/file")
                {
                    HandleFileDownload(stream, head);
                    return;
                }
                if (head.Method == "GET" && head.Path == "/stream")
                {
                    HandleStream(stream, head);
                    return;
                }
                if (head.Method == "POST" && head.Path == "/upload")
                {
                    HandleUpload(stream, head);
                    return;
                }

                string body = ReadBody(stream, head.ContentLength);
                // TaskJournal (v3.9.0): kosongkan korelasi tugas di
                // thread ini, lalu Route mengisinya dari body bila
                // ada "taskId"; hwnd depan ditangkap SEBELUM Route
                // (keadaan saat operasi diterima).
                _opTaskTls = null;
                long fgHwnd = 0;
                try { fgHwnd = NativeInput.ForegroundHandle().ToInt64(); } catch { }
                var sw = Stopwatch.StartNew();
                var resp = Route(new Request { Method = head.Method, Path = head.Path, Query = head.Query ?? "", Body = body });
                sw.Stop();
                // Catat operasi SETELAH Route selesai tetapi SEBELUM
                // respons ditulis — bila koneksi putus saat menulis,
                // catatannya sudah ada untuk dicek lewat /lastops.
                RecordOp(head.Method, head.Path, resp.Status, (int)sw.ElapsedMilliseconds,
                    _opTaskTls, fgHwnd, SummarizeResponse(resp));
                resp = EnrichError(resp);
                WriteResponse(stream, resp);
            }
            catch
            {
                // Koneksi putus di tengah jalan dsb. — tidak perlu mengganggu user.
            }
        }
    }

    private static RequestHead ReadRequestHead(NetworkStream stream)
    {
        // Baca header byte demi byte sampai ketemu \r\n\r\n (batas 32 KB).
        var header = new MemoryStream();
        var one = new byte[1];
        var terminator = new byte[] { 13, 10, 13, 10 };
        int matched = 0;
        while (header.Length < 32768)
        {
            int n = stream.Read(one, 0, 1);
            if (n == 0) return null;
            header.WriteByte(one[0]);
            if (one[0] == terminator[matched]) matched++;
            else matched = one[0] == 13 ? 1 : 0;
            if (matched == 4) break;
        }

        string headerText = Encoding.ASCII.GetString(header.ToArray());
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        int contentLength = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
        }

        string path = parts[1];
        string query = "";
        int q = path.IndexOf('?');
        if (q >= 0)
        {
            query = path.Substring(q + 1);
            path = path.Substring(0, q);
        }

        return new RequestHead
        {
            Method = parts[0].ToUpperInvariant(),
            Path = path,
            Query = query,
            ContentLength = contentLength,
        };
    }

    private static string ReadBody(NetworkStream stream, int contentLength)
    {
        if (contentLength <= 0) return "";
        if (contentLength > 1048576) contentLength = 1048576; // batas waras 1 MB untuk body JSON
        var buf = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = stream.Read(buf, read, contentLength - read);
            if (n == 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    private static string GetQueryParam(string query, string name)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var pair in query.Split('&'))
        {
            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair.Substring(0, eq) : pair;
            if (string.Equals(WebUtility.UrlDecode(key), name, StringComparison.OrdinalIgnoreCase))
                return eq >= 0 ? WebUtility.UrlDecode(pair.Substring(eq + 1)) : "";
        }
        return null;
    }

    private static void WriteResponse(NetworkStream stream, Response resp)
    {
        string reason;
        switch (resp.Status)
        {
            case 200: reason = "OK"; break;
            case 400: reason = "Bad Request"; break;
            case 404: reason = "Not Found"; break;
            case 409: reason = "Conflict"; break;
            case 504: reason = "Gateway Timeout"; break;
            default: reason = "Internal Server Error"; break;
        }
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(resp.Status).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: ").Append(resp.ContentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(resp.BodyBytes.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (resp.BodyBytes.Length > 0)
            stream.Write(resp.BodyBytes, 0, resp.BodyBytes.Length);
        stream.Flush();
    }

    // ---------- Catatan operasi terakhir (v2.4.0 gelombang 2) ----------
    // Cincin 50 operasi terakhir yang lewat Route: nomor urut, jam,
    // metode, path, status HTTP, durasi. Gunanya anti-ambiguitas —
    // setelah koneksi putus di tengah perintah, Muse bisa mengecek
    // lewat GET /lastops apakah perintahnya sempat dijalankan,
    // tanpa menebak. (/file, /stream, /upload tidak lewat Route dan
    // tidak tercatat di sini; ketiganya tercatat di log berkas.)

    private sealed class OpRecord
    {
        public long Seq;
        public string Time;
        public string Method;
        public string Path;
        public int Status;
        public int Ms;
        // TaskJournal (v3.9.0 — DSA-09/10): korelasi tugas dari
        // body "taskId", hwnd jendela depan saat operasi diterima,
        // dan ringkasan hasil (pesan error bila ada).
        public string TaskId;
        public long FgHwnd;
        public string Summary;
    }

    private readonly List<OpRecord> _ops = new List<OpRecord>();
    private readonly object _opsLock = new object();
    private long _opSeq;

    // Telemetri per endpoint (v3.5.0 — Diagnostics & Telemetry):
    // agregat jumlah/durasi/kegagalan dari operasi yang sama
    // dengan yang dicatat RecordOp (termasuk /ping & /frame),
    // dibaca lewat GET /diag.
    private sealed class DiagStat
    {
        public long Count;
        public long TotalMs;
        public long MaxMs;
        public long Fails;
    }

    private readonly Dictionary<string, DiagStat> _diag = new Dictionary<string, DiagStat>();
    private readonly long _startTick = Environment.TickCount64;

    private void RecordOp(string method, string path, int status, int ms, string taskId = null, long fgHwnd = 0, string summary = "")
    {
        // Telemetri (v3.5.0): SEMUA operasi masuk agregat /diag —
        // termasuk /ping & /frame, karena latensi merekalah yang
        // paling sering diukur audit kecepatan.
        lock (_opsLock)
        {
            string key = method + " " + path;
            if (!_diag.TryGetValue(key, out var ds)) { ds = new DiagStat(); _diag[key] = ds; }
            ds.Count++;
            ds.TotalMs += ms;
            if (ms > ds.MaxMs) ds.MaxMs = ms;
            if (status >= 400) ds.Fails++;
        }
        // v2.4.2: cek kesehatan (/ping) dan cek gambar cepat (/frame)
        // tidak dicatat — keduanya panggilan latar yang sering dan
        // hanya menggeser operasi penting keluar dari cincin 50.
        if (path == "/ping" || path == "/frame") return;
        OpRecord rec = null;
        lock (_opsLock)
        {
            _ops.Add(new OpRecord
            {
                Seq = ++_opSeq,
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Method = method,
                Path = path,
                Status = status,
                Ms = ms,
                TaskId = taskId,
                FgHwnd = fgHwnd,
                Summary = summary ?? "",
            });
            if (_ops.Count > 50) _ops.RemoveRange(0, _ops.Count - 50);
            rec = _ops[_ops.Count - 1];
        }
        // Jurnal persisten (v3.9.0): hanya operasi bertanda tugas —
        // cukup untuk merekonstruksi satu tugas sesudah restart.
        if (rec != null && !string.IsNullOrEmpty(taskId))
            AppendJournal(rec);
    }

    // ---------- TaskJournal persisten (v3.9.0) ----------

    private static string JournalPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jembatan");
        return Path.Combine(dir, "task-journal.jsonl");
    }

    private static void RotateJournalIfNeeded()
    {
        try
        {
            string p = JournalPath();
            var fi = new FileInfo(p);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                string old = p + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(p, old);
            }
        }
        catch { /* jurnal tidak boleh menjatuhkan start */ }
    }

    private void AppendJournal(OpRecord rec)
    {
        try
        {
            var line = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["seq"] = rec.Seq,
                ["time"] = rec.Time,
                ["taskId"] = rec.TaskId,
                ["method"] = rec.Method,
                ["path"] = rec.Path,
                ["status"] = rec.Status,
                ["ms"] = rec.Ms,
                ["fgHwnd"] = rec.FgHwnd,
                ["summary"] = rec.Summary ?? "",
            });
            lock (_opsLock)
            {
                File.AppendAllText(JournalPath(), line + "\n");
            }
        }
        catch { /* jurnal gagal tulis tidak boleh menjatuhkan operasi */ }
    }

    /// <summary>Taksonomi error (v3.10.0 — DSA-11/12): respons
    /// gagal (status ≥400) berbody JSON dengan "error" tetapi
    /// tanpa "code" dilengkapi code + retryable dari peta status.
    /// String error lama tidak diubah — pemanggil lama aman,
    /// pemanggil baru bisa memutuskan ulang-tidak dari data,
    /// bukan dari menebak pesan.</summary>
    private static Response EnrichError(Response resp)
    {
        try
        {
            if (resp == null || resp.Status < 400 || resp.BodyBytes == null ||
                resp.BodyBytes.Length == 0 || resp.BodyBytes.Length > 65536)
                return resp;
            using (var doc = JsonDocument.Parse(resp.BodyBytes))
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return resp;
                if (!root.TryGetProperty("error", out _)) return resp;
                if (root.TryGetProperty("code", out _)) return resp;
                string code; bool retryable;
                switch (resp.Status)
                {
                    case 400: code = "bad_request"; retryable = false; break;
                    case 404: code = "not_found"; retryable = false; break;
                    case 409: code = "conflict"; retryable = false; break;
                    case 429: code = "busy"; retryable = true; break;
                    case 504: code = "ui_timeout"; retryable = false; break;
                    case 500: code = "internal"; retryable = true; break;
                    default: code = "error"; retryable = false; break;
                }
                var dict = new Dictionary<string, object>();
                foreach (var prop in root.EnumerateObject())
                    dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? (object)prop.Value.GetString()
                        : prop.Value.Clone();
                dict["code"] = code;
                dict["retryable"] = retryable;
                return Response.JsonObject(dict, resp.Status);
            }
        }
        catch { return resp; }
    }

    /// <summary>Ringkasan hasil dari badan respons (v3.9.0):
    /// pesan "error" bila respons JSON memuatnya; selain itu
    /// kosong. Dipotong 160 karakter.</summary>
    private static string SummarizeResponse(Response resp)
    {
        try
        {
            if (resp == null || resp.BodyBytes == null || resp.BodyBytes.Length == 0 || resp.BodyBytes.Length > 16384)
                return "";
            using (var doc = JsonDocument.Parse(resp.BodyBytes))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var err) &&
                    err.ValueKind == JsonValueKind.String)
                {
                    string s = err.GetString() ?? "";
                    return s.Length > 160 ? s.Substring(0, 160) : s;
                }
            }
        }
        catch { /* ringkasan hanya keterangan */ }
        return "";
    }

    // ---------- Routing ----------

    // ---------- Rutinitas tersimpan (v2.7.0) ----------

    private static bool ValidRoutineName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (name.Length > 40) return false;
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != ' ' && c != '_' && c != '-')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Saring isi rutinitas: tiap aksi wajib objek ber-"do", dan
    /// aksi yang bisa memanggil rutinitas lain / batch bersarang
    /// dilarang (cegah rekursi tak berujung). null = bersih.
    /// </summary>
    private static string ScanRoutineActions(JsonElement actions)
    {
        foreach (var a in actions.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) return "tiap aksi wajib berupa objek";
            string d = GetString(a, "do", "");
            if (d == "") return "ada aksi tanpa field do";
            if (d == "batch" || d.StartsWith("routines")) return "aksi terlarang di rutinitas: " + d;
        }
        return null;
    }

    private Response Route(Request req)
    {
        _opNameTls = req.Method + " " + req.Path; // nama operasi UI (v3.7.0)
        // TaskJournal (v3.9.0): tangkap "taskId" dari body POST.
        // Hanya menimpa bila benar-benar ada — sub-aksi /batch
        // (Route rekursif di thread yang sama, badannya tanpa
        // taskId) mewarisi tanda tugas panggilan induknya.
        string taskFromBody = ExtractTaskId(req);
        if (taskFromBody != null) _opTaskTls = taskFromBody;
        try
        {
            // Emergency Stop (v4.0.0): selama pengguna menahan
            // input, SEMUA POST pengubah keadaan ditolak 423 —
            // termasuk /batch & /exec. GET tetap jalan agar
            // pemantauan tidak ikut mati. /halt dikecualikan
            // (memasang stop harus selalu bisa).
            if (_inputHalted && req.Method == "POST" && req.Path != "/halt" &&
                (InputPaths.Contains(req.Path) || req.Path == "/batch" ||
                 req.Path == "/exec" || req.Path == "/shell" ||
                 req.Path == "/open" || req.Path == "/upload"))
            {
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "input dihentikan pengguna lewat Emergency Stop — hanya bisa dilepas dari aplikasi MuseLink",
                    ["code"] = "input_halted",
                    ["retryable"] = false,
                }, 423);
            }

            // InputArbiter (v3.11.0): selama /batch memegang lease,
            // endpoint input dari tugas LAIN ditolak 429. Sub-aksi
            // pemilik lease lolos — taskId warisannya cocok.
            if (req.Method == "POST" && InputPaths.Contains(req.Path))
            {
                string pemilik = ActiveLeaseOwner();
                if (pemilik != null && _opTaskTls != pemilik)
                {
                    return Response.JsonObject(new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = "input sedang disewa rangkaian batch lain (" + pemilik + ")",
                        ["code"] = "input_leased",
                        ["retryable"] = true,
                        ["leaseOwner"] = pemilik,
                    }, 429);
                }
            }
            if (req.Method == "GET" && req.Path == "/ping")
            {
                _onActivity();
                // v2.4.2: ping TIDAK dicatat di log jendela — cek
                // kesehatan Muse sering datang dan membanjiri log;
                // label status "aktivitas terakhir" tetap diperbarui
                // lewat _onActivity di atas.
                return Response.Json("{\"ok\":true,\"app\":\"jembatan\",\"version\":\"" + AppVersion + "\",\"admin\":"
                    + (Settings.IsElevated() ? "true" : "false")
                    + ",\"halted\":" + (_inputHalted ? "true" : "false") + "}");
            }

            if (req.Method == "GET" && req.Path == "/screenshot")
            {
                _onActivity();
                // Wilayah potong opsional (v2.4.0): ?x=&y=&w=&h= —
                // tanpa w/h hasilnya layar penuh seperti biasa.
                int sx = ParseClamp(GetQueryParam(req.Query, "x"), 0, 0, 7680);
                int sy = ParseClamp(GetQueryParam(req.Query, "y"), 0, 0, 4320);
                int sw = ParseClamp(GetQueryParam(req.Query, "w"), 0, 0, 7680);
                int sh = ParseClamp(GetQueryParam(req.Query, "h"), 0, 0, 4320);
                // Grid koordinat di PNG penuh (v2.6.1): sama
                // seperti /frame?grid=1 tapi di resolusi asli —
                // presisi maksimal untuk target mungil.
                bool sGrid = GetQueryParam(req.Query, "grid") == "1";
                bool sMark = GetQueryParam(req.Query, "mark") == "1";
                int sStep = ParseClamp(GetQueryParam(req.Query, "step"), 100, 25, 500);
                byte[] png;
                if (sw > 0 && sh > 0)
                {
                    png = OnUi(() =>
                    {
                        using (var bmp = CaptureScreenshotRegionBitmap(sx, sy, sw, sh, out var effS))
                        {
                            if (sGrid) DrawGrid(bmp, effS, sStep);
                            if (sMark) DrawClickMark(bmp, effS);
                            using (var ms = new MemoryStream())
                            {
                                bmp.Save(ms, ImageFormat.Png);
                                return ms.ToArray();
                            }
                        }
                    });
                }
                else if (sGrid || sMark)
                {
                    png = OnUi(() =>
                    {
                        var bnd = NativeInput.PrimaryBounds();
                        using (var bmp = CaptureFullBitmap())
                        {
                            if (sGrid) DrawGrid(bmp, bnd, sStep);
                            if (sMark) DrawClickMark(bmp, bnd);
                            using (var ms = new MemoryStream())
                            {
                                bmp.Save(ms, ImageFormat.Png);
                                return ms.ToArray();
                            }
                        }
                    });
                }
                else
                {
                    png = OnUi(CaptureScreenshot);
                }
                _log("screenshot layar diambil" + (sw > 0 && sh > 0 ? $" (wilayah {sx},{sy} {sw}x{sh})" : "") + (sGrid ? " +grid" : "") + (sMark ? " +mark" : ""));
                return new Response { ContentType = "image/png", BodyBytes = png };
            }

            if (req.Method == "GET" && req.Path == "/uia")
            {
                _onActivity();
                // Peta elemen interaktif jendela aktif (v2.4.0) —
                // klik presisinya lewat POST /uia/click.
                int max = ParseClamp(GetQueryParam(req.Query, "max"), 120, 10, 400);
                var items = OnUi(() => UiaScan.ScanForeground(max));
                var list = new List<Dictionary<string, object>>();
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    list.Add(new Dictionary<string, object>
                    {
                        ["i"] = i,
                        ["name"] = it.Name,
                        ["type"] = it.Type,
                        ["x"] = it.Rect.X,
                        ["y"] = it.Rect.Y,
                        ["w"] = it.Rect.Width,
                        ["h"] = it.Rect.Height,
                        ["enabled"] = it.Enabled,
                        ["value"] = it.Value,
                        ["state"] = it.State,
                        ["patterns"] = it.Patterns,
                        ["automationId"] = it.AutomationId,
                        ["class"] = it.ClassName,
                    });
                }
                var fgTitle = "";
                try
                {
                    var fg = OnUi(NativeInput.ForegroundHandle);
                    foreach (var w in OnUi(NativeInput.ListWindows))
                        if (w.Handle == fg) { fgTitle = w.Title; break; }
                }
                catch { /* judul jendela depan hanya keterangan */ }
                _log($"peta UIA diminta ({list.Count} elemen)");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["window"] = fgTitle,
                    ["count"] = list.Count,
                    ["elements"] = list,
                });
            }

            if (req.Method == "GET" && req.Path == "/clipboard")
            {
                _onActivity();
                string text = OnUi(() =>
                {
                    // Clipboard bisa sedang dikunci aplikasi lain — gagal = teks kosong.
                    try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; }
                    catch { return ""; }
                });
                _log($"clipboard dibaca ({text.Length} karakter)");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["text"] = text,
                });
            }

            if (req.Method == "GET" && req.Path == "/cursor")
            {
                // Posisi kursor saat ini (v5.10.0) — mata kursor:
                // Muse tahu di mana tangannya tanpa motret layar.
                var pos = NativeInput.CursorPosition();
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true, ["x"] = pos.X, ["y"] = pos.Y,
                });
            }

            if (req.Method == "GET" && req.Path == "/clipboard/files")
            {
                _onActivity();
                var files = OnUi(() =>
                {
                    var list = new List<string>();
                    try
                    {
                        if (System.Windows.Clipboard.ContainsFileDropList())
                            foreach (string f in System.Windows.Clipboard.GetFileDropList()) list.Add(f);
                    }
                    catch { /* clipboard dikunci aplikasi lain */ }
                    return list;
                });
                // Isi TIDAK dicatat — hanya jumlahnya (aturan clipboard).
                _log($"clipboard berkas dibaca ({files.Count} berkas)");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true, ["files"] = files,
                });
            }

            if (req.Method == "GET" && req.Path == "/clipboard/image")
            {
                _onActivity();
                string b64 = OnUi(() =>
                {
                    try
                    {
                        if (!System.Windows.Clipboard.ContainsImage()) return "";
                        var img = System.Windows.Clipboard.GetImage();
                        if (img == null) return "";
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                        using (var ms = new MemoryStream()) { enc.Save(ms); return Convert.ToBase64String(ms.ToArray()); }
                    }
                    catch { return ""; }
                });
                if (string.IsNullOrEmpty(b64))
                    return Response.Json("{\"ok\":false,\"error\":\"tidak ada gambar di clipboard\"}", 404);
                _log($"clipboard gambar dibaca ({b64.Length} karakter base64)");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true, ["pngBase64"] = b64,
                });
            }

            if (req.Method == "GET" && req.Path == "/record/status")
            {
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["recording"] = MacroRecorder.IsRecording,
                    ["name"] = MacroRecorder.RecordingName,
                    ["events"] = MacroRecorder.RecordedEvents,
                    ["elapsedMs"] = MacroRecorder.RecordElapsedMs,
                    ["playing"] = MacroRecorder.IsPlaying,
                    ["playingName"] = MacroRecorder.PlayingName,
                });
            }

            if (req.Method == "GET" && req.Path == "/macros")
            {
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true, ["macros"] = MacroRecorder.ListMacros(),
                });
            }

            if (req.Method == "GET" && req.Path == "/windows")
            {
                _onActivity();
                // Skema tunggal v3.8.0 (WindowJson) — /windows kini
                // sekaya /state: hwnd, pid, foreground, hung, state,
                // dialog, ownerHwnd.
                var snapW = OnUi(() => (NativeInput.ForegroundHandle(), NativeInput.ListWindows()));
                var list = new List<Dictionary<string, object>>();
                foreach (var w in snapW.Item2)
                    list.Add(WindowJson(w, snapW.Item1));
                _log($"daftar jendela diminta ({list.Count} jendela)");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["windows"] = list,
                });
            }

            if (req.Method == "GET" && req.Path == "/sysinfo")
            {
                _onActivity();
                var bounds = NativeInput.PrimaryBounds();
                // Metrik kesehatan PC (v2.4.0 gelombang 2) — diukur
                // di thread koneksi ini (±250ms untuk cuplikan CPU).
                var met = NativeInput.SystemMetrics();
                _log("info sistem diminta");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["version"] = AppVersion,
                    ["admin"] = Settings.IsElevated(),
                    ["pc"] = Environment.MachineName,
                    ["windows"] = Environment.OSVersion.VersionString,
                    ["screen"] = bounds.Width + "x" + bounds.Height,
                    ["uptimeSec"] = (int)(Environment.TickCount64 / 1000),
                    ["jembatanPort"] = Port,
                    ["cpuPercent"] = met.CpuPercent,
                    ["ramPercent"] = met.RamPercent,
                    ["ramTotalMb"] = met.RamTotalMb,
                    ["diskFreeGb"] = met.DiskFreeGb,
                    ["diskTotalGb"] = met.DiskTotalGb,
                    ["idleSec"] = met.IdleSec,
                    // v2.5.0: kesehatan aplikasi & keadaan fitur baru.
                    ["appUptimeSec"] = (int)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds,
                    ["keepAwake"] = NativeInput.KeepAwakeOn,
                    ["theme"] = Settings.Theme,
                    ["themeDark"] = UiThemeState.IsDark,
                    ["tailscaleRelay"] = TailscaleRelay(),
                    ["eventsLastSeq"] = EventLog.LastSeq,
                    // v2.7.0: mesin tangkap layar yang sedang aktif.
                    ["capture"] = DxgiCapture.Mode,
                    ["captureScale"] = DxgiCapture.ScaleMode,
                });
            }

            if (req.Method == "GET" && req.Path == "/context")
            {
                _onActivity();
                // Konteks instan (v2.5.9, fitur untuk Muse atas
                // pilihan user): keadaan PC yang paling sering
                // ditanyakan Muse dibungkus SATU respons —
                // jendela depan + daftar jendela + aplikasi
                // penting jalan/tidak + metrik ringan — mengganti
                // 5-6 panggilan terpisah.
                var snap = OnUi(() =>
                {
                    var wins = NativeInput.ListWindows();
                    var fgHandle = NativeInput.ForegroundHandle();
                    NativeInput.WindowInfo fg = null;
                    foreach (var w in wins)
                        if (w.Handle == fgHandle) { fg = w; break; }
                    int clipLen = -1;
                    try { clipLen = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText().Length : 0; }
                    catch { /* clipboard terkunci sesaat */ }
                    return (wins, fg, clipLen, fgHandle);
                });
                var met = NativeInput.SystemMetrics();
                // Skema tunggal v3.8.0 (WindowJson).
                var winList = new List<Dictionary<string, object>>();
                foreach (var w in snap.wins)
                {
                    if (winList.Count >= 25) break;
                    winList.Add(WindowJson(w, snap.fgHandle));
                }
                Dictionary<string, object> fgDict = null;
                if (snap.fg != null)
                    fgDict = WindowJson(snap.fg, snap.fgHandle);
                string spotifyTitle = null;
                bool spotifyRun = false;
                try
                {
                    foreach (var p in Process.GetProcessesByName("Spotify"))
                    {
                        spotifyRun = true;
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(p.MainWindowTitle))
                                spotifyTitle = p.MainWindowTitle;
                        }
                        catch { /* abaikan per proses */ }
                    }
                }
                catch { /* abaikan */ }
                bool Running(string name)
                {
                    try { return Process.GetProcessesByName(name).Length > 0; }
                    catch { return false; }
                }
                var bounds = NativeInput.PrimaryBounds();
                _log($"konteks instan diminta ({winList.Count} jendela)");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["version"] = AppVersion,
                    ["admin"] = Settings.IsElevated(),
                    ["task"] = TaskContext.Snapshot(),
                    ["foreground"] = fgDict,
                    ["windows"] = winList,
                    ["apps"] = new Dictionary<string, object>
                    {
                        ["obs"] = Running("obs64"),
                        ["chrome"] = Running("chrome"),
                        ["wplay"] = Running("wPlay"),
                        ["spotify"] = spotifyRun,
                    },
                    ["spotifyTitle"] = spotifyTitle,
                    ["cpuPercent"] = met.CpuPercent,
                    ["ramPercent"] = met.RamPercent,
                    ["idleSec"] = met.IdleSec,
                    ["screen"] = bounds.Width + "x" + bounds.Height,
                    ["appUptimeSec"] = (int)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds,
                    ["keepAwake"] = NativeInput.KeepAwakeOn,
                    ["theme"] = Settings.Theme,
                    ["relay"] = TailscaleRelay(),
                    ["clipboardChars"] = snap.clipLen,
                    ["eventsLastSeq"] = EventLog.LastSeq,
                });
            }

            if (req.Method == "GET" && req.Path == "/delta")
            {
                _onActivity();
                // Smart frame (v2.6.4, pilihan user): kirim
                // hanya potongan wilayah yang BERUBAH sejak
                // panggilan delta sebelumnya pada lebar yang
                // sama. Layar kebanyakan diam -> muatan
                // menyusut drastis di jalur relay lambat.
                // Perubahan >60% / panggilan pertama / lebar
                // beda -> bingkai penuh; nol perubahan ->
                // mode "none" tanpa gambar.
                int dw = ParseClamp(GetQueryParam(req.Query, "w"), 1280, 320, 1920);
                int dq = ParseClamp(GetQueryParam(req.Query, "q"), 70, 30, 95);
                var scrD = NativeInput.PrimaryBounds();
                Bitmap cur;
                try { cur = NativeInput.CaptureScreenScaled(dw); }
                catch { cur = CaptureScaledBitmap(dw); }
                string mode = "full";
                Rectangle cropScr = scrD;
                double cov = 100.0;
                int blocks = -1;
                lock (_deltaLock)
                {
                    bool sama = _deltaBase != null && _deltaBase.Width == cur.Width && _deltaBase.Height == cur.Height;
                    if (sama)
                    {
                        var ubah = CompareBlocks(_deltaBase, cur, 32, 16, 0, 0, out int totalB);
                        cov = totalB > 0 ? ubah.Count * 100.0 / totalB : 0;
                        blocks = ubah.Count;
                        if (ubah.Count == 0)
                        {
                            mode = "none";
                        }
                        else if (cov <= 60.0)
                        {
                            var u = ubah[0];
                            foreach (var b in ubah) u = Rectangle.Union(u, b);
                            double kx = (double)scrD.Width / cur.Width;
                            double ky = (double)scrD.Height / cur.Height;
                            cropScr = new Rectangle(
                                Math.Max(0, (int)(u.X * kx) - 8),
                                Math.Max(0, (int)(u.Y * ky) - 8),
                                (int)(u.Width * kx) + 16,
                                (int)(u.Height * ky) + 16);
                            cropScr.Intersect(scrD);
                            mode = "crop";
                        }
                    }
                    if (_deltaBase != null) _deltaBase.Dispose();
                    _deltaBase = (Bitmap)cur.Clone();
                }
                byte[] jpeg;
                if (mode == "crop")
                {
                    using (var bmp = CaptureRegionBitmap(cropScr, 0, out _))
                        jpeg = EncodeJpeg(bmp, dq);
                }
                else if (mode == "none")
                {
                    jpeg = Array.Empty<byte>();
                    cropScr = scrD;
                }
                else
                {
                    jpeg = EncodeJpeg(cur, dq);
                }
                cur.Dispose();
                _log($"delta: mode {mode}, cakupan {(blocks < 0 ? "-" : cov.ToString("0.0") + "%")}, kirim {jpeg.Length} byte");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["mode"] = mode,
                    ["x"] = cropScr.X, ["y"] = cropScr.Y,
                    ["w"] = cropScr.Width, ["h"] = cropScr.Height,
                    ["coveragePct"] = blocks < 0 ? (double?)null : Math.Round(cov, 1),
                    ["changedBlocks"] = blocks,
                    ["frame"] = new Dictionary<string, object>
                    {
                        ["mime"] = "image/jpeg",
                        ["b64"] = Convert.ToBase64String(jpeg),
                    },
                });
            }

            if (req.Method == "GET" && req.Path == "/inspect")
            {
                _onActivity();
                // Inspeksi super (v2.6.3, pilihan user): SATU
                // respons berisi bingkai wilayah + OCR adaptif
                // + elemen UIA jendela depan + konteks ringkas.
                // Mengganti 3-4 panggilan terpisah (frame, ocr,
                // uia, context) tiap Muse memeriksa satu
                // tempat — hemat 2-3 detik per pemeriksaan.
                int ix = ParseClamp(GetQueryParam(req.Query, "x"), 0, 0, 7680);
                int iy = ParseClamp(GetQueryParam(req.Query, "y"), 0, 0, 4320);
                int iw2 = ParseClamp(GetQueryParam(req.Query, "w"), 0, 0, 7680);
                int ih = ParseClamp(GetQueryParam(req.Query, "h"), 0, 0, 4320);
                int ifw = ParseClamp(GetQueryParam(req.Query, "fw"), 960, 320, 1920);
                int iq = ParseClamp(GetQueryParam(req.Query, "q"), 70, 30, 95);
                bool withUia = GetQueryParam(req.Query, "uia") != "0";
                var scrI = NativeInput.PrimaryBounds();
                var mintaI = (iw2 > 0 && ih > 0) ? new Rectangle(ix, iy, iw2, ih) : scrI;
                // Jendela latar (v2.6.4): ?win=<potongan judul>
                // mengarahkan inspeksi ke jendela itu (wilayah
                // = rect jendelanya, UIA dipindai dari jendela
                // itu, bukan yang di depan). ?rel=1 membuat
                // koordinat OCR/UIA relatif ke sudut jendela.
                NativeInput.WindowInfo fWinI = null;
                bool relI = GetQueryParam(req.Query, "rel") == "1";
                string winQ = GetQueryParam(req.Query, "win");
                if (!string.IsNullOrWhiteSpace(winQ))
                {
                    fWinI = FindWindowByTitle(winQ);
                    if (fWinI == null)
                        return Response.Json("{\"ok\":false,\"error\":\"jendela tidak ditemukan: " + winQ.Replace("\\", " ").Replace("\"", " ") + "\"}", 404);
                    mintaI = fWinI.Rect;
                    // v2.7.3: jendela terminimize — Windows melipat
                    // pohon UIA-nya dan rect-nya jatuh di luar
                    // layar, jadi pindaian biasa menghasilkan
                    // kekosongan yang menyesatkan. Laporkan terus
                    // terang supaya pemanggil tahu sebabnya.
                    if (NativeInput.IsWindowMinimized(fWinI.Handle))
                        return Response.Json("{\"ok\":true,\"minimized\":true,\"note\":\"jendela terminimize - UIA/OCR kosong; pulihkan jendelanya untuk inspeksi penuh\",\"window\":{\"title\":\"" + fWinI.Title.Replace("\\", " ").Replace("\"", " ") + "\"}}");
                }
                int relOxI = (relI && fWinI != null) ? fWinI.Rect.X : 0;
                int relOyI = (relI && fWinI != null) ? fWinI.Rect.Y : 0;
                Rectangle effI;
                OcrReader.Result ocrRes;
                string ocrMode;
                using (var bmp = CaptureRegionBitmap(mintaI, 0, out effI))
                {
                    string engI = GetQueryParam(req.Query, "engine") ?? "";
                    if (engI == "tesseract") { ocrRes = OcrReader.ReadTesseract(bmp, effI.X, effI.Y); ocrMode = "tesseract"; }
                    else if (engI == "auto") { ocrRes = OcrAdaptivePick(bmp, effI.X, effI.Y, out ocrMode, true); }
                    else { ocrRes = OcrAdaptivePick(bmp, effI.X, effI.Y, out ocrMode); }
                }
                byte[] jpeg;
                int fW, fH;
                using (var bmpF = CaptureRegionBitmap(effI, ifw, out _))
                {
                    fW = bmpF.Width; fH = bmpF.Height;
                    jpeg = EncodeJpeg(bmpF, iq);
                }
                var kata = new List<Dictionary<string, object>>();
                foreach (var wd in ocrRes.Words)
                {
                    kata.Add(new Dictionary<string, object>
                    {
                        ["t"] = wd.Text,
                        ["x"] = wd.Rect.X - relOxI, ["y"] = wd.Rect.Y - relOyI,
                        ["w"] = wd.Rect.Width, ["h"] = wd.Rect.Height,
                    });
                }
                var uiaList = new List<Dictionary<string, object>>();
                if (withUia)
                {
                    var items = fWinI != null
                        ? OnUi(() => UiaScan.ScanWindow(fWinI.Handle, 300))
                        : OnUi(() => UiaScan.ScanForeground(300));
                    foreach (var it in items)
                    {
                        if (uiaList.Count >= 100) break;
                        if (!it.Rect.IntersectsWith(effI)) continue;
                        uiaList.Add(new Dictionary<string, object>
                        {
                            ["name"] = it.Name, ["type"] = it.Type,
                            ["x"] = it.Rect.X - relOxI, ["y"] = it.Rect.Y - relOyI,
                            ["w"] = it.Rect.Width, ["h"] = it.Rect.Height,
                            ["cx"] = it.CenterX - relOxI, ["cy"] = it.CenterY - relOyI,
                        });
                    }
                }
                var fgI = OnUi(() =>
                {
                    var wins = NativeInput.ListWindows();
                    var h = NativeInput.ForegroundHandle();
                    foreach (var w in wins) if (w.Handle == h) return w;
                    return null;
                });
                Dictionary<string, object> fgDictI = null;
                if (fgI != null)
                {
                    fgDictI = new Dictionary<string, object>
                    {
                        ["title"] = fgI.Title, ["process"] = fgI.Process,
                        ["x"] = fgI.Rect.X, ["y"] = fgI.Rect.Y,
                        ["w"] = fgI.Rect.Width, ["h"] = fgI.Rect.Height,
                    };
                }
                var metI = NativeInput.SystemMetrics();
                bool RunningI(string name)
                {
                    try { return Process.GetProcessesByName(name).Length > 0; }
                    catch { return false; }
                }
                _log($"inspect wilayah {effI.X},{effI.Y} {effI.Width}x{effI.Height}: {kata.Count} kata ({ocrMode}), {uiaList.Count} elemen");
                var inspResp = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["region"] = new Dictionary<string, object>
                    {
                        ["x"] = effI.X, ["y"] = effI.Y,
                        ["w"] = effI.Width, ["h"] = effI.Height,
                    },
                    ["frame"] = new Dictionary<string, object>
                    {
                        ["mime"] = "image/jpeg",
                        ["width"] = fW, ["height"] = fH,
                        ["b64"] = Convert.ToBase64String(jpeg),
                    },
                    ["ocr"] = new Dictionary<string, object>
                    {
                        ["adaptive"] = true,
                        ["mode"] = ocrMode,
                        ["text"] = ocrRes.Text,
                        ["words"] = kata,
                    },
                    ["uia"] = uiaList,
                    ["context"] = new Dictionary<string, object>
                    {
                        ["foreground"] = fgDictI,
                        ["apps"] = new Dictionary<string, object>
                        {
                            ["obs"] = RunningI("obs64"),
                            ["chrome"] = RunningI("chrome"),
                            ["wplay"] = RunningI("wPlay"),
                            ["spotify"] = RunningI("Spotify"),
                        },
                        ["cpuPercent"] = metI.CpuPercent,
                        ["ramPercent"] = metI.RamPercent,
                        ["idleSec"] = metI.IdleSec,
                        ["relay"] = TailscaleRelay(),
                        ["theme"] = Settings.Theme,
                    },
                };
                if (fWinI != null)
                {
                    inspResp["window"] = new Dictionary<string, object>
                    {
                        ["title"] = fWinI.Title, ["process"] = fWinI.Process,
                        ["x"] = fWinI.Rect.X, ["y"] = fWinI.Rect.Y,
                        ["w"] = fWinI.Rect.Width, ["h"] = fWinI.Rect.Height,
                    };
                    inspResp["relative"] = relI;
                }
                return Response.JsonObject(inspResp);
            }

            if (req.Method == "GET" && req.Path == "/routines")
            {
                _onActivity();
                var daftar = new List<object>();
                foreach (var kv in Routines.List())
                {
                    daftar.Add(new Dictionary<string, object>
                    {
                        ["name"] = kv.Key,
                        ["steps"] = kv.Value,
                    });
                }
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["routines"] = daftar,
                });
            }

            if (req.Method == "GET" && req.Path == "/schedules")
            {
                // Daftar jadwal tersimpan (v5.6.0 — Scheduler
                // Engine), lengkap dengan slot berikutnya dan
                // catatan jalan terakhir.
                _onActivity();
                var daftarJ = new List<object>();
                foreach (var rowJ in Schedules.List())
                    daftarJ.Add(Schedules.RowJson(rowJ));
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["schedules"] = daftarJ,
                });
            }

            if (req.Method == "GET" && req.Path == "/state")
            {
                _onActivity();
                // Potret kondisi desktop menyeluruh (v3.5.0 —
                // Desktop State Awareness): monitor, semua jendela
                // (posisi + status hang), jendela depan, mouse,
                // dan idle user — satu panggilan menggantikan
                // tebak-tebakan dari screenshot.
                // Satu snapshot OnUi gabungan (v3.8.0 — DSA-08):
                // dulu 1 + 1 + N panggilan UI (IsHung per jendela),
                // kini tepat satu; skema lewat WindowJson.
                var snapS = OnUi(() => (NativeInput.ForegroundHandle(), NativeInput.ListWindows()));
                var fgH = snapS.Item1;
                var wins = snapS.Item2;
                var wlist = new List<Dictionary<string, object>>();
                foreach (var w in wins)
                    wlist.Add(WindowJson(w, fgH));
                var mons = new List<Dictionary<string, object>>();
                foreach (var s in NativeInput.AllScreens())
                {
                    mons.Add(new Dictionary<string, object>
                    {
                        ["device"] = s.DeviceName,
                        ["x"] = s.Bounds.X,
                        ["y"] = s.Bounds.Y,
                        ["w"] = s.Bounds.Width,
                        ["h"] = s.Bounds.Height,
                        ["primary"] = s.Primary,
                    });
                }
                double dpiScale = 1.0;
                try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = Math.Round(g.DpiX / 96.0, 3); } catch { }
                var mouse = NativeInput.CursorPosition();
                int idleSec = -1;
                try { idleSec = NativeInput.IdleSeconds(); } catch { }
                string fgTitle = "";
                foreach (var w in wins) if (w.Handle == fgH) { fgTitle = w.Title; break; }
                _log($"potret kondisi desktop diminta ({wlist.Count} jendela)");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["monitors"] = mons,
                    ["dpiScale"] = dpiScale,
                    ["foreground"] = fgTitle,
                    ["windows"] = wlist,
                    ["mouse"] = new Dictionary<string, object> { ["x"] = mouse.X, ["y"] = mouse.Y },
                    ["userIdleSec"] = idleSec,
                });
            }

            if (req.Method == "GET" && req.Path == "/diag")
            {
                _onActivity();
                // Telemetri (v3.5.0 — Diagnostics & Telemetry):
                // agregat latensi & kegagalan per endpoint sejak
                // aplikasi mulai + resource proses MuseLink.
                var eps = new List<Dictionary<string, object>>();
                lock (_opsLock)
                {
                    foreach (var kv in _diag)
                    {
                        var d = kv.Value;
                        eps.Add(new Dictionary<string, object>
                        {
                            ["endpoint"] = kv.Key,
                            ["count"] = d.Count,
                            ["avgMs"] = d.Count > 0 ? d.TotalMs / d.Count : 0,
                            ["maxMs"] = d.MaxMs,
                            ["fails"] = d.Fails,
                        });
                    }
                }
                eps.Sort((a, b) => ((long)b["count"]).CompareTo((long)a["count"]));
                var proc = Process.GetCurrentProcess();
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["version"] = AppVersion,
                    ["uptimeSec"] = (Environment.TickCount64 - _startTick) / 1000,
                    ["process"] = new Dictionary<string, object>
                    {
                        ["ramMB"] = proc.WorkingSet64 / 1048576,
                        ["cpuSec"] = Math.Round(proc.TotalProcessorTime.TotalSeconds, 1),
                        ["threads"] = proc.Threads.Count,
                        // v5.12.0: pisahkan umur PROSES dari umur
                        // listener (uptimeSec di atas). Penyelidikan
                        // 26 Nov: listener yang dimatikan/dinyalakan
                        // dari UI sempat terbaca sebagai "aplikasi
                        // restart" — kini keduanya terbaca jelas.
                        ["pid"] = proc.Id,
                        ["processUptimeSec"] = (long)(DateTime.Now - proc.StartTime).TotalSeconds,
                    },
                    ["endpoints"] = eps,
                });
            }

            if (req.Method == "GET" && req.Path == "/op")
            {
                // Status SATU operasi UI (v3.7.0 — BUG-10): pasangan
                // respons 504. Operasi yang timeout tetap tercatat
                // sampai selesai di latar (state "done-late").
                if (!long.TryParse(GetQueryParam(req.Query, "id"), out long opId) ||
                    !_uiOps.TryGetValue(opId, out var uop))
                    return Response.Json("{\"ok\":false,\"error\":\"operasi tidak ditemukan\"}", 404);
                long nowT = Environment.TickCount64;
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["id"] = uop.Id,
                    ["name"] = uop.Name,
                    ["taskId"] = uop.TaskId,
                    ["state"] = uop.State,
                    ["durationMs"] = (uop.FinishedTick > 0 ? uop.FinishedTick : nowT) - uop.StartedTick,
                    ["error"] = uop.Error,
                });
            }

            if (req.Method == "GET" && req.Path == "/ops")
            {
                // Daftar operasi UI terakhir (v3.7.0): terbaru dulu,
                // maksimal 20 dari 100 catatan yang disimpan.
                var all = new List<UiOp>(_uiOps.Values);
                all.Sort((a, b) => b.Id.CompareTo(a.Id));
                var list = new List<Dictionary<string, object>>();
                long nowT = Environment.TickCount64;
                foreach (var uop in all)
                {
                    if (list.Count >= 20) break;
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = uop.Id,
                        ["name"] = uop.Name,
                        ["taskId"] = uop.TaskId,
                        ["state"] = uop.State,
                        ["durationMs"] = (uop.FinishedTick > 0 ? uop.FinishedTick : nowT) - uop.StartedTick,
                    });
                }
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["ops"] = list,
                });
            }

            if (req.Method == "POST" && req.Path == "/op/cancel")
            {
                // Batalkan operasi UI yang MASIH ANTRI (v3.10.0 —
                // DSA-14): yang sudah berjalan tidak bisa
                // dibatalkan dan dijawab jujur begitu.
                using (var doc = JsonDocument.Parse(req.Body))
                {
                    long cancelId = GetLong(doc.RootElement, "id", 0);
                    if (cancelId <= 0 || !_uiOps.TryGetValue(cancelId, out var cop))
                        return Response.Json("{\"ok\":false,\"error\":\"operasi tidak ditemukan\"}", 404);
                    if (cop.FinishedTick > 0)
                        return Response.JsonObject(new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["error"] = "operasi sudah selesai (keadaan: " + cop.State + ")",
                            ["code"] = "op_finished",
                            ["retryable"] = false,
                        }, 409);
                    if (cop.Started)
                        return Response.JsonObject(new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["error"] = "operasi sudah berjalan di thread UI — tidak bisa dibatalkan",
                            ["code"] = "op_running",
                            ["retryable"] = false,
                        }, 409);
                    cop.CancelRequested = true;
                    _log($"operasi UI #{cancelId} ({cop.Name}) ditandai batal selagi antre");
                    return Response.JsonObject(new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["cancelled"] = true,
                        ["id"] = cancelId,
                        ["note"] = "operasi akan dilewati saat gilirannya tiba; pemanggilnya menerima 409 op_cancelled",
                    });
                }
            }

            if (req.Method == "GET" && req.Path == "/health")
            {
                // Diagnosis satu panggilan (v3.10.0 — DSA-13).
                // Isi dibangun BuildHealth() — UI Control Room
                // (v4.0.0) membaca data yang sama persis.
                return Response.JsonObject(BuildHealth());
            }

            if (req.Method == "GET" && req.Path == "/task")
            {
                // Task Memory & Context (v5.12.0): potret tugas
                // aktif + tugas terakhir yang selesai.
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["task"] = TaskContext.Snapshot(),
                });
            }

            if (req.Method == "GET" && req.Path == "/input/lease")
            {
                // Arbitrase (v5.13.0): siapa memegang lease input.
                var (pemilik, sisa) = LeaseInfo();
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["leased"] = pemilik != null,
                    ["owner"] = pemilik,
                    ["remainingSec"] = sisa,
                });
            }

            if (req.Method == "GET" && req.Path == "/pw/state")
            {
                // Playwright (v5.14.0): keadaan pendamping —
                // terpasang? sehat? profil otomasi di mana.
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["installed"] = PwBridge.Installed,
                    ["healthy"] = PwBridge.Healthy(),
                    ["profile"] = PwBridge.ProfileDir,
                    ["lastError"] = PwBridge.LastError,
                });
            }

            if (req.Method == "GET" && req.Path == "/reflex/rules")
            {
                var daftar = new List<Dictionary<string, object>>();
                foreach (var r in ReflexEngine.Rules())
                    daftar.Add(RuleJson(r));
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["rules"] = daftar,
                });
            }

            if (req.Method == "GET" && req.Path == "/reflex/log")
            {
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["fires"] = ReflexEngine.Fires(),
                });
            }

            if (req.Method == "POST" && req.Path == "/halt")
            {
                // Emergency Stop dari sisi agen (v4.0.0): agen
                // BOLEH menghentikan dirinya sendiri, tetapi
                // MELEPAS stop hanya bisa dari aplikasi — jaringan
                // tidak boleh membatalkan keputusan pengguna.
                using (var doc = JsonDocument.Parse(req.Body))
                {
                    bool on = GetBool(doc.RootElement, "on", true);
                    if (!on)
                        return Response.JsonObject(new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["error"] = "Emergency Stop hanya bisa dilepas dari aplikasi MuseLink",
                            ["code"] = "halt_release_forbidden",
                            ["retryable"] = false,
                        }, 403);
                    SetHalt(true, "agen (POST /halt)");
                    return Response.JsonObject(new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["halted"] = true,
                        ["note"] = "semua input ditahan sampai pengguna melepas Stop dari aplikasi",
                    });
                }
            }

            if (req.Method == "GET" && req.Path == "/task")
            {
                // TaskJournal (v3.9.0 — DSA-09/10): seluruh operasi
                // satu tugas (yang membawa "taskId" di body POST)
                // sebagai satu rangkaian — dari cincin memori;
                // bila tidak ada (mis. aplikasi sempat restart),
                // dari jurnal berkas task-journal.jsonl.
                string taskId = GetQueryParam(req.Query, "id") ?? "";
                if (string.IsNullOrWhiteSpace(taskId))
                    return Response.Json("{\"ok\":false,\"error\":\"parameter id (taskId) wajib\"}", 400);
                var opsList = new List<Dictionary<string, object>>();
                lock (_opsLock)
                {
                    foreach (var op in _ops)
                    {
                        if (op.TaskId != taskId) continue;
                        opsList.Add(new Dictionary<string, object>
                        {
                            ["seq"] = op.Seq, ["time"] = op.Time, ["method"] = op.Method,
                            ["path"] = op.Path, ["status"] = op.Status, ["ms"] = op.Ms,
                            ["fgHwnd"] = op.FgHwnd, ["summary"] = op.Summary ?? "",
                        });
                    }
                }
                string source = "memory";
                if (opsList.Count == 0)
                {
                    // Pindai jurnal berkas (maks ~4MB ekor berkas).
                    try
                    {
                        string jp = JournalPath();
                        if (File.Exists(jp))
                        {
                            string[] lines = File.ReadAllLines(jp);
                            foreach (var line in lines)
                            {
                                if (!line.Contains(taskId)) continue;
                                try
                                {
                                    using (var doc = JsonDocument.Parse(line))
                                    {
                                        var r = doc.RootElement;
                                        if (GetString(r, "taskId", "") != taskId) continue;
                                        opsList.Add(new Dictionary<string, object>
                                        {
                                            ["seq"] = GetLong(r, "seq", 0), ["time"] = GetString(r, "time", ""),
                                            ["method"] = GetString(r, "method", ""), ["path"] = GetString(r, "path", ""),
                                            ["status"] = GetInt(r, "status", 0), ["ms"] = GetInt(r, "ms", 0),
                                            ["fgHwnd"] = GetLong(r, "fgHwnd", 0), ["summary"] = GetString(r, "summary", ""),
                                        });
                                    }
                                }
                                catch { /* baris rusak — lewati */ }
                            }
                            if (opsList.Count > 0) source = "journal";
                        }
                    }
                    catch { /* jurnal tak terbaca — anggap tidak ada */ }
                }
                var uiList = new List<Dictionary<string, object>>();
                long nowT2 = Environment.TickCount64;
                foreach (var uop in _uiOps.Values)
                {
                    if (uop.TaskId != taskId) continue;
                    uiList.Add(new Dictionary<string, object>
                    {
                        ["id"] = uop.Id, ["name"] = uop.Name, ["state"] = uop.State,
                        ["durationMs"] = (uop.FinishedTick > 0 ? uop.FinishedTick : nowT2) - uop.StartedTick,
                        ["error"] = uop.Error,
                    });
                }
                uiList.Sort((a, b) => ((long)a["id"]).CompareTo((long)b["id"]));
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["taskId"] = taskId,
                    ["found"] = opsList.Count > 0 || uiList.Count > 0,
                    ["source"] = source,
                    ["ops"] = opsList,
                    ["uiOps"] = uiList,
                });
            }

            if (req.Method == "GET" && req.Path == "/lastops")
            {
                // Tanpa _onActivity/_log berlebihan: ini jalur cek
                // pasca-putus, isinya catatan operasi Route terakhir.
                List<OpRecord> snap;
                lock (_opsLock) { snap = new List<OpRecord>(_ops); }
                snap.Reverse();
                var list = new List<Dictionary<string, object>>();
                foreach (var op in snap)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["seq"] = op.Seq,
                        ["time"] = op.Time,
                        ["method"] = op.Method,
                        ["path"] = op.Path,
                        ["status"] = op.Status,
                        ["ms"] = op.Ms,
                        ["taskId"] = op.TaskId,
                        ["fgHwnd"] = op.FgHwnd,
                        ["summary"] = op.Summary ?? "",
                    });
                }
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["ops"] = list,
                });
            }

            if (req.Method == "GET" && req.Path == "/frame")
            {
                _onActivity();
                // Bingkai instan (v2.4.0 gelombang 2): tangkapan JPEG
                // terkompresi & diperkecil — jauh lebih ringan dari
                // PNG layar penuh /screenshot untuk lingkaran
                // lihat-klik yang rapat. BitBlt tidak butuh thread UI
                // (pelajaran dari /stream v2.1).
                // Preset (v5.17.0): ?preset=status setara
                // w=640&q=50 — bingkai cek status cepat (muatan
                // kecil, tuas kecepatan terbesar menurut audit
                // Ronde 4). Parameter w/q eksplisit tetap menang:
                // ParseClamp hanya memakai bawaan preset bila
                // w/q tidak diberikan.
                bool presetStatus = GetQueryParam(req.Query, "preset") == "status";
                int fw = ParseClamp(GetQueryParam(req.Query, "w"), presetStatus ? 640 : 1280, 320, 1920);
                int fq = ParseClamp(GetQueryParam(req.Query, "q"), presetStatus ? 50 : 70, 30, 95);
                // Format PNG tanpa hilang (v3.5.0): ?fmt=png — teks
                // kecil (panel Figma dsb.) terbaca tajam, tidak blur
                // JPEG. Muatan lebih besar; pakai untuk wilayah.
                bool pngFmt = GetQueryParam(req.Query, "fmt") == "png";
                // Wilayah (v2.4.4): rx/ry/rw/rh membatasi tangkapan ke
                // satu wilayah — muatan jauh lebih kecil daripada
                // bingkai layar penuh (kurva audit #1: ukuran muatan
                // adalah tuas kecepatan terbesar yang tersisa).
                int rx = ParseClamp(GetQueryParam(req.Query, "rx"), 0, 0, 7680);
                int ry = ParseClamp(GetQueryParam(req.Query, "ry"), 0, 0, 4320);
                int rw = ParseClamp(GetQueryParam(req.Query, "rw"), 0, 0, 7680);
                int rh = ParseClamp(GetQueryParam(req.Query, "rh"), 0, 0, 4320);
                // Grid koordinat (v2.6.0): garis + label koordinat
                // LAYAR digambar di salinan gambar (layar user tidak
                // berubah) — Muse membaca koordinat klik persis
                // dari label, bukan menebak piksel.
                bool gridOn = GetQueryParam(req.Query, "grid") == "1";
                bool markOn = GetQueryParam(req.Query, "mark") == "1";
                int gridStep = ParseClamp(GetQueryParam(req.Query, "step"), 100, 25, 500);
                byte[] jpeg;
                if (rw > 0 && rh > 0)
                {
                    using (var bmp = CaptureRegionBitmap(new Rectangle(rx, ry, rw, rh), fw, out var effR))
                    {
                        if (gridOn) DrawGrid(bmp, effR, gridStep);
                        if (markOn) DrawClickMark(bmp, effR);
                        jpeg = pngFmt ? EncodePng(bmp) : EncodeJpeg(bmp, fq);
                    }
                    _log($"bingkai wilayah diambil ({rx},{ry} {rw}x{rh} -> {fw}px, q{fq})" + (gridOn ? " +grid" : ""));
                }
                else
                {
                    var scrB = NativeInput.PrimaryBounds();
                    try
                    {
                        using (var bmp = NativeInput.CaptureScreenScaled(fw))
                        {
                            if (gridOn) DrawGrid(bmp, scrB, gridStep);
                            if (markOn) DrawClickMark(bmp, scrB);
                            jpeg = pngFmt ? EncodePng(bmp) : EncodeJpeg(bmp, fq);
                        }
                    }
                    catch
                    {
                        using (var bmp = CaptureScaledBitmap(fw))
                        {
                            if (gridOn) DrawGrid(bmp, scrB, gridStep);
                            if (markOn) DrawClickMark(bmp, scrB);
                            jpeg = pngFmt ? EncodePng(bmp) : EncodeJpeg(bmp, fq);
                        }
                    }
                    _log($"bingkai instan diambil ({fw}px, q{fq})" + (gridOn ? " +grid" : ""));
                }
                return new Response { ContentType = pngFmt ? "image/png" : "image/jpeg", BodyBytes = jpeg };
            }

            if (req.Method == "GET" && req.Path == "/burst")
            {
                _onActivity();
                // Snapshot beruntun (v2.6.0): beberapa bingkai
                // ditangkap di sisi PC lalu disusun vertikal jadi
                // SATU gambar berlabel waktu — Muse bisa membaca
                // gerakan (loading, animasi) dalam satu panggilan.
                int bn = ParseClamp(GetQueryParam(req.Query, "n"), 4, 2, 8);
                int bw = ParseClamp(GetQueryParam(req.Query, "w"), 640, 320, 1280);
                int bq = ParseClamp(GetQueryParam(req.Query, "q"), 60, 30, 95);
                int bgap = ParseClamp(GetQueryParam(req.Query, "gap"), 350, 0, 2000);
                // Burst wilayah (v2.6.1): rx/ry/rw/rh membatasi
                // bingkai ke satu wilayah — teks di tiap bingkai
                // kebaca, muatan lebih kecil.
                int brx = ParseClamp(GetQueryParam(req.Query, "rx"), 0, 0, 7680);
                int bry = ParseClamp(GetQueryParam(req.Query, "ry"), 0, 0, 4320);
                int brw = ParseClamp(GetQueryParam(req.Query, "rw"), 0, 0, 7680);
                int brh = ParseClamp(GetQueryParam(req.Query, "rh"), 0, 0, 4320);
                bool burstRegion = brw > 0 && brh > 0;
                var frames = new List<Bitmap>();
                var stamps = new List<long>();
                var swBurst = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    for (int i = 0; i < bn; i++)
                    {
                        if (i > 0 && bgap > 0) Thread.Sleep(bgap);
                        Bitmap f;
                        if (burstRegion)
                        {
                            f = CaptureRegionBitmap(new Rectangle(brx, bry, brw, brh), bw, out _);
                        }
                        else
                        {
                            try { f = NativeInput.CaptureScreenScaled(bw); }
                            catch { f = CaptureScaledBitmap(bw); }
                        }
                        frames.Add(f);
                        stamps.Add(swBurst.ElapsedMilliseconds);
                    }
                    int fwSheet = frames[0].Width;
                    int fh = frames[0].Height;
                    using (var sheet = new Bitmap(fwSheet, fh * bn))
                    using (var g = Graphics.FromImage(sheet))
                    {
                        int fontPx = Math.Max(10, fwSheet / 56);
                        using (var font = new Font("Consolas", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var bgB = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                        using (var fgB = new SolidBrush(Color.White))
                        {
                            for (int i = 0; i < bn; i++)
                            {
                                g.DrawImage(frames[i], 0, i * fh);
                                string t = "#" + (i + 1) + " +" + stamps[i] + "ms";
                                var sz = g.MeasureString(t, font);
                                g.FillRectangle(bgB, 4, i * fh + 4, sz.Width + 6, sz.Height + 3);
                                g.DrawString(t, font, fgB, 7, i * fh + 5);
                            }
                        }
                        var jpeg = EncodeJpeg(sheet, bq);
                        _log($"burst {bn} bingkai ({bw}px, jeda {bgap}ms) diambil" + (burstRegion ? $" (wilayah {brx},{bry} {brw}x{brh})" : ""));
                        return new Response { ContentType = "image/jpeg", BodyBytes = jpeg };
                    }
                }
                finally
                {
                    foreach (var f in frames) f.Dispose();
                }
            }

            if (req.Method == "GET" && req.Path == "/diff")
            {
                _onActivity();
                // Sorotan perubahan layar (v2.6.0): panggilan
                // pertama menyimpan garis dasar; panggilan
                // berikutnya membandingkan per blok 32px dan
                // mengembalikan blok yang berubah (koordinat
                // layar) + persen cakupan. Garis dasar bergeser
                // tiap panggilan (perubahan sejak panggilan
                // terakhir). reset=1 memaksa garis dasar baru.
                int dx = ParseClamp(GetQueryParam(req.Query, "x"), 0, 0, 7680);
                int dy = ParseClamp(GetQueryParam(req.Query, "y"), 0, 0, 4320);
                int dw = ParseClamp(GetQueryParam(req.Query, "w"), 0, 0, 7680);
                int dh = ParseClamp(GetQueryParam(req.Query, "h"), 0, 0, 4320);
                bool reset = GetQueryParam(req.Query, "reset") == "1";
                var scrD = NativeInput.PrimaryBounds();
                var mintaD = (dw > 0 && dh > 0) ? new Rectangle(dx, dy, dw, dh) : scrD;
                lock (_diffLock)
                {
                    using (var cur = CaptureRegionBitmap(mintaD, 0, out var effD))
                    {
                        if (_diffBase == null || _diffRegion != effD || reset)
                        {
                            if (_diffBase != null) _diffBase.Dispose();
                            _diffBase = new Bitmap(cur);
                            _diffRegion = effD;
                            _log($"diff: garis dasar disimpan ({effD.Width}x{effD.Height})");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["baseline"] = true,
                                ["changedBlocks"] = 0,
                                ["coveragePct"] = 0.0,
                                ["bounds"] = null,
                                ["blocks"] = new List<Dictionary<string, object>>(),
                                ["region"] = new Dictionary<string, object>
                                {
                                    ["x"] = effD.X, ["y"] = effD.Y,
                                    ["w"] = effD.Width, ["h"] = effD.Height,
                                },
                            });
                        }
                        var blocks = CompareBlocks(_diffBase, cur, 32, 16, effD.X, effD.Y, out int totalBlocks);
                        _diffBase.Dispose();
                        _diffBase = new Bitmap(cur);
                        double cov = totalBlocks > 0 ? Math.Round(blocks.Count * 100.0 / totalBlocks, 1) : 0.0;
                        Dictionary<string, object> boundsDict = null;
                        if (blocks.Count > 0)
                        {
                            var u = blocks[0];
                            foreach (var b in blocks) u = Rectangle.Union(u, b);
                            boundsDict = new Dictionary<string, object>
                            {
                                ["x"] = u.X, ["y"] = u.Y, ["w"] = u.Width, ["h"] = u.Height,
                            };
                        }
                        var bl = new List<Dictionary<string, object>>();
                        foreach (var b in blocks)
                        {
                            if (bl.Count >= 60) break;
                            bl.Add(new Dictionary<string, object>
                            {
                                ["x"] = b.X, ["y"] = b.Y, ["w"] = b.Width, ["h"] = b.Height,
                            });
                        }
                        _log($"diff: {blocks.Count} blok berubah ({cov}%)");
                        return Response.JsonObject(new Dictionary<string, object>
                        {
                            ["ok"] = true,
                            ["baseline"] = false,
                            ["changedBlocks"] = blocks.Count,
                            ["coveragePct"] = cov,
                            ["bounds"] = boundsDict,
                            ["blocks"] = bl,
                            ["region"] = new Dictionary<string, object>
                            {
                                ["x"] = effD.X, ["y"] = effD.Y,
                                ["w"] = effD.Width, ["h"] = effD.Height,
                            },
                        });
                    }
                }
            }

            if (req.Method == "GET" && req.Path == "/events")
            {
                // Catatan kejadian PC (v2.5.0): apa yang terjadi
                // selagi Muse tidak memanggil — proses mulai /
                // berhenti, jendela buka / tutup, clipboard,
                // menganggur, kunci layar. ?since=<seq> membatasi
                // pada kejadian sesudah nomor urut itu.
                long since = 0;
                long.TryParse(GetQueryParam(req.Query, "since"), out since);
                int emax = ParseClamp(GetQueryParam(req.Query, "max"), 100, 1, 200);
                // Long-poll (v3.9.0 — DSA-07): ?wait=<ms> menahan
                // respons sampai ada kejadian baru (maks 30 dtk).
                int waitMs = ParseClamp(GetQueryParam(req.Query, "wait"), 0, 0, 30000);
                if (waitMs > 0 && EventLog.LastSeq <= since)
                    EventLog.WaitForNew(since, waitMs);
                var evs = EventLog.Since(since, emax);
                var elist = new List<Dictionary<string, object>>();
                foreach (var ev in evs)
                {
                    elist.Add(new Dictionary<string, object>
                    {
                        ["seq"] = ev.Seq,
                        ["time"] = ev.Time,
                        ["kind"] = ev.Kind,
                        ["detail"] = ev.Detail,
                        ["hwnd"] = ev.Hwnd,
                        ["pid"] = ev.Pid,
                    });
                }
                // Bendera terpotong (v3.9.0 — DSA-06): since
                // tertinggal lebih jauh dari isi cincin — kejadian
                // di antaranya hilang dan pemanggil harus tahu.
                long firstSeq = EventLog.FirstSeq;
                bool truncated = since > 0 && firstSeq > 0 && firstSeq > since + 1;
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["events"] = elist,
                    ["lastSeq"] = EventLog.LastSeq,
                    ["truncated"] = truncated,
                });
            }

            if (req.Method == "GET" && req.Path == "/ocr")
            {
                _onActivity();
                // Baca layar sebagai TEKS (v2.4.4): mesin OCR bawaan
                // Windows membaca wilayah layar dan mengembalikan
                // teks + kotak tiap kata. Respons teks ±1-2KB tiba
                // secepat /ping sambil membawa info lebih dari
                // bingkai JPEG puluhan KB — jalur baca tercepat
                // untuk dialog, pesan error, dan log.
                int ox = ParseClamp(GetQueryParam(req.Query, "x"), 0, 0, 7680);
                int oy = ParseClamp(GetQueryParam(req.Query, "y"), 0, 0, 4320);
                int ow = ParseClamp(GetQueryParam(req.Query, "w"), 0, 0, 7680);
                int oh = ParseClamp(GetQueryParam(req.Query, "h"), 0, 0, 4320);
                var scr = NativeInput.PrimaryBounds();
                var minta = (ow > 0 && oh > 0) ? new Rectangle(ox, oy, ow, oh) : scr;
                var wilayah = Rectangle.Intersect(scr, minta);
                if (wilayah.Width <= 0 || wilayah.Height <= 0) wilayah = scr;
                // Baca DIPERKUAT (v2.6.0): ?enhance=1 memperbesar +
                // kontras dulu — teks kecil terbaca lebih sering.
                // enhance=auto (v2.6.2): jalankan KEDUA mode
                // dan pakai hasil yang lebih kaya kata — mode
                // diperkuat terbukti kadang MENANG kadang KALAH
                // dari mode biasa, jadi jangan dipaksa satu.
                string enhParam = GetQueryParam(req.Query, "enhance") ?? "";
                bool enhanceOn = enhParam == "1";
                bool adaptif = enhParam == "auto";
                // Mesin OCR (v2.6.5): ?engine=tesseract memaksa
                // mesin Tesseract (ind+eng); ?engine=auto
                // melombakan WinRT biasa + diperkuat +
                // Tesseract, yang terkaya kata menang.
                string engineParam = GetQueryParam(req.Query, "engine") ?? "";
                bool tessOn = engineParam == "tesseract";
                bool engineAuto = engineParam == "auto";
                // Mesin GPU (v5.4.0): ?engine=gpu membaca lewat
                // EasyOCR di sidecar AI (GPU); gagal -> jatuh ke
                // mesin bawaan secara jujur (mode tetap "normal").
                bool gpuOn = engineParam == "gpu";
                // Bawaan GPU (v5.5.0): tanpa ?engine= (dan tanpa
                // enhance) GPU dicoba PERTAMA; tangga mesin lama
                // menjadi cadangan otomatis.
                bool gpuDefault = engineParam == "" && !enhanceOn && !adaptif;
                OcrReader.Result hasil;
                string modeDipakai = "normal";
                using (var bmp = CaptureRegionBitmap(wilayah, 0, out var eff))
                {
                    wilayah = eff;
                    try
                    {
                        // GPU dulu (v5.5.0): engine=gpu memaksa;
                        // tanpa engine GPU adalah bawaan. Gagal ->
                        // tangga mesin lama di bawah yang menilai.
                        OcrReader.Result gpuHasil = null;
                        if (gpuOn || gpuDefault)
                        {
                            gpuHasil = AiBridge.OcrResult(bmp, eff.X, eff.Y, out var gErr);
                            if (gpuHasil != null) modeDipakai = "gpu";
                            else _log("OCR GPU gagal (" + gErr + ") — lanjut mesin bawaan");
                        }
                        if (gpuHasil != null)
                        {
                            hasil = gpuHasil;
                        }
                        else if (tessOn)
                        {
                            hasil = OcrReader.ReadTesseract(bmp, eff.X, eff.Y);
                            modeDipakai = "tesseract";
                        }
                        else if (engineAuto)
                        {
                            var bA = OcrReader.Read(bmp, eff.X, eff.Y);
                            var kA = OcrReader.ReadEnhanced(bmp, eff.X, eff.Y);
                            var tA = OcrReader.ReadTesseract(bmp, eff.X, eff.Y);
                            int sB = bA.Words.Count * 1000 + (bA.Text != null ? bA.Text.Length : 0);
                            int sK = kA.Words.Count * 1000 + (kA.Text != null ? kA.Text.Length : 0);
                            int sT = tA.Words.Count * 1000 + (tA.Text != null ? tA.Text.Length : 0);
                            if (sT >= sB && sT >= sK) { hasil = tA; modeDipakai = "tesseract"; }
                            else if (sK > sB) { hasil = kA; modeDipakai = "enhanced"; }
                            else { hasil = bA; modeDipakai = "normal"; }
                        }
                        else if (adaptif)
                        {
                            var biasa = OcrReader.Read(bmp, eff.X, eff.Y);
                            var kuat = OcrReader.ReadEnhanced(bmp, eff.X, eff.Y);
                            int skorBiasa = biasa.Words.Count * 1000 + (biasa.Text != null ? biasa.Text.Length : 0);
                            int skorKuat = kuat.Words.Count * 1000 + (kuat.Text != null ? kuat.Text.Length : 0);
                            if (skorKuat > skorBiasa) { hasil = kuat; modeDipakai = "enhanced"; }
                            else { hasil = biasa; modeDipakai = "normal"; }
                        }
                        else if (enhanceOn)
                        {
                            hasil = OcrReader.ReadEnhanced(bmp, eff.X, eff.Y);
                            modeDipakai = "enhanced";
                        }
                        else
                        {
                            hasil = OcrReader.Read(bmp, eff.X, eff.Y);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log("OCR gagal: " + ex.Message);
                        return Response.Json("{\"ok\":false,\"error\":\"" +
                            ex.Message.Replace("\\", " ").Replace("\"", " ") + "\"}", 500);
                    }
                }
                var kata = new List<Dictionary<string, object>>();
                foreach (var wd in hasil.Words)
                {
                    kata.Add(new Dictionary<string, object>
                    {
                        ["t"] = wd.Text,
                        ["x"] = wd.Rect.X,
                        ["y"] = wd.Rect.Y,
                        ["w"] = wd.Rect.Width,
                        ["h"] = wd.Rect.Height,
                    });
                }
                _log($"OCR wilayah {wilayah.X},{wilayah.Y} {wilayah.Width}x{wilayah.Height}: {kata.Count} kata" + (adaptif ? $" (adaptif: {modeDipakai})" : enhanceOn ? " (diperkuat)" : ""));
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["enhanced"] = modeDipakai == "enhanced",
                    ["mode"] = modeDipakai,
                    ["adaptive"] = adaptif,
                    ["tesseract"] = OcrReader.TesseractAvailable,
                    ["text"] = hasil.Text,
                    ["words"] = kata,
                    ["region"] = new Dictionary<string, object>
                    {
                        ["x"] = wilayah.X, ["y"] = wilayah.Y,
                        ["w"] = wilayah.Width, ["h"] = wilayah.Height,
                    },
                });
            }

            if (req.Method == "POST")
            {
                using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.Body) ? "{}" : req.Body))
                {
                    var root = doc.RootElement;
                    switch (req.Path)
                    {
                        case "/detect":
                        {
                            // Deteksi objek YOLO di GPU (v5.4.0):
                            // {x?,y?,w?,h?} wilayah layar (bawaan:
                            // seluruh layar utama) diteruskan ke
                            // sidecar AI visual; koordinat hasil
                            // dikembalikan sebagai koordinat LAYAR.
                            // {imgsz?} (v5.19.0): ukuran inferensi
                            // YOLO, di-clamp 320..1920; tanpa param
                            // perilaku lama (bawaan sidecar 640).
                            _onActivity();
                            int dx = GetInt(root, "x", 0), dy = GetInt(root, "y", 0);
                            int dw = GetInt(root, "w", 0), dh = GetInt(root, "h", 0);
                            int dImg = GetInt(root, "imgsz", 0);
                            if (dImg > 0) dImg = Math.Clamp(dImg, 320, 1920);
                            var scrD = NativeInput.PrimaryBounds();
                            var mintaD = (dw > 0 && dh > 0) ? new Rectangle(dx, dy, dw, dh) : scrD;
                            try
                            {
                                using var bmpD = CaptureRegionBitmap(mintaD, 0, out var effD);
                                var rawD = AiBridge.DetectJson(bmpD, dImg, out var dErr);
                                if (rawD == null)
                                    return Response.Json("{\"ok\":false,\"error\":\"deteksi GPU tidak tersedia: " +
                                        dErr.Replace("\\", " ").Replace("\"", " ") + "\"}");
                                using var docD = JsonDocument.Parse(rawD);
                                var dets = new List<Dictionary<string, object>>();
                                if (docD.RootElement.TryGetProperty("detections", out var dArr))
                                {
                                    foreach (var d in dArr.EnumerateArray())
                                    {
                                        dets.Add(new Dictionary<string, object>
                                        {
                                            ["label"] = d.TryGetProperty("label", out var lb) ? lb.GetString() : "",
                                            ["conf"] = d.TryGetProperty("conf", out var cf) ? cf.GetDouble() : 0,
                                            ["x"] = effD.X + (d.TryGetProperty("x", out var ex2) ? ex2.GetInt32() : 0),
                                            ["y"] = effD.Y + (d.TryGetProperty("y", out var ey2) ? ey2.GetInt32() : 0),
                                            ["w"] = d.TryGetProperty("w", out var ew2) ? ew2.GetInt32() : 0,
                                            ["h"] = d.TryGetProperty("h", out var eh2) ? eh2.GetInt32() : 0,
                                        });
                                    }
                                }
                                long msD = docD.RootElement.TryGetProperty("ms", out var msEl) ? msEl.GetInt64() : -1;
                                long imgD = docD.RootElement.TryGetProperty("imgsz", out var imgEl) ? imgEl.GetInt64() : 0;
                                _log($"Detect GPU wilayah {effD.X},{effD.Y} {effD.Width}x{effD.Height}: {dets.Count} objek");
                                var respD = new Dictionary<string, object>
                                {
                                    ["ok"] = true,
                                    ["engine"] = "yolo-gpu",
                                    ["model"] = AiBridge.ModelName,
                                    ["ms"] = msD,
                                    ["detections"] = dets,
                                    ["region"] = new Dictionary<string, object>
                                    {
                                        ["x"] = effD.X, ["y"] = effD.Y,
                                        ["w"] = effD.Width, ["h"] = effD.Height,
                                    },
                                };
                                if (imgD > 0) respD["imgsz"] = imgD;
                                return Response.JsonObject(respD);
                            }
                            catch (Exception ex)
                            {
                                _log("Detect gagal: " + ex.Message);
                                return Response.Json("{\"ok\":false,\"error\":\"" +
                                    ex.Message.Replace("\\", " ").Replace("\"", " ") + "\"}", 500);
                            }
                        }

                        case "/transcribe":
                        {
                            // Transkrip audio Whisper di GPU
                            // (v5.5.0): {audio: base64 WAV} -> teks
                            // + bahasa terdeteksi. Jembatan perintah
                            // suara bahasa Indonesia (Whisper paham
                            // id; pengenal suara Windows tidak).
                            _onActivity();
                            string aB64 = GetString(root, "audio", "");
                            if (string.IsNullOrWhiteSpace(aB64))
                                return Response.Json("{\"ok\":false,\"error\":\"audio (base64) wajib diisi\"}", 400);
                            byte[] aBytes;
                            try { aBytes = Convert.FromBase64String(aB64); }
                            catch { return Response.Json("{\"ok\":false,\"error\":\"audio bukan base64 sah\"}", 400); }
                            var rawT = AiBridge.TranscribeJson(aBytes, out var tErr);
                            if (rawT == null)
                                return Response.Json("{\"ok\":false,\"error\":\"transkrip GPU tidak tersedia: " +
                                    tErr.Replace("\\", " ").Replace("\"", " ") + "\"}", 503);
                            using var docT = JsonDocument.Parse(rawT);
                            var rt = docT.RootElement;
                            string teksT = rt.TryGetProperty("text", out var txEl) ? txEl.GetString() ?? "" : "";
                            _log($"transkrip GPU ({aBytes.Length} byte): {teksT.Length} char");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["engine"] = "whisper-gpu",
                                ["text"] = teksT,
                                ["language"] = rt.TryGetProperty("language", out var lgEl) ? lgEl.GetString() ?? "" : "",
                                ["ms"] = rt.TryGetProperty("ms", out var msTEl) ? msTEl.GetInt64() : -1,
                            });
                        }

                        case "/routines/save":
                        {
                            // Simpan rutinitas bernama (v2.7.0):
                            // {name, actions:[...]} — actions persis
                            // format /batch, disimpan di PC.
                            string nama = GetString(root, "name", "").Trim();
                            if (!ValidRoutineName(nama))
                                return Response.Json("{\"ok\":false,\"error\":\"nama rutinitas tidak sah (1-40 huruf/angka/spasi/_/-)\"}", 400);
                            if (!root.TryGetProperty("actions", out var acts) ||
                                acts.ValueKind != JsonValueKind.Array ||
                                acts.GetArrayLength() < 1 || acts.GetArrayLength() > 100)
                                return Response.Json("{\"ok\":false,\"error\":\"actions wajib array 1-100 aksi\"}", 400);
                            string saring = ScanRoutineActions(acts);
                            if (saring != null)
                                return Response.Json("{\"ok\":false,\"error\":\"" + saring + "\"}", 400);
                            Routines.Save(nama, acts.GetRawText());
                            // Penjaga idle (v5.13.0): minIdleSec
                            // opsional — rutinitas ditunda selama
                            // user fisik masih aktif.
                            int minIdle = GetInt(root, "minIdleSec", 0);
                            if (minIdle > 0 || root.TryGetProperty("minIdleSec", out _))
                                Routines.SetMinIdleSec(nama, minIdle);
                            _onActivity();
                            _log($"rutinitas '{nama}' disimpan ({acts.GetArrayLength()} aksi)");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["name"] = nama,
                                ["steps"] = acts.GetArrayLength(),
                                ["minIdleSec"] = Routines.GetMinIdleSec(nama),
                            });
                        }
                        case "/routines/delete":
                        {
                            string nama = GetString(root, "name", "").Trim();
                            bool ada = Routines.Delete(nama);
                            _onActivity();
                            _log($"rutinitas '{nama}' dihapus: {ada}");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["deleted"] = ada,
                            });
                        }
                        case "/routines/run":
                        {
                            // Jalankan rutinitas tersimpan lewat mesin
                            // /batch yang sama (satu panggilan jaringan
                            // untuk seluruh rangkaian).
                            string nama = GetString(root, "name", "").Trim();
                            if (!Routines.TryGet(nama, out var actsJson))
                                return Response.Json("{\"ok\":false,\"error\":\"rutinitas tidak ditemukan\"}", 404);
                            // Penjaga idle (v5.13.0 — Arbitrase):
                            // rutinitas ber-minIdleSec DITUNDA
                            // selama sentuhan fisik user masih
                            // lebih baru dari ambangnya — jangan
                            // merebut layar orang yang sedang
                            // memakai PC-nya. Berlaku juga untuk
                            // panggilan dari scheduler & refleks
                            // (semuanya lewat handler ini).
                            int minIdleRun = Routines.GetMinIdleSec(nama);
                            if (minIdleRun > 0)
                            {
                                long agoIdle = PhysicalInput.AgoSec;
                                if (agoIdle >= 0 && agoIdle < minIdleRun)
                                {
                                    _log($"rutinitas '{nama}' DITUNDA: user aktif ({agoIdle} dtk lalu, ambang {minIdleRun} dtk)");
                                    return Response.JsonObject(new Dictionary<string, object>
                                    {
                                        ["ok"] = false,
                                        ["error"] = "ditunda: user sedang aktif memakai PC",
                                        ["code"] = "user_active",
                                        ["retryable"] = true,
                                        ["userIdleSec"] = agoIdle,
                                        ["minIdleSec"] = minIdleRun,
                                    }, 409);
                                }
                            }
                            using (var dokActs = JsonDocument.Parse(actsJson))
                            {
                                string saring = ScanRoutineActions(dokActs.RootElement);
                                if (saring != null)
                                    return Response.Json("{\"ok\":false,\"error\":\"" + saring + "\"}", 400);
                            }
                            bool stopOnError = !root.TryGetProperty("stopOnError", out var soe) ||
                                               soe.ValueKind != JsonValueKind.False;
                            string bodyBatch = "{\"actions\":" + actsJson +
                                               ",\"stopOnError\":" + (stopOnError ? "true" : "false") + "}";
                            _onActivity();
                            _log($"rutinitas '{nama}' dijalankan");
                            var respBatch = Route(new Request
                            {
                                Method = "POST", Path = "/batch", Query = "", Body = bodyBatch,
                            });
                            try
                            {
                                using (var dok = JsonDocument.Parse(Encoding.UTF8.GetString(respBatch.BodyBytes)))
                                {
                                    if (dok.RootElement.ValueKind == JsonValueKind.Object)
                                    {
                                        var gab = new Dictionary<string, object>();
                                        foreach (var p in dok.RootElement.EnumerateObject())
                                            gab[p.Name] = p.Value.Clone();
                                        gab["routine"] = nama;
                                        return Response.JsonObject(gab);
                                    }
                                }
                            }
                            catch { /* respons batch bukan JSON — teruskan apa adanya */ }
                            return respBatch;
                        }
                        case "/verify":
                        {
                            // Verification Engine mandiri (v5.6.0):
                            // buktikan satu kondisi desktop sekarang
                            // juga — {kind, text/process/path/value,
                            // timeoutMs} atau dibungkus {"verify":{…}}.
                            // Mesin yang sama dengan field "verify"
                            // per aksi di /batch.
                            JsonElement vRoot = root;
                            if (root.TryGetProperty("verify", out var vWrap) &&
                                vWrap.ValueKind == JsonValueKind.Object) vRoot = vWrap;
                            var swV = Stopwatch.StartNew();
                            bool met = VerifyPoll(vRoot, null, out string det);
                            _log($"verify {GetString(vRoot, "kind", "text")}: {(met ? "TERPENUHI" : "tidak terbukti")} ({swV.ElapsedMilliseconds}ms)");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["met"] = met,
                                ["kind"] = GetString(vRoot, "kind", "text"),
                                ["detail"] = det ?? "",
                                ["waitedMs"] = (int)swV.ElapsedMilliseconds,
                            });
                        }
                        case "/schedules/save":
                        {
                            // Simpan jadwal (v5.6.0 — Scheduler
                            // Engine): {name, routine, kind:
                            // daily|weekly|interval|once, at, day,
                            // minutes, enabled}. Tersimpan di SQLite
                            // (Schedules); slot berikutnya dihitung
                            // sekarang dan dikembalikan.
                            string nama = GetString(root, "name", "").Trim();
                            if (!ValidRoutineName(nama))
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false,
                                    ["error"] = "nama jadwal tidak sah (1-40 huruf/angka/spasi/_/-)",
                                }, 400);
                            string routine = GetString(root, "routine", "").Trim();
                            if (!Routines.TryGet(routine, out _))
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false,
                                    ["error"] = "rutinitas '" + routine + "' tidak ditemukan",
                                }, 400);
                            string kind = GetString(root, "kind", "").Trim().ToLowerInvariant();
                            string at = GetString(root, "at", "").Trim();
                            int day = GetInt(root, "day", -1);
                            int minutes = GetInt(root, "minutes", 0);
                            string errPola = null;
                            switch (kind)
                            {
                                case "daily":
                                    if (!Schedules.TryHm(at, out var hmD)) errPola = "at wajib format HH:mm";
                                    else at = hmD.Hours.ToString("D2") + ":" + hmD.Minutes.ToString("D2");
                                    break;
                                case "weekly":
                                    if (!Schedules.TryHm(at, out var hmW)) errPola = "at wajib format HH:mm";
                                    else at = hmW.Hours.ToString("D2") + ":" + hmW.Minutes.ToString("D2");
                                    if (day < 0 || day > 6) errPola = "day wajib 0-6 (0=Minggu)";
                                    break;
                                case "interval":
                                    if (minutes < 1 || minutes > 10080) errPola = "minutes wajib 1-10080";
                                    break;
                                case "once":
                                    if (!DateTime.TryParse(at, out var tOnce)) errPola = "at wajib tanggal-waktu ISO";
                                    else if (tOnce <= DateTime.Now) errPola = "jadwal sekali di masa lalu tidak bisa disimpan";
                                    else at = Schedules.Iso(tOnce);
                                    break;
                                default:
                                    errPola = "kind wajib daily|weekly|interval|once";
                                    break;
                            }
                            if (errPola != null)
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false, ["error"] = errPola,
                                }, 400);
                            var lama = Schedules.Get(nama);
                            var baris = new Schedules.Row
                            {
                                Name = nama,
                                Routine = routine,
                                Kind = kind,
                                At = at,
                                Day = day,
                                Minutes = minutes,
                                Enabled = GetBool(root, "enabled", lama == null || lama.Enabled),
                                LastRun = lama != null ? lama.LastRun : "",
                                LastStatus = lama != null ? lama.LastStatus : "",
                                NextRun = Schedules.ComputeNext(kind, at, day, minutes, DateTime.Now),
                            };
                            Schedules.Save(baris);
                            _onActivity();
                            _log($"jadwal '{nama}' disimpan ({kind} → rutinitas '{routine}', berikutnya {baris.NextRun})");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["name"] = nama,
                                ["nextRun"] = baris.NextRun,
                            });
                        }
                        case "/schedules/delete":
                        {
                            string nama = GetString(root, "name", "").Trim();
                            bool ada = Schedules.Delete(nama);
                            _onActivity();
                            _log($"jadwal '{nama}' dihapus: {ada}");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["deleted"] = ada,
                            });
                        }
                        case "/schedules/toggle":
                        {
                            string nama = GetString(root, "name", "").Trim();
                            bool enabled = GetBool(root, "enabled", true);
                            bool ada = Schedules.SetEnabled(nama, enabled);
                            _onActivity();
                            _log($"jadwal '{nama}' {(enabled ? "diaktifkan" : "dimatikan")}: {ada}");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["found"] = ada,
                                ["enabled"] = enabled,
                            });
                        }
                        case "/media/probe":
                        {
                            // Probe metadata berkas media (v5.6.0).
                            string path = GetString(root, "path", "").Trim();
                            if (path.Length == 0 || !File.Exists(path))
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false, ["error"] = "berkas tidak ditemukan: " + path,
                                }, 400);
                            var hasil = MediaTools.Probe(path);
                            return Response.JsonObject(hasil,
                                hasil.TryGetValue("ok", out var okP) && okP is bool bP && bP ? 200 : 400);
                        }
                        case "/media/thumb":
                        {
                            // Thumbnail satu bingkai (v5.6.0):
                            // {path, atSec, out}.
                            string path = GetString(root, "path", "").Trim();
                            string outP = GetString(root, "out", "").Trim();
                            if (path.Length == 0 || !File.Exists(path) || outP.Length == 0)
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false, ["error"] = "path/out wajib dan berkas masukan harus ada",
                                }, 400);
                            int toMs = GetInt(root, "timeoutMs", 120000);
                            if (toMs < 5000) toMs = 5000;
                            if (toMs > 300000) toMs = 300000;
                            var hasil = MediaTools.Thumb(path, GetDouble(root, "atSec", 1), outP, toMs);
                            return Response.JsonObject(hasil,
                                hasil.TryGetValue("ok", out var okT) && okT is bool bT && bT ? 200 : 400);
                        }
                        case "/media/trim":
                        {
                            // Potong berkas media (v5.6.0): {path,
                            // start, duration|end, out, copy}.
                            string path = GetString(root, "path", "").Trim();
                            string outP = GetString(root, "out", "").Trim();
                            double start = GetDouble(root, "start", 0);
                            double dur = GetDouble(root, "duration", 0);
                            if (dur <= 0)
                            {
                                double end = GetDouble(root, "end", 0);
                                if (end > start) dur = end - start;
                            }
                            if (path.Length == 0 || !File.Exists(path) || outP.Length == 0 || dur <= 0)
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false, ["error"] = "path/out wajib, berkas harus ada, duration (atau end) harus > start",
                                }, 400);
                            int toMs = GetInt(root, "timeoutMs", 120000);
                            if (toMs < 5000) toMs = 5000;
                            if (toMs > 300000) toMs = 300000;
                            var hasil = MediaTools.Trim(path, start, dur, outP,
                                GetBool(root, "copy", false), toMs);
                            return Response.JsonObject(hasil,
                                hasil.TryGetValue("ok", out var okTr) && okTr is bool bTr && bTr ? 200 : 400);
                        }
                        case "/media/convert":
                        {
                            // Konversi berkas media (v5.6.0):
                            // {path, out} — codec menurut ekstensi
                            // tujuan (.wav = PCM siap Whisper).
                            string path = GetString(root, "path", "").Trim();
                            string outP = GetString(root, "out", "").Trim();
                            if (path.Length == 0 || !File.Exists(path) || outP.Length == 0)
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false, ["error"] = "path/out wajib dan berkas masukan harus ada",
                                }, 400);
                            int toMs = GetInt(root, "timeoutMs", 120000);
                            if (toMs < 5000) toMs = 5000;
                            if (toMs > 300000) toMs = 300000;
                            var hasil = MediaTools.Convert(path, outP, toMs);
                            return Response.JsonObject(hasil,
                                hasil.TryGetValue("ok", out var okC) && okC is bool bC && bC ? 200 : 400);
                        }
                        case "/batch":
                        {
                            // Rangkaian aksi dalam SATU panggilan
                            // (v2.4.1 — "mode ngebut"): Muse mengirim
                            // daftar aksi dan semuanya dijalankan
                            // berurutan di sisi PC, tanpa jeda
                            // bolak-balik jaringan di antara langkah.
                            // Tiap aksi {"do":"click","x":..,...} —
                            // "do" = nama endpoint tanpa garis miring
                            // (move/click/type/key/keydown/keyup/
                            // scroll/drag/focus/window/uia_click/
                            // clipboard/paste/open/exec/shell/wait/ocr)
                            // atau {"do":"sleep","ms":300} untuk jeda.
                            // "ocr" membaca teks wilayah layar
                            // (x/y/w/h) di tengah rangkaian (v2.5.5).
                            // Hasil tiap aksi dikembalikan sebagai
                            // daftar; berhenti pada galat pertama
                            // kecuali "stopOnError":false.
                            if (!root.TryGetProperty("actions", out var actions) ||
                                actions.ValueKind != JsonValueKind.Array)
                                return Response.Json("{\"ok\":false,\"error\":\"actions wajib berupa array\"}", 400);
                            // InputArbiter (v3.11.0 — DSA-15): sewa
                            // input selama rangkaian berjalan; batch
                            // lain & input asing ditolak 429
                            // input_leased sampai rangkaian selesai.
                            string leaseOwner = _opTaskTls;
                            if (string.IsNullOrEmpty(leaseOwner))
                            {
                                leaseOwner = "batch-" + Interlocked.Increment(ref _leaseSeq);
                                _opTaskTls = leaseOwner; // sub-aksi mewarisi pemilik
                            }
                            if (!TryAcquireLease(leaseOwner))
                            {
                                string pemilikAktif = ActiveLeaseOwner();
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false,
                                    ["error"] = "input sedang disewa rangkaian batch lain (" + pemilikAktif + ")",
                                    ["code"] = "input_leased",
                                    ["retryable"] = true,
                                    ["leaseOwner"] = pemilikAktif,
                                }, 429);
                            }
                            bool stopOnError = !root.TryGetProperty("stopOnError", out var soe) ||
                                               soe.ValueKind != JsonValueKind.False;
                            // diffAfter (v2.6.1): garis dasar
                            // diambil SEBELUM aksi jalan; sesudah
                            // rangkaian selesai, perubahan layar
                            // dihitung dan ditempel ke respons —
                            // lingkaran klik->cek jadi satu
                            // panggilan (pasangan /diff).
                            bool diffAfter = root.TryGetProperty("diffAfter", out var daf) &&
                                             daf.ValueKind == JsonValueKind.True;
                            Bitmap diffBefore = null;
                            Rectangle diffRegion = Rectangle.Empty;
                            if (diffAfter)
                            {
                                var scrDiff = NativeInput.PrimaryBounds();
                                int dX = GetInt(root, "diffX", 0);
                                int dY = GetInt(root, "diffY", 0);
                                int dW = GetInt(root, "diffW", 0);
                                int dH = GetInt(root, "diffH", 0);
                                var mintaDiff = (dW > 0 && dH > 0)
                                    ? new Rectangle(dX, dY, dW, dH) : scrDiff;
                                diffBefore = CaptureRegionBitmap(mintaDiff, 0, out diffRegion);
                            }
                            // Arbitrase manusia (v5.9.0): bidang
                            // opsional "yieldToUserMs" — bila user
                            // BARU SAJA memakai mouse/keyboard
                            // (masukan terakhir <1 dtk), rangkaian
                            // MENUNDA sampai layar tenang atau
                            // batas tercapai, alih-alih rebutan
                            // kursor. Tanpa bidang ini, perilaku
                            // lama persis. Hasilnya dilaporkan di
                            // respons (userYielded dsb.).
                            int yieldCap = GetInt(root, "yieldToUserMs", 0);
                            if (yieldCap < 0) yieldCap = 0;
                            if (yieldCap > 10000) yieldCap = 10000;
                            bool userYielded = false;
                            int userYieldWaitMs = 0;
                            bool userStillActive = false;
                            if (yieldCap > 0 && NativeInput.IdleMilliseconds() < 1000)
                            {
                                var swYield = System.Diagnostics.Stopwatch.StartNew();
                                while (NativeInput.IdleMilliseconds() < 1000
                                       && swYield.ElapsedMilliseconds < yieldCap)
                                    Thread.Sleep(100);
                                userYielded = true;
                                userYieldWaitMs = (int)swYield.ElapsedMilliseconds;
                                userStillActive = NativeInput.IdleMilliseconds() < 1000;
                                _log($"batch menunda demi user: {userYieldWaitMs}ms"
                                    + (userStillActive ? " (user masih aktif — lanjut sesudah batas)" : ""));
                            }
                            var results = new List<object>();
                            int doneCount = 0;
                            foreach (var action in actions.EnumerateArray())
                            {
                                if (doneCount >= 100)
                                {
                                    results.Add(new Dictionary<string, object>
                                    {
                                        ["do"] = "(dipotong)",
                                        ["ok"] = false,
                                        ["error"] = "maksimum 100 aksi per batch",
                                    });
                                    break;
                                }
                                TouchLease(leaseOwner);
                                string doName = action.ValueKind == JsonValueKind.Object
                                    ? GetString(action, "do", "") : "";
                                if (doName == "sleep")
                                {
                                    int ms = GetInt(action, "ms", 0);
                                    if (ms < 0) ms = 0;
                                    if (ms > 10000) ms = 10000;
                                    Thread.Sleep(ms);
                                    results.Add(new Dictionary<string, object>
                                    {
                                        ["do"] = "sleep", ["ok"] = true, ["ms"] = ms,
                                    });
                                    doneCount++;
                                    continue;
                                }
                                if (doName == "" || doName == "batch")
                                {
                                    results.Add(new Dictionary<string, object>
                                    {
                                        ["do"] = doName, ["ok"] = false,
                                        ["error"] = "aksi tidak dikenal / batch bersarang dilarang",
                                    });
                                    if (stopOnError) break;
                                    continue;
                                }
                                // Verification + Recovery (v5.6.0):
                                // field opsional per aksi — "verify"
                                // (kondisi yang harus terbukti sesudah
                                // aksi jalan), "retry" {times, delayMs}
                                // (ulang aksi+verifikasi dengan jeda
                                // membesar), "fallback" (aksi cadangan
                                // bila percobaan terakhir tetap gagal).
                                JsonElement verifyEl = default;
                                bool hasVerify = action.TryGetProperty("verify", out verifyEl) &&
                                                   verifyEl.ValueKind == JsonValueKind.Object;
                                int retryTimes = 0;
                                int retryDelayMs = 500;
                                if (action.TryGetProperty("retry", out var retryEl) &&
                                    retryEl.ValueKind == JsonValueKind.Object)
                                {
                                    retryTimes = GetInt(retryEl, "times", 0);
                                    if (retryTimes < 0) retryTimes = 0;
                                    if (retryTimes > 5) retryTimes = 5;
                                    retryDelayMs = GetInt(retryEl, "delayMs", 500);
                                    if (retryDelayMs < 0) retryDelayMs = 0;
                                    if (retryDelayMs > 10000) retryDelayMs = 10000;
                                }
                                JsonElement fallbackEl = default;
                                bool hasFallback = action.TryGetProperty("fallback", out fallbackEl) &&
                                                     fallbackEl.ValueKind == JsonValueKind.Object;
                                int attempts = 0;
                                bool okFinal = false;
                                bool recovered = false;
                                bool? verified = null;
                                string verifyDetail = null;
                                int lastStatus = 0;
                                object lastParsed = null;
                                while (true)
                                {
                                    attempts++;
                                // "uia_click" diterima sebagai alias /uia/click.
                                string subPath = "/" + doName.Replace('_', '/');
                                var subReq = new Request
                                {
                                    Method = "POST",
                                    Path = subPath,
                                    Query = "",
                                    Body = action.GetRawText(),
                                };
                                // Aksi "ocr" (v2.5.5): /ocr aslinya
                                // endpoint GET berparameter query —
                                // teruskan sebagai sub-permintaan GET
                                // dengan query yang disusun dari field
                                // aksi, supaya baca-teks bisa ikut di
                                // tengah rangkaian batch (satu
                                // panggilan untuk pola tunggu-klik-baca).
                                if (doName == "ocr")
                                {
                                    subReq.Method = "GET";
                                    subReq.Query = "x=" + GetInt(action, "x", 0) +
                                                   "&y=" + GetInt(action, "y", 0) +
                                                   "&w=" + GetInt(action, "w", 0) +
                                                   "&h=" + GetInt(action, "h", 0);
                                    subReq.Body = "";
                                }
                                Response subResp;
                                try { subResp = Route(subReq); }
                                catch (Exception ex)
                                {
                                    string msg = ex.Message.Replace("\\", "\\\\").Replace("\"", "\\\"");
                                    subResp = Response.Json(
                                        "{\"ok\":false,\"error\":\"" + msg + "\"}", 500);
                                }
                                object parsed;
                                string rawText = Encoding.UTF8.GetString(subResp.BodyBytes);
                                try
                                {
                                    using (var rd = JsonDocument.Parse(rawText))
                                        parsed = rd.RootElement.Clone();
                                }
                                catch { parsed = rawText; }
                                lastStatus = subResp.Status;
                                lastParsed = parsed;
                                bool stepOk = subResp.Status < 400;
                                if (hasVerify)
                                {
                                    bool metV = VerifyPoll(verifyEl, () => TouchLease(leaseOwner), out string detV);
                                    verified = metV;
                                    verifyDetail = detV;
                                    stepOk = stepOk && metV;
                                }
                                if (stepOk) { okFinal = true; break; }
                                if (attempts > retryTimes) break;
                                if (retryDelayMs > 0) Thread.Sleep(retryDelayMs);
                                retryDelayMs = Math.Min(retryDelayMs * 2, 10000);
                                TouchLease(leaseOwner);
                                }
                                // Recovery: aksi cadangan dicoba sekali
                                // bila aksi utama tetap gagal sesudah
                                // semua percobaan ulang (BUKAN undo —
                                // hanya jalan pintas pemulihan).
                                if (!okFinal && hasFallback)
                                {
                                    string fbName = GetString(fallbackEl, "do", "");
                                    if (fbName != "" && fbName != "batch" && fbName != "sleep")
                                    {
                                        var fb = RunBatchAction(fallbackEl, fbName);
                                        bool fbOk = fb.Status < 400;
                                        if (fbOk && fallbackEl.TryGetProperty("verify", out var fbVerify) &&
                                            fbVerify.ValueKind == JsonValueKind.Object)
                                        {
                                            bool metF = VerifyPoll(fbVerify, () => TouchLease(leaseOwner), out string detF);
                                            verified = metF;
                                            verifyDetail = detF;
                                            fbOk = metF;
                                        }
                                        if (fbOk)
                                        {
                                            recovered = true;
                                            okFinal = true;
                                            lastStatus = fb.Status;
                                            lastParsed = fb.Parsed;
                                        }
                                    }
                                }
                                var hasilAksi = new Dictionary<string, object>
                                {
                                    ["do"] = doName,
                                    ["status"] = lastStatus,
                                    ["result"] = lastParsed,
                                };
                                if (retryTimes > 0) hasilAksi["attempts"] = attempts;
                                if (hasVerify)
                                {
                                    hasilAksi["verified"] = verified ?? false;
                                    if (verifyDetail != null) hasilAksi["verifyDetail"] = verifyDetail;
                                }
                                if (recovered) hasilAksi["recovered"] = true;
                                results.Add(hasilAksi);
                                doneCount++;
                                if (!okFinal && stopOnError) break;
                            }
                            _onActivity();
                            _log($"batch: {doneCount} aksi dijalankan");
                            var batchResp = new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["count"] = doneCount,
                                ["results"] = results,
                            };
                            if (yieldCap > 0)
                            {
                                batchResp["userYielded"] = userYielded;
                                batchResp["userYieldWaitMs"] = userYieldWaitMs;
                                batchResp["userStillActive"] = userStillActive;
                            }
                            // shotAfter (v2.4.2, usulan performa A):
                            // respons ditutup dengan bingkai JPEG
                            // keadaan layar TERKINI — Muse langsung
                            // melihat hasil rangkaiannya dalam respons
                            // yang sama, tanpa panggilan motret
                            // terpisah. Bingkai dikirim base64.
                            if (root.TryGetProperty("shotAfter", out var saf) &&
                                saf.ValueKind == JsonValueKind.True)
                            {
                                int shotW = GetInt(root, "shotWidth", 1280);
                                if (shotW < 320) shotW = 320;
                                if (shotW > 1920) shotW = 1920;
                                int shotQ = GetInt(root, "shotQuality", 70);
                                if (shotQ < 30) shotQ = 30;
                                if (shotQ > 95) shotQ = 95;
                                byte[] jpeg;
                                int iw, ih;
                                using (var bmp = CaptureScaledBitmap(shotW))
                                {
                                    iw = bmp.Width; ih = bmp.Height;
                                    jpeg = EncodeJpeg(bmp, shotQ);
                                }
                                batchResp["frame"] = new Dictionary<string, object>
                                {
                                    ["mime"] = "image/jpeg",
                                    ["width"] = iw,
                                    ["height"] = ih,
                                    ["b64"] = Convert.ToBase64String(jpeg),
                                };
                            }
                            // ocrAfter (v2.5.6, pasangan shotAfter):
                            // respons juga ditutup dengan TEKS hasil
                            // OCR wilayah layar — pola "lakukan lalu
                            // baca hasilnya" cukup satu panggilan.
                            // Wilayah dari ocrX/ocrY/ocrW/ocrH
                            // (bawaan 0 = layar penuh); dijalankan
                            // lewat handler GET /ocr yang sama via
                            // sub-permintaan internal.
                            if (root.TryGetProperty("ocrAfter", out var oaf) &&
                                oaf.ValueKind == JsonValueKind.True)
                            {
                                var ocrReq = new Request
                                {
                                    Method = "GET",
                                    Path = "/ocr",
                                    Query = "x=" + GetInt(root, "ocrX", 0) +
                                            "&y=" + GetInt(root, "ocrY", 0) +
                                            "&w=" + GetInt(root, "ocrW", 0) +
                                            "&h=" + GetInt(root, "ocrH", 0),
                                    Body = "",
                                };
                                try
                                {
                                    var ocrResp = Route(ocrReq);
                                    string ocrRaw = Encoding.UTF8.GetString(ocrResp.BodyBytes);
                                    using (var rd = JsonDocument.Parse(ocrRaw))
                                        batchResp["ocr"] = rd.RootElement.Clone();
                                }
                                catch (Exception ex)
                                {
                                    batchResp["ocr"] = new Dictionary<string, object>
                                    {
                                        ["ok"] = false, ["error"] = ex.Message,
                                    };
                                }
                            }
                            if (diffAfter && diffBefore != null)
                            {
                                try
                                {
                                    using (var curDiff = CaptureRegionBitmap(diffRegion, 0, out var effDiff))
                                    {
                                        var dBlocks = CompareBlocks(diffBefore, curDiff, 32, 16, effDiff.X, effDiff.Y, out int dTotal);
                                        double dCov = dTotal > 0 ? Math.Round(dBlocks.Count * 100.0 / dTotal, 1) : 0.0;
                                        Dictionary<string, object> dBounds = null;
                                        if (dBlocks.Count > 0)
                                        {
                                            var u = dBlocks[0];
                                            foreach (var b in dBlocks) u = Rectangle.Union(u, b);
                                            dBounds = new Dictionary<string, object>
                                            {
                                                ["x"] = u.X, ["y"] = u.Y, ["w"] = u.Width, ["h"] = u.Height,
                                            };
                                        }
                                        var dList = new List<Dictionary<string, object>>();
                                        foreach (var b in dBlocks)
                                        {
                                            if (dList.Count >= 60) break;
                                            dList.Add(new Dictionary<string, object>
                                            {
                                                ["x"] = b.X, ["y"] = b.Y, ["w"] = b.Width, ["h"] = b.Height,
                                            });
                                        }
                                        batchResp["diff"] = new Dictionary<string, object>
                                        {
                                            ["changedBlocks"] = dBlocks.Count,
                                            ["coveragePct"] = dCov,
                                            ["bounds"] = dBounds,
                                            ["blocks"] = dList,
                                        };
                                    }
                                }
                                catch (Exception ex)
                                {
                                    batchResp["diff"] = new Dictionary<string, object>
                                    {
                                        ["ok"] = false, ["error"] = ex.Message,
                                    };
                                }
                                finally
                                {
                                    diffBefore.Dispose();
                                }
                            }
                            ReleaseLease(leaseOwner);
                            return Response.JsonObject(batchResp);
                        }
                        case "/move":
                        {
                            // v5.10.0: relatif {dx, dy} dari posisi
                            // kursor sekarang, dan glideMs (gerak
                            // meluncur menyusuri jalur, bukan
                            // teleport) untuk aplikasi/game yang
                            // tidak mengakui teleport sebagai
                            // gerakan. Tanpa keduanya: lama persis.
                            int glideMs = GetInt(root, "glideMs", 0);
                            if (glideMs < 0) glideMs = 0;
                            if (glideMs > 10000) glideMs = 10000;
                            bool relatif = HasInt(root, "dx") || HasInt(root, "dy");
                            if (!relatif && (!HasInt(root, "x") || !HasInt(root, "y"))) return MissingField("x/y");
                            int x = GetInt(root, "x", 0), y = GetInt(root, "y", 0);
                            int dx = GetInt(root, "dx", 0), dy = GetInt(root, "dy", 0);
                            int fx = 0, fy = 0;
                            OnUi(() =>
                            {
                                int tx = x, ty = y;
                                if (relatif)
                                {
                                    var cur = NativeInput.CursorPosition();
                                    tx = cur.X + dx; ty = cur.Y + dy;
                                }
                                if (glideMs > 0) NativeInput.GlideTo(tx, ty, glideMs);
                                else NativeInput.Move(tx, ty);
                                fx = tx; fy = ty;
                                return true;
                            });
                            _onActivity();
                            _log($"geser mouse ke {fx},{fy}" + (glideMs > 0 ? $" (meluncur {glideMs}ms)" : "") + (relatif ? " (relatif)" : ""));
                            return Ok();
                        }
                        case "/click":
                        {
                            if (!HasInt(root, "x") || !HasInt(root, "y")) return MissingField("x/y");
                            int x = GetInt(root, "x", 0), y = GetInt(root, "y", 0);
                            string button = GetString(root, "button", "left");
                            // Koordinat relatif jendela (v2.6.3):
                            // {relativeTo:"potongan judul"} — x/y
                            // diartikan dari sudut kiri-atas jendela
                            // itu lalu diubah ke absolut di sini.
                            // Jendela tidak ketemu -> 404.
                            string relTo = GetString(root, "relativeTo", "");
                            string relTitle = null;
                            if (!string.IsNullOrWhiteSpace(relTo))
                            {
                                var win = FindWindowByTitle(relTo);
                                if (win == null)
                                    return Response.Json("{\"ok\":false,\"error\":\"jendela tidak ditemukan: " + relTo.Replace("\\", " ").Replace("\"", " ") + "\"}", 404);
                                relTitle = win.Title;
                                x += win.Rect.X;
                                y += win.Rect.Y;
                            }
                            // count (v2.4.0): 2 = klik ganda, 3 = klik tiga.
                            int count = GetInt(root, "count", 1);
                            if (count < 1) count = 1;
                            if (count > 3) count = 3;
                            int n = count;
                            // holdMs (v5.9.0): tekan-tahan — tombol
                            // ditahan selama itu sebelum dilepas,
                            // dalam SATU aksi UI lokal (long-press
                            // yang timing-nya rapat). Berlaku bila
                            // > 0; count diabaikan saat menahan.
                            int hold = GetInt(root, "holdMs", 0);
                            if (hold < 0) hold = 0;
                            if (hold > 10000) hold = 10000;
                            // modifiers (gelombang 2): "ctrl,shift,alt,win"
                            // ditahan selama klik berlangsung.
                            var mods = ParseModifiers(GetString(root, "modifiers", ""));
                            OnUi(() =>
                            {
                                foreach (var vk in mods) NativeInput.KeyDown(vk);
                                try
                                {
                                    if (hold > 0) NativeInput.ClickHold(x, y, button, hold);
                                    else NativeInput.ClickTimes(x, y, button, n);
                                }
                                finally { for (int i = mods.Count - 1; i >= 0; i--) NativeInput.KeyUp(mods[i]); }
                                return true;
                            });
                            _onActivity();
                            _lastClickX = x; _lastClickY = y; _lastClickTick = Environment.TickCount64;
                            _log($"klik {x},{y} ({button}{(hold > 0 ? ", tahan " + hold + "ms" : (n > 1 ? ", " + n + "x" : ""))}{(mods.Count > 0 ? ", +penahan" : "")})" + (relTitle != null ? $" (relatif: {relTitle})" : ""));
                            // Klik verifikasi (v2.6.4): {expectText}
                            // -> sesudah klik, server menunggu teks
                            // itu muncul di sekitar titik klik (OCR
                            // wilayah radius, polling 250ms) lalu
                            // melapor verified:true/false dalam
                            // respons yang sama. Satu panggilan
                            // menggantikan klik->tunggu->cari.
                            string expect = GetString(root, "expectText", "");
                            if (!string.IsNullOrWhiteSpace(expect))
                            {
                                int expTimeout = GetInt(root, "expectTimeoutMs", 3000);
                                if (expTimeout < 250) expTimeout = 250;
                                if (expTimeout > 10000) expTimeout = 10000;
                                int expRadius = GetInt(root, "expectRadius", 400);
                                if (expRadius < 100) expRadius = 100;
                                if (expRadius > 1000) expRadius = 1000;
                                var wilE = new Rectangle(x - expRadius, y - expRadius, expRadius * 2, expRadius * 2);
                                bool verified = false;
                                var swExp = System.Diagnostics.Stopwatch.StartNew();
                                while (swExp.ElapsedMilliseconds < expTimeout)
                                {
                                    try
                                    {
                                        using (var bmp = CaptureRegionBitmap(wilE, 0, out var effE))
                                        {
                                            var rr = OcrReader.Read(bmp, effE.X, effE.Y);
                                            if (rr.Text != null && rr.Text.IndexOf(expect, StringComparison.OrdinalIgnoreCase) >= 0)
                                            { verified = true; break; }
                                        }
                                    }
                                    catch { /* abaikan, coba lagi */ }
                                    System.Threading.Thread.Sleep(250);
                                }
                                _log($"klik verifikasi '{expect}': {(verified ? "KETEMU" : "tidak ketemu")} ({swExp.ElapsedMilliseconds}ms)");
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = true,
                                    ["verified"] = verified,
                                    ["expectText"] = expect,
                                    ["waitedMs"] = (int)swExp.ElapsedMilliseconds,
                                });
                            }
                            return Ok();
                        }
                        case "/clickwhen":
                        {
                            // Klik-tunggu (v2.6.5): tunggu teks
                            // muncul di layar (UIA jendela depan
                            // + OCR wilayah adaptif), klik
                            // tengahnya, lalu opsional verifikasi
                            // teks lain muncul — SATU panggilan
                            // menggantikan cari->klik->cek.
                            string cari = GetString(root, "text", "");
                            if (string.IsNullOrWhiteSpace(cari))
                                return Response.Json("{\"ok\":false,\"error\":\"field wajib hilang: text\"}", 400);
                            int cwTimeout = GetInt(root, "timeoutMs", 15000);
                            if (cwTimeout < 1000) cwTimeout = 1000;
                            if (cwTimeout > 60000) cwTimeout = 60000;
                            var scrW = NativeInput.PrimaryBounds();
                            var wilW = scrW;
                            if (HasInt(root, "x") && HasInt(root, "y") && HasInt(root, "w") && HasInt(root, "h"))
                            {
                                var mw = new Rectangle(GetInt(root, "x", 0), GetInt(root, "y", 0), GetInt(root, "w", 0), GetInt(root, "h", 0));
                                var potong = Rectangle.Intersect(scrW, mw);
                                if (potong.Width > 0 && potong.Height > 0) wilW = potong;
                            }
                            var swW = System.Diagnostics.Stopwatch.StartNew();
                            int ketemuX = -1, ketemuY = -1;
                            while (swW.ElapsedMilliseconds < cwTimeout && ketemuX < 0)
                            {
                                try
                                {
                                    var itemsW = OnUi(() => UiaScan.ScanForeground(300));
                                    foreach (var it in itemsW)
                                    {
                                        if (!string.IsNullOrEmpty(it.Name) && it.Name.IndexOf(cari, StringComparison.OrdinalIgnoreCase) >= 0
                                            && wilW.Contains(it.CenterX, it.CenterY))
                                        { ketemuX = it.CenterX; ketemuY = it.CenterY; break; }
                                    }
                                }
                                catch { /* abaikan, lanjut OCR */ }
                                if (ketemuX < 0)
                                {
                                    try
                                    {
                                        using (var bmp = CaptureRegionBitmap(wilW, 0, out var effW))
                                        {
                                            var rr = OcrAdaptivePick(bmp, effW.X, effW.Y, out _);
                                            var gab = CariTeksDiKata(rr.Words, cari);
                                            if (gab != null) { ketemuX = gab.Value.X; ketemuY = gab.Value.Y; }
                                        }
                                    }
                                    catch { /* abaikan, polling lagi */ }
                                }
                                if (ketemuX < 0) System.Threading.Thread.Sleep(300);
                            }
                            if (ketemuX < 0)
                            {
                                _log($"clickwhen '{cari}': TIDAK ketemu ({swW.ElapsedMilliseconds}ms)");
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = true, ["found"] = false,
                                    ["waitedMs"] = (int)swW.ElapsedMilliseconds,
                                });
                            }
                            OnUi(() => { NativeInput.ClickTimes(ketemuX, ketemuY, "left", 1); return true; });
                            _onActivity();
                            _lastClickX = ketemuX; _lastClickY = ketemuY; _lastClickTick = Environment.TickCount64;
                            _log($"clickwhen '{cari}': ketemu, klik {ketemuX},{ketemuY} ({swW.ElapsedMilliseconds}ms)");
                            var respW = new Dictionary<string, object>
                            {
                                ["ok"] = true, ["found"] = true,
                                ["x"] = ketemuX, ["y"] = ketemuY,
                                ["waitedMs"] = (int)swW.ElapsedMilliseconds,
                            };
                            string expectW = GetString(root, "expectText", "");
                            if (!string.IsNullOrWhiteSpace(expectW))
                            {
                                int expT = GetInt(root, "expectTimeoutMs", 3000);
                                if (expT < 250) expT = 250;
                                if (expT > 10000) expT = 10000;
                                int expR = GetInt(root, "expectRadius", 400);
                                if (expR < 100) expR = 100;
                                if (expR > 1000) expR = 1000;
                                var wilE2 = new Rectangle(ketemuX - expR, ketemuY - expR, expR * 2, expR * 2);
                                bool ver = false;
                                var swE = System.Diagnostics.Stopwatch.StartNew();
                                while (swE.ElapsedMilliseconds < expT)
                                {
                                    try
                                    {
                                        using (var bmp = CaptureRegionBitmap(wilE2, 0, out var effE2))
                                        {
                                            var rr2 = OcrReader.Read(bmp, effE2.X, effE2.Y);
                                            if (rr2.Text != null && rr2.Text.IndexOf(expectW, StringComparison.OrdinalIgnoreCase) >= 0) { ver = true; break; }
                                        }
                                    }
                                    catch { /* abaikan */ }
                                    System.Threading.Thread.Sleep(250);
                                }
                                respW["verified"] = ver;
                                respW["expectText"] = expectW;
                                _log($"clickwhen verifikasi '{expectW}': {(ver ? "KETEMU" : "tidak ketemu")}");
                            }
                            return Response.JsonObject(respW);
                        }
                        case "/keydown":
                        case "/keyup":
                        {
                            // Tahan / lepas satu tombol (gelombang 2) —
                            // untuk kombinasi lintas perintah, mis.
                            // tahan CTRL lalu /click di tempat lain.
                            string keyName = GetString(root, "key", "");
                            ushort vk = NativeInput.VkFromName(keyName);
                            if (vk == 0)
                                return Response.Json("{\"ok\":false,\"error\":\"nama tombol tidak dikenal\"}", 400);
                            bool down = req.Path == "/keydown";
                            bool scanHold = GetBool(root, "scan", false);
                            OnUi(() =>
                            {
                                if (scanHold)
                                {
                                    if (down) NativeInput.KeyDownScan(vk);
                                    else NativeInput.KeyUpScan(vk);
                                }
                                else
                                {
                                    if (down) NativeInput.KeyDown(vk);
                                    else NativeInput.KeyUp(vk);
                                }
                                return true;
                            });
                            // v3.6.0 (BUG-20): catat tombol tertahan
                            // supaya /keys/release & Stop() bisa
                            // melepasnya — jangan sampai CTRL/ALT
                            // tertahan logis di sesi Windows user.
                            lock (_heldLock)
                            {
                                if (down) _heldKeys.Add(vk);
                                else _heldKeys.Remove(vk);
                            }
                            _onActivity();
                            _log((down ? "tahan tombol: " : "lepas tombol: ") + keyName);
                            return Ok();
                        }
                        case "/mousedown":
                        case "/mouseup":
                        {
                            // Tahan / lepas tombol mouse (v5.9.0 —
                            // pasangan /keydown-/keyup untuk mouse):
                            // tekan-tahan & seret manual dua tahap
                            // jadi mungkin. Tombol tertahan dicatat
                            // dan dilepas paksa oleh ReleaseHeldKeys
                            // (Stop & /keys/release) — tidak boleh
                            // tertahan selamanya di sesi user.
                            string mButton = GetString(root, "button", "left");
                            bool mDown = req.Path == "/mousedown";
                            if (mDown && HasInt(root, "x") && HasInt(root, "y"))
                            {
                                int mx = GetInt(root, "x", 0), my = GetInt(root, "y", 0);
                                OnUi(() => { NativeInput.Move(mx, my); return true; });
                            }
                            OnUi(() =>
                            {
                                if (mDown) NativeInput.ButtonDown(mButton);
                                else NativeInput.ButtonUp(mButton);
                                return true;
                            });
                            lock (_heldLock)
                            {
                                if (mDown) _heldMouse.Add(mButton.ToLowerInvariant());
                                else _heldMouse.Remove(mButton.ToLowerInvariant());
                            }
                            _onActivity();
                            _log((mDown ? "tahan mouse: " : "lepas mouse: ") + mButton);
                            return Ok();
                        }
                        case "/keys/release":
                        {
                            // Lepas SEMUA tombol tertahan (v3.6.0,
                            // BUG-20 audit): jaring pengaman bila
                            // urutan /keydown-/keyup terputus di
                            // tengah — tombol tidak tertahan selamanya.
                            int nRel = ReleaseHeldKeys();
                            _onActivity();
                            _log("lepas semua tombol tertahan: " + nRel);
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["released"] = nRel,
                            });
                        }
                        case "/wait":
                        {
                            // Tunggu kondisi (gelombang 2): jendela
                            // berjudul tertentu muncul ATAU berkas ada.
                            // Pemeriksaan di sisi server tiap 500ms —
                            // Muse tidak perlu bolak-balik bertanya.
                            string wTitle = GetString(root, "windowTitle", "");
                            string fPath = GetString(root, "filePath", "");
                            // Kondisi proses (v2.4.4): processGone —
                            // tunggu sampai proses bernama itu TIDAK
                            // jalan lagi (installer/build kelar);
                            // processExists — tunggu sampai jalan.
                            // Nama tanpa akhiran .exe.
                            // see (v2.4.4, "wait-and-see"): bila
                            // kondisi layar yang ketemu, respons
                            // ditutup gambar wilayah yang dipantau.
                            string pGone = GetString(root, "processGone", "");
                            string pExists = GetString(root, "processExists", "");
                            if (pGone.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) pGone = pGone.Substring(0, pGone.Length - 4);
                            if (pExists.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) pExists = pExists.Substring(0, pExists.Length - 4);
                            bool see = GetBool(root, "see", false);
                            // Mode layar (v2.4.2, usulan performa B):
                            // screenChange {x,y,w,h} — tunggu sampai
                            // piksel wilayah itu BERUBAH dari keadaan
                            // awal; screenQuiet {x,y,w,h,quietMs} —
                            // tunggu sampai wilayah itu DIAM selama
                            // quietMs (bawaan 1500). PC yang memantau
                            // sendiri tiap 250ms; Muse cukup menunggu
                            // satu laporan.
                            bool hasChange = false, hasQuiet = false;
                            int chX = 0, chY = 0, chW = 0, chH = 0;
                            int quX = 0, quY = 0, quW = 0, quH = 0, quMs = 1500;
                            if (root.TryGetProperty("screenChange", out var scEl) &&
                                scEl.ValueKind == JsonValueKind.Object)
                            {
                                hasChange = true;
                                chX = GetInt(scEl, "x", 0); chY = GetInt(scEl, "y", 0);
                                chW = GetInt(scEl, "w", 0); chH = GetInt(scEl, "h", 0);
                            }
                            if (root.TryGetProperty("screenQuiet", out var sqEl) &&
                                sqEl.ValueKind == JsonValueKind.Object)
                            {
                                hasQuiet = true;
                                quX = GetInt(sqEl, "x", 0); quY = GetInt(sqEl, "y", 0);
                                quW = GetInt(sqEl, "w", 0); quH = GetInt(sqEl, "h", 0);
                                quMs = GetInt(sqEl, "quietMs", 1500);
                                if (quMs < 200) quMs = 200;
                                if (quMs > 60000) quMs = 60000;
                            }
                            // Mode changePct (v2.6.2): tunggu sampai
                            // CAKUPAN perubahan wilayah mencapai
                            // ambang persen — lebih bermakna dari
                            // screenChange biner untuk kejadian
                            // bertahap (loading, animasi selesai).
                            // {x,y,w,h,pct} — pct bawaan 5.
                            bool hasPct = false;
                            int pcX = 0, pcY = 0, pcW = 0, pcH = 0;
                            double pcTarget = 5.0;
                            if (root.TryGetProperty("changePct", out var cpEl) &&
                                cpEl.ValueKind == JsonValueKind.Object)
                            {
                                hasPct = true;
                                pcX = GetInt(cpEl, "x", 0); pcY = GetInt(cpEl, "y", 0);
                                pcW = GetInt(cpEl, "w", 0); pcH = GetInt(cpEl, "h", 0);
                                if (cpEl.TryGetProperty("pct", out var pctEl) &&
                                    pctEl.ValueKind == JsonValueKind.Number)
                                    pcTarget = pctEl.GetDouble();
                                if (pcTarget < 0.1) pcTarget = 0.1;
                                if (pcTarget > 100) pcTarget = 100;
                            }
                            if (string.IsNullOrWhiteSpace(wTitle) && string.IsNullOrWhiteSpace(fPath) &&
                                !hasChange && !hasQuiet && !hasPct &&
                                string.IsNullOrWhiteSpace(pGone) && string.IsNullOrWhiteSpace(pExists))
                                return Response.Json("{\"ok\":false,\"error\":\"windowTitle, filePath, screenChange, screenQuiet, processGone, processExists, atau changePct wajib diisi\"}", 400);
                            int wTimeout = GetInt(root, "timeoutMs", 30000);
                            if (wTimeout < 1000) wTimeout = 1000;
                            if (wTimeout > MaxExecTimeoutMs) wTimeout = MaxExecTimeoutMs;
                            _onActivity();
                            var condParts = new List<string>();
                            if (!string.IsNullOrWhiteSpace(wTitle)) condParts.Add("jendela '" + wTitle + "'");
                            if (!string.IsNullOrWhiteSpace(fPath)) condParts.Add("berkas " + fPath);
                            if (hasChange) condParts.Add("layar berubah");
                            if (hasQuiet) condParts.Add("layar diam");
                            if (hasPct) condParts.Add($"layar berubah >={pcTarget}%");
                            if (!string.IsNullOrWhiteSpace(pGone)) condParts.Add("proses " + pGone + " selesai");
                            if (!string.IsNullOrWhiteSpace(pExists)) condParts.Add("proses " + pExists + " jalan");
                            _log($"tunggu kondisi (maks {wTimeout}ms): " + string.Join(" / ", condParts));
                            var wsw = Stopwatch.StartNew();
                            bool found = false;
                            string via = "";
                            byte[] changeBase = hasChange ? RegionHash(chX, chY, chW, chH) : null;
                            Bitmap pctBase = null;
                            Rectangle pctRegion = Rectangle.Empty;
                            double pctLast = 0;
                            if (hasPct) pctBase = CaptureRegionBitmap(new Rectangle(pcX, pcY, pcW, pcH), 0, out pctRegion);
                            byte[] quietLast = hasQuiet ? RegionHash(quX, quY, quW, quH) : null;
                            long quietSince = 0;
                            int pollMs = (hasChange || hasQuiet || hasPct) ? 250 : 500;
                            while (wsw.ElapsedMilliseconds < wTimeout)
                            {
                                if (!string.IsNullOrWhiteSpace(fPath) && File.Exists(fPath))
                                {
                                    found = true; via = "file"; break;
                                }
                                if (!string.IsNullOrWhiteSpace(wTitle))
                                {
                                    var wins = OnUi(NativeInput.ListWindows);
                                    foreach (var w in wins)
                                    {
                                        if (w.Title.IndexOf(wTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            found = true; via = "window"; break;
                                        }
                                    }
                                    if (found) break;
                                }
                                if (!found && !string.IsNullOrWhiteSpace(pGone))
                                {
                                    try
                                    {
                                        if (Process.GetProcessesByName(pGone).Length == 0)
                                        {
                                            found = true; via = "process-gone";
                                        }
                                    }
                                    catch { /* nama aneh dsb. — anggap belum selesai */ }
                                }
                                if (!found && !string.IsNullOrWhiteSpace(pExists))
                                {
                                    try
                                    {
                                        if (Process.GetProcessesByName(pExists).Length > 0)
                                        {
                                            found = true; via = "process-exists";
                                        }
                                    }
                                    catch { /* abaikan */ }
                                }
                                if (!found && hasChange)
                                {
                                    var h = RegionHash(chX, chY, chW, chH);
                                    if (!HashesEqual(h, changeBase))
                                    {
                                        found = true; via = "screen-change";
                                    }
                                }
                                if (!found && hasQuiet)
                                {
                                    var h = RegionHash(quX, quY, quW, quH);
                                    if (!HashesEqual(h, quietLast))
                                    {
                                        quietLast = h;
                                        quietSince = wsw.ElapsedMilliseconds;
                                    }
                                    else if (wsw.ElapsedMilliseconds - quietSince >= quMs)
                                    {
                                        found = true; via = "screen-quiet";
                                    }
                                }
                                if (!found && hasPct && pctBase != null)
                                {
                                    using (var cur = CaptureRegionBitmap(pctRegion, 0, out var effP))
                                    {
                                        var bl = CompareBlocks(pctBase, cur, 32, 16, effP.X, effP.Y, out int tot);
                                        pctLast = tot > 0 ? bl.Count * 100.0 / tot : 0;
                                        if (pctLast >= pcTarget)
                                        {
                                            found = true; via = "change-pct";
                                        }
                                    }
                                }
                                if (found) break;
                                Thread.Sleep(pollMs);
                            }
                            if (pctBase != null) pctBase.Dispose();
                            long waited = wsw.ElapsedMilliseconds;
                            if (waited > wTimeout) waited = wTimeout;
                            _log(found ? $"kondisi ketemu via {via} setelah {waited}ms" : $"kondisi tidak ketemu ({wTimeout}ms)");
                            var waitResp = new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["found"] = found,
                                ["via"] = via,
                                ["waitedMs"] = waited,
                            };
                            if (hasPct) waitResp["coveragePct"] = Math.Round(pctLast, 1);
                            // Wait-and-see: sertakan gambar wilayah
                            // yang dipantau (yang berubah / yang
                            // sudah diam) supaya Muse tidak perlu
                            // satu panggilan gambar lagi.
                            if (see && found && (via == "screen-change" || via == "screen-quiet" || via == "change-pct"))
                            {
                                var wr = via == "screen-change"
                                    ? new Rectangle(chX, chY, chW, chH)
                                    : via == "change-pct" ? pctRegion
                                    : new Rectangle(quX, quY, quW, quH);
                                try
                                {
                                    using (var wb = CaptureRegionBitmap(wr, 960, out var weff))
                                    {
                                        var wj = EncodeJpeg(wb, 70);
                                        waitResp["frame"] = new Dictionary<string, object>
                                        {
                                            ["mime"] = "image/jpeg",
                                            ["x"] = weff.X, ["y"] = weff.Y,
                                            ["width"] = wb.Width, ["height"] = wb.Height,
                                            ["b64"] = Convert.ToBase64String(wj),
                                        };
                                    }
                                }
                                catch { /* gambar bonus — gagal pun laporan tunggu tetap sah */ }
                            }
                            return Response.JsonObject(waitResp);
                        }
                        case "/uia/click":
                        {
                            // Klik elemen dari pindai /uia terakhir —
                            // berdasarkan indeks atau nama (v2.4.0).
                            // Penjaga kebasian (v3.6.0, BUG-02 audit):
                            // koordinat pindai tidak boleh diklik bila
                            // jendela depan sudah berganti atau pindai
                            // berumur >90 detik — dulu klik mendarat di
                            // jendela lain sambil melaporkan sukses.
                            if (UiaScan.LastScanHwnd != IntPtr.Zero &&
                                NativeInput.ForegroundHandle() != UiaScan.LastScanHwnd)
                                return Response.Json("{\"ok\":false,\"error\":\"jendela depan berubah sejak pindai terakhir — pindai ulang lewat GET /uia\"}", 409);
                            if (UiaScan.LastScanTick > 0 &&
                                Environment.TickCount64 - UiaScan.LastScanTick > 90000)
                                return Response.Json("{\"ok\":false,\"error\":\"pindai terlalu tua (>90 detik) — pindai ulang lewat GET /uia\"}", 409);
                            int index = GetInt(root, "index", -1);
                            string name = GetString(root, "name", "");
                            var item = OnUi(() => UiaScan.Find(index, name));
                            if (item == null)
                                return Response.Json("{\"ok\":false,\"error\":\"elemen tidak ditemukan — pindai dulu lewat GET /uia\"}", 404);
                            string button = GetString(root, "button", "left");
                            int cx = item.CenterX, cy = item.CenterY;
                            OnUi(() => { NativeInput.Click(cx, cy, button); return true; });
                            _onActivity();
                            _lastClickX = cx; _lastClickY = cy; _lastClickTick = Environment.TickCount64;
                            _log($"klik elemen UIA '{item.Name}' ({item.Type}) di {cx},{cy}");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["clicked"] = item.Name,
                                ["type"] = item.Type,
                                ["x"] = cx,
                                ["y"] = cy,
                            });
                        }
                        case "/locate":
                        {
                            // Fondasi Visual Grounding (v3.6.0): SATU
                            // panggilan mencari elemen/teks di layar —
                            // UIA dulu (pindai segar jendela depan),
                            // gugur ke OCR layar penuh bila UIA tidak
                            // menemukan (aplikasi buta-UIA seperti
                            // CapCut). click:true mengklik kandidat
                            // pertama; marks:true melampirkan gambar
                            // berkotak nomor (Set-of-Marks) untuk
                            // verifikasi visual sebelum bertindak.
                            string q = GetString(root, "text", "");
                            if (string.IsNullOrWhiteSpace(q))
                                return Response.Json("{\"ok\":false,\"error\":\"text wajib diisi\"}", 400);
                            string mode = GetString(root, "mode", "auto").Trim().ToLowerInvariant();
                            bool doClick = GetBool(root, "click", false);
                            bool wantMarks = GetBool(root, "marks", false);
                            var hits = new List<LocateHit>();
                            string viaUsed = "";
                            if (mode == "auto" || mode == "uia")
                            {
                                var scan = OnUi(() => UiaScan.ScanForeground(500));
                                foreach (var it in scan)
                                {
                                    if (it.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                                        hits.Add(new LocateHit { Via = "uia", Text = it.Name, Type = it.Type, Rect = it.Rect });
                                    if (hits.Count >= 10) break;
                                }
                                if (hits.Count > 0) viaUsed = "uia";
                            }
                            if (hits.Count == 0 && (mode == "auto" || mode == "uia3"))
                            {
                                // Anak tangga UIA lintas jendela
                                // (v5.15.0): pindai depan di atas
                                // hanya melihat jendela aktif —
                                // ikon taskbar (Start, File
                                // Explorer, Chrome) & elemen di
                                // jendela latar lolos dari sana
                                // (temuan Ronde 2h/4-1). Pakai
                                // mesin /uia3/find yang sama:
                                // semua jendela tingkat atas,
                                // elemen bernama memuat query.
                                var cross = OnUi(() => UiaScan.FindAcrossWindows(q, "", "", "", 1500));
                                foreach (var it in cross)
                                {
                                    if (it.Rect.Width <= 0 || it.Rect.Height <= 0) continue;
                                    hits.Add(new LocateHit { Via = "uia3", Text = it.Name, Type = it.Type, Rect = it.Rect });
                                    if (hits.Count >= 10) break;
                                }
                                if (hits.Count > 0) viaUsed = "uia3";
                            }
                            if (hits.Count == 0 && (mode == "auto" || mode == "ocr"))
                            {
                                var bounds = NativeInput.PrimaryBounds();
                                OcrReader.Result ocrRes = null;
                                using (var bmp = CaptureRegionBitmap(bounds, 0, out var eff))
                                    ocrRes = OcrReader.Read(bmp, eff.X, eff.Y);
                                // Kata tunggal mengandung query, atau
                                // dua kata berurutan digabung (query
                                // berspasi, mis. "Create project").
                                var ws = ocrRes.Words;
                                // Lolos 1: cocok persis. Lolos 2 (bila
                                // kosong & query mengandung angka):
                                // cocok KABUR — OCR sering menukar
                                // 0<->O/ø, 1<->l/I, 5<->S, 8<->B
                                // ("ZEBRA360" terbaca "ZEBRA36ø").
                                for (int pass = 0; pass < 2 && hits.Count == 0; pass++)
                                {
                                    string qq = pass == 0 ? q : NormalizeOcrFuzzy(q);
                                    if (qq == null) break;
                                    for (int i = 0; i < ws.Count && hits.Count < 10; i++)
                                    {
                                        string w1 = pass == 0 ? ws[i].Text
                                            : (NormalizeOcrFuzzy(ws[i].Text) ?? ws[i].Text);
                                        if (w1.IndexOf(qq, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            hits.Add(new LocateHit { Via = "ocr", Text = ws[i].Text, Type = "word", Rect = ws[i].Rect });
                                        }
                                        else if (q.IndexOf(' ') > 0 && i + 1 < ws.Count)
                                        {
                                            string gab = ws[i].Text + " " + ws[i + 1].Text;
                                            string gabN = pass == 0 ? gab
                                                : (NormalizeOcrFuzzy(gab) ?? gab);
                                            if (gabN.IndexOf(qq, StringComparison.OrdinalIgnoreCase) >= 0)
                                            {
                                                var rr = Rectangle.Union(ws[i].Rect, ws[i + 1].Rect);
                                                hits.Add(new LocateHit { Via = "ocr", Text = gab, Type = "words", Rect = rr });
                                            }
                                        }
                                    }
                                }
                                if (hits.Count > 0) viaUsed = "ocr";
                            }
                            if (hits.Count == 0 && (mode == "auto" || mode == "yolo"))
                            {
                                // Cadangan Visual Grounding GPU
                                // (v5.5.0): YOLO mengenali objek
                                // dunia nyata berlabel tetap (COCO).
                                // Query dipetakan ke label (alias
                                // Indonesia di MapCocoLabel); tak
                                // terpetakan -> lewati diam-diam.
                                string labelY = MapCocoLabel(q);
                                if (labelY != null)
                                {
                                    var boundsY = NativeInput.PrimaryBounds();
                                    using (var bmpY = CaptureRegionBitmap(boundsY, 0, out var effY))
                                    {
                                        var detsY = AiBridge.DetectList(bmpY, effY.X, effY.Y, out var yErr);
                                        if (detsY != null)
                                        {
                                            foreach (var dY in detsY)
                                            {
                                                if (string.Equals(dY.Label, labelY, StringComparison.OrdinalIgnoreCase))
                                                    hits.Add(new LocateHit { Via = "yolo-gpu", Text = dY.Label, Type = "object", Rect = dY.Rect });
                                                if (hits.Count >= 10) break;
                                            }
                                            if (hits.Count > 0) viaUsed = "yolo-gpu";
                                        }
                                        else _log("locate YOLO gagal: " + yErr);
                                    }
                                }
                            }
                            if (hits.Count == 0 && (mode == "auto" || mode == "omni"))
                            {
                                // Tahap terakhir (v5.11.0 —
                                // Bagian D): OmniParser untuk UI
                                // buta-UIA (canvas, Electron,
                                // game). mode "omni" eksplisit =
                                // kembalikan SEMUA kotak elemen
                                // (maks 40) agar pemanggil memilih;
                                // mode "auto" = hanya kotak yang
                                // label teksnya memuat query.
                                var boundsO = NativeInput.PrimaryBounds();
                                using (var bmpO = CaptureRegionBitmap(boundsO, 0, out var effO))
                                {
                                    var detsO = AiBridge.ParseList(bmpO, effO.X, effO.Y, out var oErr);
                                    if (detsO != null)
                                    {
                                        foreach (var dO in detsO)
                                        {
                                            if (mode == "omni" ||
                                                dO.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                                                hits.Add(new LocateHit { Via = "omniparser", Text = dO.Label, Type = "element", Rect = dO.Rect });
                                            if (hits.Count >= 40) break;
                                        }
                                        if (hits.Count > 0) viaUsed = "omniparser";
                                    }
                                    else _log("locate OmniParser gagal: " + oErr);
                                }
                            }
                            var cand = new List<Dictionary<string, object>>();
                            foreach (var h in hits)
                            {
                                cand.Add(new Dictionary<string, object>
                                {
                                    ["via"] = h.Via, ["text"] = h.Text, ["type"] = h.Type,
                                    ["x"] = h.Rect.X, ["y"] = h.Rect.Y,
                                    ["w"] = h.Rect.Width, ["h"] = h.Rect.Height,
                                    ["cx"] = h.Rect.X + h.Rect.Width / 2,
                                    ["cy"] = h.Rect.Y + h.Rect.Height / 2,
                                });
                            }
                            bool clicked = false;
                            if (doClick && hits.Count > 0)
                            {
                                int cx = hits[0].Rect.X + hits[0].Rect.Width / 2;
                                int cy = hits[0].Rect.Y + hits[0].Rect.Height / 2;
                                OnUi(() => { NativeInput.Click(cx, cy, "left"); return true; });
                                _lastClickX = cx; _lastClickY = cy; _lastClickTick = Environment.TickCount64;
                                clicked = true;
                            }
                            _onActivity();
                            _log($"locate '{q}': {hits.Count} kandidat" +
                                 (viaUsed != "" ? " via " + viaUsed : "") +
                                 (clicked ? " (diklik)" : ""));
                            var resp = new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["found"] = hits.Count > 0,
                                ["via"] = viaUsed,
                                ["count"] = hits.Count,
                                ["candidates"] = cand,
                                ["clicked"] = clicked,
                            };
                            // Sesi grounding (v3.7.0): simpan hasil
                            // ini 60 detik agar /locate/act bisa
                            // mengklik kandidat PILIHAN (cid stabil)
                            // sesudah validasi ulang.
                            string sessionId = "";
                            if (hits.Count > 0)
                            {
                                var gs = new GroundSession
                                {
                                    Id = "loc-" + Interlocked.Increment(ref _groundSeq),
                                    Query = q,
                                    CreatedTick = Environment.TickCount64,
                                };
                                try
                                {
                                    var fgInfo = OnUi(() =>
                                    {
                                        var h = NativeInput.ForegroundHandle();
                                        int pid = 0; string title = "";
                                        foreach (var w in NativeInput.ListWindows())
                                            if (w.Handle == h) { pid = w.Pid; title = w.Title; break; }
                                        return (h, pid, title);
                                    });
                                    gs.Hwnd = fgInfo.h.ToInt64();
                                    gs.Pid = fgInfo.pid;
                                    gs.WindowTitle = fgInfo.title ?? "";
                                }
                                catch { /* sesi tetap dibuat; act menolak bila jendela asal tak dikenal */ }
                                bool digitQ = false;
                                foreach (char ch in q) if (char.IsDigit(ch)) { digitQ = true; break; }
                                for (int i = 0; i < hits.Count; i++)
                                {
                                    var gc = new GroundCandidate
                                    {
                                        Cid = "c" + (i + 1),
                                        Via = hits[i].Via,
                                        Text = hits[i].Text,
                                        Type = hits[i].Type,
                                        Rect = hits[i].Rect,
                                        DigitFuzzy = digitQ,
                                    };
                                    gs.Candidates.Add(gc);
                                    if (i < cand.Count) cand[i]["cid"] = gc.Cid;
                                }
                                PruneGroundSessions(gs.CreatedTick);
                                lock (_groundLock) { _groundSessions[gs.Id] = gs; }
                                sessionId = gs.Id;
                            }
                            resp["session"] = sessionId;
                            resp["sessionTtlSec"] = GroundSessionTtlMs / 1000;
                            if (wantMarks && hits.Count > 0)
                            {
                                var boundsM = NativeInput.PrimaryBounds();
                                using (var bmp = CaptureRegionBitmap(boundsM, 1280, out var effM))
                                {
                                    DrawMarks(bmp, effM, hits);
                                    resp["image"] = Convert.ToBase64String(EncodeJpeg(bmp, 80));
                                    resp["imageW"] = bmp.Width;
                                    resp["imageH"] = bmp.Height;
                                }
                            }
                            return Response.JsonObject(resp);
                        }
                        case "/locate/act":
                        {
                            // Klik kandidat DARI sesi grounding
                            // (v3.7.0): validasi ulang dulu — sesi
                            // harus hidup, jendela depan harus sama,
                            // target harus masih di tempatnya.
                            // Berubah/tidak valid -> 409, tidak diklik.
                            _onActivity();
                            string sid = GetString(root, "session", "");
                            string cidWanted = GetString(root, "cid", "");
                            int idxWanted = GetInt(root, "index", -1);
                            GroundSession sess = null;
                            if (!string.IsNullOrEmpty(sid))
                                lock (_groundLock) { _groundSessions.TryGetValue(sid, out sess); }
                            if (sess == null)
                                return Response.Json("{\"ok\":false,\"error\":\"sesi grounding tidak ditemukan\"}", 404);
                            if (Environment.TickCount64 - sess.CreatedTick > GroundSessionTtlMs)
                            {
                                lock (_groundLock) { _groundSessions.Remove(sess.Id); }
                                return Response.Json("{\"ok\":false,\"error\":\"sesi grounding kedaluwarsa (60 detik) — panggil /locate lagi\"}", 409);
                            }
                            GroundCandidate candSel = null;
                            if (!string.IsNullOrEmpty(cidWanted))
                                candSel = sess.Candidates.Find(c => c.Cid == cidWanted);
                            else if (idxWanted >= 0 && idxWanted < sess.Candidates.Count)
                                candSel = sess.Candidates[idxWanted];
                            if (candSel == null)
                                return Response.Json("{\"ok\":false,\"error\":\"kandidat tidak ada di sesi ini\"}", 404);

                            // 1) Jendela depan harus sama seperti
                            // saat /locate; proses asal harus hidup.
                            var fgNow = OnUi(NativeInput.ForegroundHandle);
                            if (sess.Hwnd != 0 && fgNow.ToInt64() != sess.Hwnd)
                                return Response.Json("{\"ok\":false,\"error\":\"jendela depan berubah sejak /locate — target tidak valid lagi\"}", 409);
                            if (sess.Pid > 0)
                            {
                                try { Process.GetProcessById(sess.Pid); }
                                catch { return Response.Json("{\"ok\":false,\"error\":\"proses jendela asal sudah mati\"}", 409); }
                            }

                            // 2) Validasi ulang isi target di
                            // tempatnya; pakai koordinat SEGAR hasil
                            // validasi untuk klik.
                            int clickX = candSel.CX, clickY = candSel.CY;
                            if (candSel.Via == "uia")
                            {
                                var fresh = OnUi(() => UiaScan.ScanForeground(500));
                                UiaScan.Item found = null;
                                foreach (var n in fresh)
                                {
                                    if (!string.Equals(n.Name, candSel.Text, StringComparison.Ordinal)) continue;
                                    if (!string.IsNullOrEmpty(candSel.Type) &&
                                        !string.Equals(n.Type, candSel.Type, StringComparison.Ordinal)) continue;
                                    if (Math.Abs(n.CenterX - candSel.CX) <= 24 &&
                                        Math.Abs(n.CenterY - candSel.CY) <= 24) { found = n; break; }
                                }
                                if (found == null)
                                    return Response.Json("{\"ok\":false,\"error\":\"elemen UIA tidak ditemukan lagi di tempatnya — target berubah\"}", 409);
                                clickX = found.CenterX; clickY = found.CenterY;
                            }
                            else
                            {
                                // Wilayah validasi diperlebar jauh
                                // (perbaikan v3.7.1): OCR WinRT
                                // mengembalikan KOSONG pada crop
                                // mungil seukuran kata — terbukti di
                                // uji v3.7.0 (crop 79x26 -> 0 kata,
                                // wilayah lega terbaca normal). Baca
                                // potongan layar yang lega, cari
                                // KATA-nya untuk koordinat segar.
                                var scrB = NativeInput.PrimaryBounds();
                                int vx = Math.Max(scrB.X, candSel.Rect.X - 200);
                                int vy = Math.Max(scrB.Y, candSel.Rect.Y - 60);
                                int vw = Math.Min(scrB.Right - vx, candSel.Rect.Width + 400);
                                int vh = Math.Min(scrB.Bottom - vy, candSel.Rect.Height + 120);
                                var reg = new Rectangle(vx, vy, Math.Max(40, vw), Math.Max(30, vh));
                                var ocrRes = OnUi(() =>
                                {
                                    using (var bmp = CaptureRegionBitmap(reg, 0, out var effR))
                                        return OcrReader.Read(bmp, effR.X, effR.Y);
                                });
                                OcrReader.Word wordHit = null;
                                foreach (var w in ocrRes.Words)
                                {
                                    if (w.Text.IndexOf(candSel.Text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        (w.Text.Length >= 3 &&
                                         candSel.Text.IndexOf(w.Text, StringComparison.OrdinalIgnoreCase) >= 0))
                                    { wordHit = w; break; }
                                }
                                if (wordHit == null && candSel.DigitFuzzy)
                                {
                                    string qN = NormalizeOcrFuzzy(candSel.Text);
                                    if (qN != null)
                                        foreach (var w in ocrRes.Words)
                                        {
                                            string wN = NormalizeOcrFuzzy(w.Text) ?? w.Text;
                                            if (wN.IndexOf(qN, StringComparison.OrdinalIgnoreCase) >= 0)
                                            { wordHit = w; break; }
                                        }
                                }
                                bool still = wordHit != null;
                                if (!still)
                                {
                                    string hay = ocrRes.Text ?? "";
                                    still = hay.IndexOf(candSel.Text, StringComparison.OrdinalIgnoreCase) >= 0;
                                    if (!still && candSel.DigitFuzzy)
                                    {
                                        string hayN = NormalizeOcrFuzzy(hay);
                                        string qN = NormalizeOcrFuzzy(candSel.Text);
                                        still = hayN != null && qN != null &&
                                                hayN.IndexOf(qN, StringComparison.OrdinalIgnoreCase) >= 0;
                                    }
                                }
                                if (!still)
                                    return Response.Json("{\"ok\":false,\"error\":\"teks target tidak ditemukan lagi di wilayahnya — target berubah\"}", 409);
                                if (wordHit != null)
                                {
                                    clickX = wordHit.Rect.X + wordHit.Rect.Width / 2;
                                    clickY = wordHit.Rect.Y + wordHit.Rect.Height / 2;
                                }
                            }

                            // 3) Potret wilayah klik sebelum & sesudah
                            // untuk verifikasi perubahan layar.
                            var clip = new Rectangle(Math.Max(0, clickX - 60), Math.Max(0, clickY - 40), 120, 80);
                            byte[] hashBefore;
                            using (var b0 = CaptureRegionBitmap(clip, 0, out _)) hashBefore = HashBitmap(b0);
                            OnUi(() => { NativeInput.Click(clickX, clickY, "left"); return true; });
                            _lastClickX = clickX; _lastClickY = clickY; _lastClickTick = Environment.TickCount64;
                            Thread.Sleep(180);
                            bool changed;
                            using (var b1 = CaptureRegionBitmap(clip, 0, out _))
                                changed = !hashBefore.SequenceEqual(HashBitmap(b1));
                            _log($"locate/act {sess.Id}/{candSel.Cid} '{candSel.Text}' -> klik ({clickX},{clickY}), layar berubah: {changed}");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["clicked"] = true,
                                ["session"] = sess.Id,
                                ["cid"] = candSel.Cid,
                                ["via"] = candSel.Via,
                                ["x"] = clickX,
                                ["y"] = clickY,
                                ["verified"] = new Dictionary<string, object> { ["screenChanged"] = changed },
                                ["note"] = changed
                                    ? "layar di sekitar titik klik berubah sesudah klik"
                                    : "tidak ada perubahan layar terdeteksi di sekitar titik klik",
                            });
                        }
                        case "/uia/find":
                        {
                            // Cari elemen + (opsional) klik dalam
                            // SATU panggilan (v2.4.4): pindai baru
                            // jendela depan, saring berdasar nama
                            // dan/atau tipe, kembalikan semua yang
                            // cocok; click:true mengklik kecocokan
                            // pertama. Menghemat satu perjalanan
                            // penuh dibanding /uia lalu /uia/click.
                            string fName = GetString(root, "name", "");
                            string fType = GetString(root, "type", "");
                            if (string.IsNullOrWhiteSpace(fName) && string.IsNullOrWhiteSpace(fType))
                                return Response.Json("{\"ok\":false,\"error\":\"name atau type wajib diisi\"}", 400);
                            bool fClick = GetBool(root, "click", false);
                            int fMax = GetInt(root, "max", 20);
                            if (fMax < 1) fMax = 1;
                            if (fMax > 100) fMax = 100;
                            var hasil = OnUi(() =>
                            {
                                var scan = UiaScan.ScanForeground(500);
                                var cocok = new List<UiaScan.Item>();
                                foreach (var it in scan)
                                {
                                    if (!string.IsNullOrWhiteSpace(fName) &&
                                        it.Name.IndexOf(fName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    if (!string.IsNullOrWhiteSpace(fType) &&
                                        it.Type.IndexOf(fType, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    cocok.Add(it);
                                }
                                bool clicked = false;
                                if (fClick && cocok.Count > 0)
                                {
                                    NativeInput.Click(cocok[0].CenterX, cocok[0].CenterY, "left");
                                    clicked = true;
                                }
                                return (cocok, clicked);
                            });
                            _onActivity();
                            var fList = new List<Dictionary<string, object>>();
                            foreach (var it in hasil.cocok)
                            {
                                if (fList.Count >= fMax) break;
                                fList.Add(new Dictionary<string, object>
                                {
                                    ["name"] = it.Name,
                                    ["type"] = it.Type,
                                    ["x"] = it.Rect.X,
                                    ["y"] = it.Rect.Y,
                                    ["w"] = it.Rect.Width,
                                    ["h"] = it.Rect.Height,
                                    ["cx"] = it.CenterX,
                                    ["cy"] = it.CenterY,
                                    ["enabled"] = it.Enabled,
                                    ["offscreen"] = it.Offscreen,
                                });
                            }
                            _log($"uia find '{fName}'/'{fType}': {hasil.cocok.Count} cocok" + (hasil.clicked ? ", pertama diklik" : ""));
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["count"] = hasil.cocok.Count,
                                ["clicked"] = hasil.clicked,
                                ["matches"] = fList,
                            });
                        }
                        case "/find":
                        {
                            // Cari apa pun di layar (v2.5.9, fitur
                            // untuk Muse atas pilihan user):
                            // gabungan UIA (elemen jendela depan) +
                            // OCR (teks di seluruh layar) dalam satu
                            // panggilan, kembali koordinat tengah
                            // tiap kecocokan. {text, click?, max?}.
                            string q = GetString(root, "text", "");
                            if (string.IsNullOrWhiteSpace(q))
                                return Response.Json("{\"ok\":false,\"error\":\"field wajib hilang: text\"}", 400);
                            bool fClick = GetBool(root, "click", false);
                            int fMax = GetInt(root, "max", 10);
                            if (fMax < 1) fMax = 1;
                            if (fMax > 30) fMax = 30;
                            // Koordinat relatif jendela (v2.6.3):
                            // {relativeTo:"potongan judul"} — OCR
                            // dibatasi ke wilayah jendela itu dan
                            // SEMUA koordinat hasil dikembalikan
                            // relatif terhadap sudut jendelanya;
                            // respons memuat "window" (rect absolut
                            // jendelanya) untuk konversi balik.
                            // Jendela tidak ketemu -> 404.
                            string fRelTo = GetString(root, "relativeTo", "");
                            NativeInput.WindowInfo fWin = null;
                            if (!string.IsNullOrWhiteSpace(fRelTo))
                            {
                                fWin = FindWindowByTitle(fRelTo);
                                if (fWin == null)
                                    return Response.Json("{\"ok\":false,\"error\":\"jendela tidak ditemukan: " + fRelTo.Replace("\\", " ").Replace("\"", " ") + "\"}", 404);
                            }
                            int relOx = fWin != null ? fWin.Rect.X : 0;
                            int relOy = fWin != null ? fWin.Rect.Y : 0;
                            _onActivity();
                            var matches = new List<Dictionary<string, object>>();
                            int uiaCount = 0, ocrCount = 0;
                            var uiaHits = OnUi(() =>
                            {
                                var scan = UiaScan.ScanForeground(500);
                                var hits = new List<UiaScan.Item>();
                                foreach (var it in scan)
                                {
                                    if (it.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                                        hits.Add(it);
                                }
                                return hits;
                            });
                            foreach (var it in uiaHits)
                            {
                                if (fWin != null && !it.Rect.IntersectsWith(fWin.Rect)) continue;
                                uiaCount++;
                                matches.Add(new Dictionary<string, object>
                                {
                                    ["source"] = "uia",
                                    ["text"] = it.Name,
                                    ["type"] = it.Type,
                                    ["x"] = it.Rect.X, ["y"] = it.Rect.Y,
                                    ["w"] = it.Rect.Width, ["h"] = it.Rect.Height,
                                    ["cx"] = it.CenterX, ["cy"] = it.CenterY,
                                });
                            }
                            try
                            {
                                var scr = fWin != null ? fWin.Rect : (NativeInput.PrimaryBounds());
                                using (var bmp = CaptureRegionBitmap(scr, 0, out var eff))
                                {
                                    // OCR adaptif (v2.6.2): kedua mode
                                    // dijalankan, yang lebih kaya menang.
                                    var ocrBiasa = OcrReader.Read(bmp, eff.X, eff.Y);
                                    var ocrKuat = OcrReader.ReadEnhanced(bmp, eff.X, eff.Y);
                                    var hasil = (ocrKuat.Words.Count * 1000 + (ocrKuat.Text != null ? ocrKuat.Text.Length : 0)) >
                                                (ocrBiasa.Words.Count * 1000 + (ocrBiasa.Text != null ? ocrBiasa.Text.Length : 0))
                                        ? ocrKuat : ocrBiasa;
                                    var words = hasil.Words;
                                    string ql = q.Trim();
                                    bool phrase = ql.Contains(" ");
                                    for (int i = 0; i < words.Count; i++)
                                    {
                                        Rectangle? hit = null;
                                        string hitText = null;
                                        if (!phrase)
                                        {
                                            if (words[i].Text.IndexOf(ql, StringComparison.OrdinalIgnoreCase) >= 0)
                                            { hit = words[i].Rect; hitText = words[i].Text; }
                                        }
                                        else
                                        {
                                            string acc = words[i].Text;
                                            var uni = words[i].Rect;
                                            for (int j = i + 1; j < words.Count && j <= i + 8; j++)
                                            {
                                                if (Math.Abs(words[j].Rect.Y - words[i].Rect.Y) > Math.Max(words[i].Rect.Height, words[j].Rect.Height)) break;
                                                acc += " " + words[j].Text;
                                                uni = Rectangle.Union(uni, words[j].Rect);
                                                if (acc.IndexOf(ql, StringComparison.OrdinalIgnoreCase) >= 0)
                                                { hit = uni; hitText = acc; break; }
                                            }
                                            if (hit == null && acc.IndexOf(ql, StringComparison.OrdinalIgnoreCase) >= 0)
                                            { hit = uni; hitText = acc; }
                                        }
                                        if (hit != null)
                                        {
                                            ocrCount++;
                                            var r = hit.Value;
                                            matches.Add(new Dictionary<string, object>
                                            {
                                                ["source"] = "ocr",
                                                ["text"] = hitText,
                                                ["x"] = r.X, ["y"] = r.Y,
                                                ["w"] = r.Width, ["h"] = r.Height,
                                                ["cx"] = r.X + r.Width / 2, ["cy"] = r.Y + r.Height / 2,
                                            });
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _log("find OCR gagal: " + ex.Message);
                            }
                            bool clicked = false;
                            if (matches.Count > fMax) matches.RemoveRange(fMax, matches.Count - fMax);
                            if (fClick && matches.Count > 0)
                            {
                                NativeInput.Click((int)matches[0]["cx"], (int)matches[0]["cy"], "left");
                                clicked = true;
                            }
                            if (fWin != null)
                            {
                                foreach (var m in matches)
                                {
                                    m["x"] = (int)m["x"] - relOx;
                                    m["y"] = (int)m["y"] - relOy;
                                    m["cx"] = (int)m["cx"] - relOx;
                                    m["cy"] = (int)m["cy"] - relOy;
                                }
                            }
                            _log($"find '{q}': {matches.Count} cocok (uia {uiaCount}, ocr {ocrCount})" + (clicked ? ", pertama diklik" : "") + (fWin != null ? $" (relatif: {fWin.Title})" : ""));
                            var findResp = new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["count"] = matches.Count,
                                ["clicked"] = clicked,
                                ["matches"] = matches,
                            };
                            if (fWin != null)
                            {
                                findResp["relativeTo"] = fWin.Title;
                                findResp["window"] = new Dictionary<string, object>
                                {
                                    ["title"] = fWin.Title, ["process"] = fWin.Process,
                                    ["x"] = fWin.Rect.X, ["y"] = fWin.Rect.Y,
                                    ["w"] = fWin.Rect.Width, ["h"] = fWin.Rect.Height,
                                };
                            }
                            return Response.JsonObject(findResp);
                        }
                        case "/listen":
                        {
                            // Dengar audio PC (v2.5.0): rekam loopback
                            // WASAPI selama {seconds} detik (bawaan 5,
                            // maks 30) → WAV mono 16kHz mentah.
                            int detik = GetInt(root, "seconds", 5);
                            if (detik < 1) detik = 1;
                            if (detik > 30) detik = 30;
                            // Sumber suara (v2.7.0): "system" = loopback
                            // (suara yang dimainkan PC — perilaku sejak
                            // v2.5.0), "mic" = mikrofon perangkat
                            // tangkap bawaan Windows.
                            string sumber = GetString(root, "source", "system");
                            if (sumber != "system" && sumber != "mic")
                                return Response.Json("{\"ok\":false,\"error\":\"source harus system atau mic\"}", 400);
                            _onActivity();
                            _log(sumber == "mic" ? $"dengar mikrofon ({detik} detik)…" : $"dengar audio PC ({detik} detik)…");
                            byte[] wav;
                            try
                            {
                                wav = sumber == "mic"
                                    ? AudioTools.CaptureMicWav(detik)
                                    : AudioTools.CaptureLoopbackWav(detik);
                            }
                            catch (Exception ex)
                            {
                                _log("rekam audio gagal: " + ex.Message);
                                return Response.Json("{\"ok\":false,\"error\":\"rekam audio gagal: " +
                                    ex.Message.Replace("\\", " ").Replace("\"", " ") + "\"}", 500);
                            }
                            _log($"rekaman selesai ({wav.Length} byte)");
                            // Transkrip Whisper GPU (v5.5.0):
                            // {transcribe:true} mengembalikan TEKS
                            // (JSON) alih-alih WAV mentah — Whisper
                            // paham bahasa Indonesia. Gagal sidecar
                            // -> 503 jujur; panggil ulang tanpa
                            // transcribe untuk WAV mentahnya.
                            if (GetBool(root, "transcribe", false))
                            {
                                var rawL = AiBridge.TranscribeJson(wav, out var lErr);
                                if (rawL == null)
                                    return Response.Json("{\"ok\":false,\"error\":\"transkrip GPU tidak tersedia: " +
                                        lErr.Replace("\\", " ").Replace("\"", " ") + "\"}", 503);
                                using var docL = JsonDocument.Parse(rawL);
                                var rl = docL.RootElement;
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = true,
                                    ["engine"] = "whisper-gpu",
                                    ["text"] = rl.TryGetProperty("text", out var txL) ? txL.GetString() ?? "" : "",
                                    ["language"] = rl.TryGetProperty("language", out var lgL) ? lgL.GetString() ?? "" : "",
                                    ["ms"] = rl.TryGetProperty("ms", out var msL) ? msL.GetInt64() : -1,
                                    ["source"] = sumber,
                                    ["seconds"] = detik,
                                });
                            }
                            return new Response { ContentType = "audio/wav", BodyBytes = wav };
                        }
                        case "/say":
                        {
                            // Bicara lewat speaker PC (v2.5.0): TTS
                            // bawaan Windows (SAPI).
                            string ucap = GetString(root, "text", "");
                            if (string.IsNullOrWhiteSpace(ucap))
                                return Response.Json("{\"ok\":false,\"error\":\"text kosong\"}", 400);
                            if (ucap.Length > 500) ucap = ucap.Substring(0, 500);
                            int rate = GetInt(root, "rate", 0);
                            _onActivity();
                            _log($"bicara: \"{ucap}\"");
                            string err = AudioTools.Speak(ucap, rate);
                            if (!string.IsNullOrEmpty(err))
                                return Response.Json("{\"ok\":false,\"error\":\"" +
                                    err.Replace("\\", " ").Replace("\"", " ") + "\"}", 500);
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["spoken"] = ucap,
                            });
                        }
                        case "/theme":
                        {
                            // Ganti tema tampilan (v2.5.0):
                            // {choice: device|light|dark}.
                            string choice = GetString(root, "choice", "");
                            if (choice != "device" && choice != "light" && choice != "dark")
                                return Response.Json("{\"ok\":false,\"error\":\"choice harus device, light, atau dark\"}", 400);
                            _onActivity();
                            _ui.SetTheme(choice);
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["theme"] = choice,
                                ["dark"] = UiThemeState.IsDark,
                            });
                        }
                        case "/keepawake":
                        {
                            // Saklar Jangan tidur lewat API (v2.5.0) —
                            // logika sama dengan saklar di Pengaturan.
                            bool on = GetBool(root, "on", false);
                            _onActivity();
                            _ui.ApplyKeepAwake(on);
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["keepAwake"] = NativeInput.KeepAwakeOn,
                            });
                        }
                        case "/window":
                        {
                            // Kendali jendela berdasarkan judul (v2.4.0):
                            // action = close/minimize/maximize/restore.
                            string query = GetString(root, "title", "");
                            string action = GetString(root, "action", "");
                            // Penargetan hwnd (v3.8.0 — DSA-01): bila
                            // "hwnd" dikirim, identitas itu yang
                            // dipakai (judul jadi cadangan) — jendela
                            // berjudul kembar tak lagi ambigu.
                            long hwndVal = GetLong(root, "hwnd", 0);
                            if (string.IsNullOrWhiteSpace(query) && hwndVal == 0)
                                return Response.Json("{\"ok\":false,\"error\":\"title kosong (atau kirim hwnd)\"}", 400);
                            if (action != "close" && action != "minimize" && action != "maximize" && action != "restore" && action != "rect")
                                return Response.Json("{\"ok\":false,\"error\":\"action harus close/minimize/maximize/restore/rect\"}", 400);
                            var wins = OnUi(NativeInput.ListWindows);
                            NativeInput.WindowInfo match = null;
                            foreach (var w in wins)
                            {
                                if (hwndVal != 0)
                                {
                                    if (w.Handle == (IntPtr)hwndVal) { match = w; break; }
                                }
                                else if (w.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    match = w;
                                    break;
                                }
                            }
                            if (match == null)
                                return Response.Json("{\"ok\":false,\"error\":\"jendela tidak ditemukan\"}", 404);
                            var m = match;
                            bool sent;
                            if (action == "rect")
                            {
                                // Atur posisi & ukuran persis angka
                                // (v3.5.0 — Window Manager): jendela
                                // dipulihkan dulu dari maximize agar
                                // ukuran baru berlaku.
                                if (!HasInt(root, "x") || !HasInt(root, "y") || !HasInt(root, "w") || !HasInt(root, "h"))
                                    return MissingField("x/y/w/h");
                                int wx = GetInt(root, "x", 0), wy = GetInt(root, "y", 0);
                                int ww = GetInt(root, "w", 0), wh = GetInt(root, "h", 0);
                                sent = OnUi(() =>
                                {
                                    NativeInput.WindowAction(m.Handle, "restore");
                                    return NativeInput.MoveWindowRect(m.Handle, wx, wy, ww, wh);
                                });
                            }
                            else
                            {
                                sent = OnUi(() => NativeInput.WindowAction(m.Handle, action));
                            }
                            _onActivity();
                            _log($"jendela '{m.Title}': {action}");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = sent,
                                ["window"] = m.Title,
                                ["hwnd"] = m.Handle.ToInt64(),
                                ["action"] = action,
                            });
                        }
                        case "/scroll":
                        {
                            if (!HasInt(root, "x") || !HasInt(root, "y") || !HasInt(root, "delta")) return MissingField("x/y/delta");
                            int x = GetInt(root, "x", 0), y = GetInt(root, "y", 0);
                            int delta = GetInt(root, "delta", 0);
                            // v5.10.0: axis "x" = menyamping
                            // (HWHEEL); smooth = dipecah langkah
                            // kecil lokal. Bawaan: lama persis.
                            bool horiz = GetString(root, "axis", "y") == "x";
                            bool smooth = GetBool(root, "smooth", false);
                            // v5.11.0 (Bagian A.4): scroll halus
                            // dijepit total ±4800 (40 klik roda)
                            // — delta ekstrem tidak lagi membuat
                            // UI sibuk puluhan detik.
                            if (smooth)
                            {
                                if (delta > 4800) delta = 4800;
                                if (delta < -4800) delta = -4800;
                            }
                            OnUi(() =>
                            {
                                if (smooth) NativeInput.ScrollSmooth(x, y, delta, horiz);
                                else if (horiz) NativeInput.ScrollH(x, y, delta);
                                else NativeInput.Scroll(x, y, delta);
                                return true;
                            });
                            _onActivity();
                            _log($"scroll di {x},{y} (delta {delta}, sumbu {(horiz ? "x" : "y")}{(smooth ? ", halus" : "")})");
                            return Ok();
                        }
                        case "/drag":
                        {
                            if (!HasInt(root, "x1") || !HasInt(root, "y1") || !HasInt(root, "x2") || !HasInt(root, "y2")) return MissingField("x1/y1/x2/y2");
                            int x1 = GetInt(root, "x1", 0), y1 = GetInt(root, "y1", 0);
                            int x2 = GetInt(root, "x2", 0), y2 = GetInt(root, "y2", 0);
                            string button = GetString(root, "button", "left");
                            // Timing seret (v5.9.0): grabMs tahan
                            // di awal, steps/stepMs lintasan,
                            // settleMs tahan di tujuan. Bawaan =
                            // perilaku lama persis (60/16/15/60).
                            int grabMs = GetInt(root, "grabMs", 60);
                            int dragSteps = GetInt(root, "steps", 16);
                            int stepMs = GetInt(root, "stepMs", 15);
                            int settleMs = GetInt(root, "settleMs", 60);
                            // Jalur waypoints (v5.10.0): "via"
                            // [{x, y, dwellMs}] — seret menyusuri
                            // titik-titik singgah di antara awal
                            // dan tujuan; tanpa via = garis lurus
                            // seperti biasa.
                            var viaPts = new List<(int X, int Y, int DwellMs)>();
                            if (root.TryGetProperty("via", out var viaEl) && viaEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var v in viaEl.EnumerateArray())
                                {
                                    if (v.ValueKind != JsonValueKind.Object) continue;
                                    viaPts.Add((GetInt(v, "x", 0), GetInt(v, "y", 0), GetInt(v, "dwellMs", 0)));
                                }
                            }
                            bool pakaiVia = viaPts.Count > 0;
                            // v5.11.0 (Bagian A.4): jalur seret
                            // dijepit total — maks 200 titik dan
                            // total waktu diam (grab + semua
                            // dwell + settle) maks 30 detik.
                            if (pakaiVia)
                            {
                                long totalDiam = grabMs + settleMs;
                                foreach (var p in viaPts) totalDiam += Math.Max(0, p.DwellMs);
                                if (viaPts.Count > 200 || totalDiam > 30000)
                                    return Response.JsonObject(new Dictionary<string, object>
                                    {
                                        ["ok"] = false,
                                        ["error"] = "jalur seret melebihi batas (maks 200 titik, total diam maks 30 detik)",
                                    }, 400);
                            }
                            var mods = ParseModifiers(GetString(root, "modifiers", ""));
                            // Yield (v5.13.0): seret berhenti bila
                            // user menyentuh perangkat fisik di
                            // tengah jalan.
                            long dragStartTick = Environment.TickCount64;
                            bool dragDone = OnUi(() =>
                            {
                                bool selesai = true;
                                foreach (var vk in mods) NativeInput.KeyDown(vk);
                                try
                                {
                                    if (pakaiVia)
                                    {
                                        var jalur = new List<(int X, int Y, int DwellMs)> { (x1, y1, 0) };
                                        jalur.AddRange(viaPts);
                                        jalur.Add((x2, y2, 0));
                                        selesai = NativeInput.DragPath(jalur, button, grabMs, stepMs, settleMs,
                                            () => PhysicalInput.TouchedSince(dragStartTick));
                                    }
                                    else NativeInput.Drag(x1, y1, x2, y2, button, grabMs, dragSteps, stepMs, settleMs,
                                        () => PhysicalInput.TouchedSince(dragStartTick));
                                }
                                finally { for (int i = mods.Count - 1; i >= 0; i--) NativeInput.KeyUp(mods[i]); }
                                return selesai;
                            });
                            if (!dragDone || PhysicalInput.TouchedSince(dragStartTick))
                            {
                                _log($"drag {x1},{y1} -> {x2},{y2} BERHENTI: user mengambil alih (sentuhan fisik)");
                                EventLog.Add("yield", $"drag {x1},{y1} -> {x2},{y2}");
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false,
                                    ["error"] = "seret dihentikan: user mengambil alih",
                                    ["code"] = "user_preempted",
                                }, 409);
                            }
                            _onActivity();
                            _log($"drag {x1},{y1} -> {x2},{y2} ({button}{(mods.Count > 0 ? ", +penahan" : "")})");
                            // Verifikasi hasil drag (v3.5.0 — Action
                            // Verification): {expectText} -> server
                            // menunggu teks itu muncul di sekitar
                            // titik TUJUAN drag (pola /click v2.6.4).
                            string expectD = GetString(root, "expectText", "");
                            if (!string.IsNullOrWhiteSpace(expectD))
                            {
                                int expTimeout = GetInt(root, "expectTimeoutMs", 3000);
                                if (expTimeout < 250) expTimeout = 250;
                                if (expTimeout > 10000) expTimeout = 10000;
                                int expRadius = GetInt(root, "expectRadius", 400);
                                bool verified = WaitTextNear(x2, y2, expectD, expTimeout, expRadius, out int waitedD);
                                _log($"drag verifikasi '{expectD}': {(verified ? "KETEMU" : "tidak ketemu")} ({waitedD}ms)");
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = true,
                                    ["verified"] = verified,
                                    ["expectText"] = expectD,
                                    ["waitedMs"] = waitedD,
                                });
                            }
                            return Ok();
                        }
                        case "/penstroke":
                        {
                            // Goresan pena BERTEKANAN (v5.11.0 —
                            // Bagian B.9, prototipe Windows Ink):
                            // points [{x, y, p}] dengan p =
                            // tekanan 0..1024 (bawaan 700).
                            // verify=true: potret kotak goresan
                            // sebelum & sesudah, laporkan piksel
                            // yang berubah (Bagian B.10).
                            var penPts = new List<(int X, int Y, int Pressure)>();
                            if (root.TryGetProperty("points", out var penEl) && penEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var v in penEl.EnumerateArray())
                                {
                                    if (v.ValueKind != JsonValueKind.Object) continue;
                                    penPts.Add((GetInt(v, "x", 0), GetInt(v, "y", 0), GetInt(v, "p", 700)));
                                }
                            }
                            if (penPts.Count < 2 || penPts.Count > 500)
                                return Response.Json("{\"ok\":false,\"error\":\"points harus 2..500 titik\"}", 400);
                            int penStepMs = GetInt(root, "stepMs", 8);
                            bool penVerify = GetBool(root, "verify", false);
                            int bx = int.MaxValue, by = int.MaxValue, bx2 = int.MinValue, by2 = int.MinValue;
                            foreach (var p in penPts)
                            {
                                if (p.X < bx) bx = p.X; if (p.Y < by) by = p.Y;
                                if (p.X > bx2) bx2 = p.X; if (p.Y > by2) by2 = p.Y;
                            }
                            var bbox = new Rectangle(Math.Max(0, bx - 10), Math.Max(0, by - 10),
                                bx2 - bx + 21, by2 - by + 21);
                            byte[] beforeBytes = null;
                            if (penVerify)
                            {
                                using (var b0 = CaptureRegionBitmap(bbox, 900, out var _e0))
                                using (var ms0 = new MemoryStream())
                                { b0.Save(ms0, ImageFormat.Png); beforeBytes = ms0.ToArray(); }
                            }
                            string penErr = "";
                            bool penOk = OnUi(() => NativeInput.PenStroke(penPts, penStepMs, out penErr));
                            _onActivity();
                            var respPen = new Dictionary<string, object>
                            {
                                ["ok"] = penOk,
                                ["points"] = penPts.Count,
                                ["error"] = penErr,
                            };
                            if (penVerify && beforeBytes != null)
                            {
                                Thread.Sleep(120);
                                using (var b1 = CaptureRegionBitmap(bbox, 900, out var _e1))
                                using (var msB = new MemoryStream(beforeBytes))
                                using (var b0 = new Bitmap(msB))
                                {
                                    int wMin = Math.Min(b0.Width, b1.Width);
                                    int hMin = Math.Min(b0.Height, b1.Height);
                                    long changed = 0, total = (long)wMin * hMin;
                                    for (int yy = 0; yy < hMin; yy += 2)
                                        for (int xx = 0; xx < wMin; xx += 2)
                                        {
                                            var c0 = b0.GetPixel(xx, yy);
                                            var c1 = b1.GetPixel(xx, yy);
                                            if (Math.Abs(c0.R - c1.R) > 40 || Math.Abs(c0.G - c1.G) > 40 || Math.Abs(c0.B - c1.B) > 40)
                                                changed++;
                                        }
                                    long sampled = total / 4;
                                    double ratio = sampled > 0 ? (double)changed / sampled : 0;
                                    respPen["changedRatio"] = Math.Round(ratio, 4);
                                    respPen["inkDetected"] = ratio > 0.001;
                                }
                            }
                            _log($"penstroke {penPts.Count} titik: {(penOk ? "ok" : "GAGAL " + penErr)}");
                            return Response.JsonObject(respPen, penOk ? 200 : 500);
                        }
                        case "/input/lease":
                        case "/input/lease/release":
                        {
                            // Arbitrase (v5.13.0): sewa input
                            // eksklusif lintas agen. Pemegang
                            // menyebut "taskId" = owner di body
                            // panggilan inputnya (mekanisme waris
                            // lease batch v3.11.0); panggilan agen
                            // lain tertolak 429 input_leased oleh
                            // gerbang yang sudah ada.
                            string leaseOwnerReq = GetString(root, "owner", "").Trim();
                            if (leaseOwnerReq.Length < 1 || leaseOwnerReq.Length > 60)
                                return Response.Json("{\"ok\":false,\"error\":\"owner wajib 1-60 karakter\"}", 400);
                            if (req.Path == "/input/lease/release")
                            {
                                ReleaseLease(leaseOwnerReq);
                                _log("lease input dilepas: " + leaseOwnerReq);
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = true, ["leased"] = false,
                                });
                            }
                            int ttlSec = GetInt(root, "ttlSec", 120);
                            if (ttlSec < 10) ttlSec = 10;
                            if (ttlSec > 300) ttlSec = 300;
                            if (!TryAcquireLease(leaseOwnerReq, ttlSec * 1000))
                            {
                                var (pemegang, sisaGagal) = LeaseInfo();
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false,
                                    ["error"] = "input sedang disewa " + pemegang,
                                    ["code"] = "input_leased",
                                    ["leaseOwner"] = pemegang,
                                    ["remainingSec"] = sisaGagal,
                                }, 429);
                            }
                            _onActivity();
                            _log($"lease input diambil: {leaseOwnerReq} ({ttlSec} dtk)");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true, ["leased"] = true,
                                ["owner"] = leaseOwnerReq, ["ttlSec"] = ttlSec,
                            });
                        }
                        case "/reflex/rules":
                        case "/reflex/rules/delete":
                        {
                            // Refleks (v5.13.0): kelola aturan.
                            if (req.Path == "/reflex/rules/delete")
                            {
                                bool terhapus = ReflexEngine.Delete(GetString(root, "id", ""));
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = true, ["deleted"] = terhapus,
                                });
                            }
                            string trig = GetString(root, "trigger", "").Trim().ToLowerInvariant();
                            string match = GetString(root, "match", "").Trim();
                            if (trig != "window" && trig != "process" && trig != "file")
                                return Response.Json("{\"ok\":false,\"error\":\"trigger wajib window|process|file\"}", 400);
                            if (match.Length < 1 || match.Length > 260)
                                return Response.Json("{\"ok\":false,\"error\":\"match wajib 1-260 karakter\"}", 400);
                            var aturan = new ReflexEngine.Rule
                            {
                                Trigger = trig,
                                Match = match,
                                Pattern = GetString(root, "pattern", ""),
                                On = GetString(root, "on", ""),
                                Action = GetString(root, "action", "log"),
                                CooldownSec = GetInt(root, "cooldownSec", 30),
                            };
                            var tersimpan = ReflexEngine.Add(aturan);
                            _onActivity();
                            _log($"aturan refleks ditambah: {trig} '{match}' -> {aturan.Action}");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["rule"] = RuleJson(tersimpan),
                            });
                        }
                        case "/uia3/find":
                        case "/uia3/act":
                        {
                            // UIA3 lintas jendela (v5.14.0): FlaUI
                            // memang mesin /uia sejak v2.4.0 — yang
                            // baru di sini PERMUKAANNYA: pencarian
                            // di SEMUA jendela (bukan hanya depan)
                            // + tunggu-coba-ulang, modus gagal yang
                            // berbeda dari pindai jendela depan.
                            string q3 = GetString(root, "text", "");
                            // v5.18.0: "name" diterima sebagai
                            // alias "text" (konvensi /uia/find) —
                            // dulu diam-diam diabaikan sehingga
                            // saringan nama tidak berlaku sama
                            // sekali (temuan Ronde 9).
                            if (q3.Length == 0) q3 = GetString(root, "name", "");
                            string aid3 = GetString(root, "automationId", "");
                            string win3 = GetString(root, "window", "");
                            string proc3 = GetString(root, "process", "");
                            int tmo3 = GetInt(root, "timeoutMs", 3000);
                            if (tmo3 < 0) tmo3 = 0;
                            if (tmo3 > 20000) tmo3 = 20000;
                            if (req.Path == "/uia3/find")
                            {
                                var hits3 = OnUi(() => UiaScan.FindAcrossWindows(q3, aid3, win3, proc3, tmo3));
                                var daftar3 = new List<Dictionary<string, object>>();
                                foreach (var h in hits3)
                                {
                                    daftar3.Add(new Dictionary<string, object>
                                    {
                                        ["name"] = h.Name, ["type"] = h.Type,
                                        ["automationId"] = h.AutomationId,
                                        ["window"] = h.WindowTitle, ["process"] = h.Process,
                                        ["x"] = h.Rect.X, ["y"] = h.Rect.Y,
                                        ["w"] = h.Rect.Width, ["h"] = h.Rect.Height,
                                        ["cx"] = h.CenterX, ["cy"] = h.CenterY,
                                        ["offscreen"] = h.Offscreen,
                                    });
                                }
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = true, ["count"] = daftar3.Count, ["hits"] = daftar3,
                                });
                            }
                            string act3 = GetString(root, "action", "invoke");
                            string val3 = GetString(root, "value", "");
                            var oc3 = OnUi(() => UiaScan.ActAcrossWindows(q3, aid3, win3, proc3, act3, val3, tmo3));
                            _onActivity();
                            _log($"uia3 {act3} '{q3}': {(oc3.Ok ? "OK" : oc3.Error)}");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = oc3.Ok,
                                ["element"] = oc3.ElementName,
                                ["type"] = oc3.ElementType,
                                ["window"] = oc3.WindowTitle,
                                ["value"] = oc3.Value,
                                ["error"] = oc3.Ok ? "" : oc3.Error,
                            }, oc3.Ok ? 200 : 422);
                        }
                        case "/act":
                        {
                            // Mesin pemulihan (v5.14.0): tangga
                            // lintas-jalur — coba UIA depan, lalu
                            // UIA3 lintas jendela, lalu visual
                            // /locate, terakhir OCR murni; tiap
                            // anak tangga diverifikasi (expectText
                            // muncul di sekitar titik aksi) bila
                            // diminta. Semua percobaan dilaporkan.
                            // v5.18.0: tangga BERHENTI di anak
                            // tangga pertama yang benar-benar
                            // bertindak tetapi gagal verifikasi
                            // (kode "acted_unverified") — dulu
                            // tangga terus naik dan mengulang
                            // aksi yang sama (Ronde 9: satu
                            // checkbox ter-toggle 4x).
                            string target = GetString(root, "text", "").Trim();
                            if (target.Length < 1)
                                return Response.Json("{\"ok\":false,\"error\":\"text wajib diisi\"}", 400);
                            string actAction = GetString(root, "action", "invoke");
                            string actValue = GetString(root, "value", "");
                            string expect = GetString(root, "expectText", "");
                            string winF = GetString(root, "window", "");
                            int verifyMs = GetInt(root, "verifyTimeoutMs", 4000);
                            if (verifyMs < 500) verifyMs = 500;
                            if (verifyMs > 15000) verifyMs = 15000;
                            var attempts = new List<Dictionary<string, object>>();
                            // Pembangun respons pengaman v5.18.0:
                            // aksi sudah terlanjur dijalankan di
                            // satu anak tangga tetapi verifikasi
                            // gagal — laporkan apa adanya dan
                            // jangan bertindak lagi.
                            Func<string, Dictionary<string, object>> actedResp =
                                (rungName) => new Dictionary<string, object>
                                {
                                    ["ok"] = false, ["acted"] = true, ["rung"] = rungName,
                                    ["code"] = "acted_unverified",
                                    ["error"] = "aksi sudah dijalankan di anak tangga " + rungName +
                                        " tetapi verifikasi gagal; tangga dihentikan agar aksi tidak terulang",
                                    ["attempts"] = attempts,
                                };
                            // Anak tangga 1: UIA jendela depan.
                            var ocA = OnUi(() => UiaScan.Act(null, null, "", target, -1, actAction, actValue));
                            bool okA = ocA.Ok && VerifyAct(ocA.CenterX, ocA.CenterY, expect, verifyMs);
                            attempts.Add(new Dictionary<string, object>
                            {
                                ["rung"] = "uia", ["ok"] = okA,
                                ["detail"] = ocA.Ok ? (okA ? "aksi + verifikasi lulus" : "aksi jalan, verifikasi gagal") : ocA.Error,
                            });
                            if (okA)
                            {
                                _onActivity();
                                _log($"act '{target}' berhasil di anak tangga uia");
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = true, ["rung"] = "uia",
                                    ["element"] = ocA.ElementName, ["verified"] = !string.IsNullOrWhiteSpace(expect),
                                    ["attempts"] = attempts,
                                });
                            }
                            if (ocA.Ok)
                            {
                                _log($"act '{target}' berhenti di uia: aksi jalan, verifikasi gagal");
                                var respA = actedResp("uia");
                                respA["element"] = ocA.ElementName;
                                return Response.JsonObject(respA, 422);
                            }
                            // Anak tangga 2: UIA3 lintas jendela.
                            var ocB = OnUi(() => UiaScan.ActAcrossWindows(target, "", winF, "", actAction, actValue, 3000));
                            bool okB = ocB.Ok && VerifyAct(ocB.CenterX, ocB.CenterY, expect, verifyMs);
                            attempts.Add(new Dictionary<string, object>
                            {
                                ["rung"] = "uia3", ["ok"] = okB,
                                ["detail"] = ocB.Ok ? (okB ? "aksi + verifikasi lulus" : "aksi jalan, verifikasi gagal") : ocB.Error,
                            });
                            if (okB)
                            {
                                _onActivity();
                                _log($"act '{target}' berhasil di anak tangga uia3 ({ocB.WindowTitle})");
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = true, ["rung"] = "uia3",
                                    ["element"] = ocB.ElementName, ["window"] = ocB.WindowTitle,
                                    ["verified"] = !string.IsNullOrWhiteSpace(expect),
                                    ["attempts"] = attempts,
                                });
                            }
                            if (ocB.Ok)
                            {
                                _log($"act '{target}' berhenti di uia3: aksi jalan, verifikasi gagal");
                                var respB = actedResp("uia3");
                                respB["element"] = ocB.ElementName;
                                respB["window"] = ocB.WindowTitle;
                                return Response.JsonObject(respB, 422);
                            }
                            // Anak tangga 3 & 4: visual /locate
                            // (rantai penuh), lalu OCR murni —
                            // klik koordinat kandidat pertama.
                            foreach (var (rungName, locMode) in new[] { ("locate", ""), ("ocr", "ocr") })
                            {
                                string locBody = locMode == ""
                                    ? "{\"text\":" + JsonSerializer.Serialize(target) + "}"
                                    : "{\"text\":" + JsonSerializer.Serialize(target) + ",\"mode\":\"ocr\"}";
                                var locResp = Route(new Request { Method = "POST", Path = "/locate", Query = "", Body = locBody });
                                int hx = 0, hy = 0;
                                bool ada = false;
                                string viaLoc = "";
                                try
                                {
                                    using var dokL = JsonDocument.Parse(Encoding.UTF8.GetString(locResp.BodyBytes));
                                    if (dokL.RootElement.TryGetProperty("candidates", out var cands) &&
                                        cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0)
                                    {
                                        var c0 = cands[0];
                                        hx = c0.TryGetProperty("cx", out var exx) ? exx.GetInt32() : 0;
                                        hy = c0.TryGetProperty("cy", out var eyy) ? eyy.GetInt32() : 0;
                                        viaLoc = c0.TryGetProperty("via", out var evv) ? evv.GetString() ?? "" : "";
                                        ada = hx > 0 || hy > 0;
                                    }
                                }
                                catch { /* respons locate tak terbaca — anak tangga gagal */ }
                                if (!ada)
                                {
                                    attempts.Add(new Dictionary<string, object>
                                    {
                                        ["rung"] = rungName, ["ok"] = false, ["detail"] = "target tidak ditemukan",
                                    });
                                    continue;
                                }
                                int fx = hx, fy = hy;
                                OnUi(() => { NativeInput.Click(fx, fy, "left"); return true; });
                                bool okV = VerifyAct(fx, fy, expect, verifyMs);
                                attempts.Add(new Dictionary<string, object>
                                {
                                    ["rung"] = rungName, ["ok"] = okV,
                                    ["detail"] = "klik " + fx + "," + fy + " via " + viaLoc + (okV ? "" : " — verifikasi gagal"),
                                });
                                if (okV)
                                {
                                    _onActivity();
                                    _log($"act '{target}' berhasil di anak tangga {rungName} ({fx},{fy})");
                                    return Response.JsonObject(new Dictionary<string, object>
                                    {
                                        ["ok"] = true, ["rung"] = rungName,
                                        ["x"] = fx, ["y"] = fy, ["via"] = viaLoc,
                                        ["verified"] = !string.IsNullOrWhiteSpace(expect),
                                        ["attempts"] = attempts,
                                    });
                                }
                                // Klik fisik SUDAH terjadi di titik
                                // ini; verifikasi yang gagal bukan
                                // alasan mengklik lagi dari anak
                                // tangga berikutnya (v5.18.0).
                                _log($"act '{target}' berhenti di {rungName}: klik jalan, verifikasi gagal");
                                var respV = actedResp(rungName);
                                respV["x"] = fx; respV["y"] = fy; respV["via"] = viaLoc;
                                return Response.JsonObject(respV, 422);
                            }
                            _log($"act '{target}' GAGAL di semua anak tangga");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = false,
                                ["error"] = "semua anak tangga gagal",
                                ["code"] = "act_failed",
                                ["attempts"] = attempts,
                            }, 422);
                        }
                        case "/pw/open":
                        case "/pw/goto":
                        case "/pw/click":
                        case "/pw/fill":
                        case "/pw/text":
                        case "/pw/wait":
                        case "/pw/screenshot":
                        case "/pw/close":
                        {
                            // Playwright (v5.14.0): teruskan ke
                            // sidecar Python (Chrome terpasang,
                            // profil otomasi sendiri). Kegagalan
                            // sidecar = 502 jujur; pemanggil boleh
                            // mundur ke /browser (CDP dasar).
                            // v5.15.0: /pw/open tanpa url ditolak
                            // di sini — dulu pesannya baru muncul
                            // dari sidecar SESUDAH Chrome
                            // diluncurkan (lambat & membingungkan).
                            if (req.Path == "/pw/open" &&
                                string.IsNullOrWhiteSpace(GetString(root, "url", "")))
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false,
                                    ["error"] = "url wajib diisi untuk /pw/open",
                                    ["code"] = "url_required",
                                }, 400);
                            string pwRaw = PwBridge.Call(req.Path.Substring(3), req.Body, out var pwErr);
                            if (pwRaw == null)
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false,
                                    ["error"] = pwErr,
                                    ["code"] = "pw_unavailable",
                                }, 502);
                            _onActivity();
                            return Response.Json(pwRaw);
                        }
                        case "/task/start":
                        case "/task/note":
                        case "/task/done":
                        {
                            // Task Memory & Context (v5.12.0).
                            if (req.Path == "/task/start")
                            {
                                TaskContext.Start(GetString(root, "title", ""));
                                _log("tugas dibuka: " + GetString(root, "title", ""));
                            }
                            else if (req.Path == "/task/note")
                            {
                                TaskContext.Note(GetString(root, "text", ""));
                            }
                            else
                            {
                                TaskContext.Done();
                                _log("tugas ditutup");
                            }
                            _onActivity();
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["task"] = TaskContext.Snapshot(),
                            });
                        }
                        case "/type":
                        {
                            string text = GetString(root, "text", "");
                            // Mesin Unicode SendInput sejak v2.4.0 —
                            // SendKeys lama tidak stabil untuk karakter
                            // di luar tata letak keyboard aktif.
                            // v3.11.0 (DSA-16): ketik per potongan
                            // ±20 karakter — satu OnUi per potongan
                            // agar operasi UI lain bisa menyela di
                            // antara potongan, bukan menunggu seluruh
                            // teks panjang selesai.
                            // rateMs (v5.10.0): jeda per karakter
                            // 0–50; tanpa bidang = 15 (ritme aman
                            // lama). 0 = geber — risiko aplikasi
                            // TSF rewel ditanggung pemanggil.
                            int rateMs = GetInt(root, "rateMs", 15);
                            if (rateMs < 0) rateMs = 0;
                            if (rateMs > 50) rateMs = 50;
                            // v5.11.0 (Bagian A.3): batas panjang
                            // wajar — teks raksasa dalam satu
                            // panggilan ditolak, pecah oleh
                            // pemanggil. Dulu tanpa batas sama
                            // sekali.
                            if (text.Length > 20000)
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false,
                                    ["error"] = "teks melebihi batas 20000 karakter per panggilan",
                                }, 400);
                            // v5.12.0: /type PINTAR. mode "auto"
                            // (bawaan): bila jendela depan aplikasi
                            // rewel TSF (Notepad Win11 — terbukti
                            // merusak ketikan tombol, vonis
                            // eksperimen belah 26 Nov), teks dikirim
                            // lewat jalur TEMPEL (clipboard
                            // dipulihkan sesudahnya) agar persis.
                            // mode "paste" memaksa tempel; "keys"
                            // memaksa tombol. rateMs hanya berlaku
                            // untuk jalur tombol.
                            string typeMode = GetString(root, "mode", "auto");
                            bool usePaste = typeMode == "paste" ||
                                (typeMode != "keys" && IsFussyTextAppForeground());
                            if (usePaste)
                            {
                                if (_inputHalted)
                                    return Response.JsonObject(new Dictionary<string, object>
                                    {
                                        ["ok"] = false,
                                        ["error"] = "pengetikan dihentikan Emergency Stop",
                                        ["code"] = "input_halted",
                                        ["typedChars"] = 0,
                                    }, 409);
                                OnUi(() => { PasteTextInternal(text); return true; });
                                _onActivity();
                                _log($"ketik (tempel): {text.Length} karakter");
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = true,
                                    ["via"] = "paste",
                                    ["chars"] = text.Length,
                                });
                            }
                            // v5.11.0 (Bagian A.2+A.3): potongan
                            // diperkecil 20 -> 12 karakter + jeda
                            // antar-potongan 40 ms agar tumpukan
                            // teks (TSF) aplikasi tujuan sempat
                            // terkuras — obat anomali kerusakan
                            // karakter pada teks panjang (audit
                            // 26 Nov: selalu di indeks yang sama
                            // pada rate 15, hilang di rate 30).
                            // Emergency Stop kini DIPERIKSA di
                            // tiap potongan: pengetikan berhenti
                            // di tengah, bukan berjalan sampai
                            // habis sesudah gerbang ditutup.
                            // Yield (v5.13.0): sentuhan fisik user
                            // sesudah ketik mulai menghentikan
                            // ketikan di batas potongan berikutnya.
                            long typeStartTick = Environment.TickCount64;
                            for (int off = 0; off < text.Length; off += 12)
                            {
                                if (PhysicalInput.TouchedSince(typeStartTick))
                                {
                                    _log($"ketik DIBATALKAN user mengambil alih di {off}/{text.Length} karakter");
                                    EventLog.Add("yield", $"ketik {off}/{text.Length}");
                                    return Response.JsonObject(new Dictionary<string, object>
                                    {
                                        ["ok"] = false,
                                        ["error"] = "pengetikan dihentikan: user mengambil alih",
                                        ["code"] = "user_preempted",
                                        ["typedChars"] = off,
                                    }, 409);
                                }
                                if (_inputHalted)
                                {
                                    _log($"ketik DIBATALKAN Emergency Stop di {off}/{text.Length} karakter");
                                    return Response.JsonObject(new Dictionary<string, object>
                                    {
                                        ["ok"] = false,
                                        ["error"] = "pengetikan dihentikan Emergency Stop",
                                        ["code"] = "input_halted",
                                        ["typedChars"] = off,
                                    }, 409);
                                }
                                string potongan = text.Substring(off, Math.Min(12, text.Length - off));
                                OnUi(() => { NativeInput.TypeUnicode(potongan, rateMs); return true; });
                                if (off + 12 < text.Length) Thread.Sleep(40);
                            }
                            _onActivity();
                            string preview = text.Length > 30 ? text.Substring(0, 30) + "…" : text;
                            _log($"ketik: {preview} ({text.Length} karakter)");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["via"] = "keys",
                                ["chars"] = text.Length,
                            });
                        }
                        case "/key":
                        {
                            string keys = GetString(root, "keys", "");
                            // scan (v5.10.0): mode game — kejadian
                            // dikirim sebagai scan code fisik
                            // (sintaks "ctrl+shift+esc"), bukan
                            // kode virtual. Untuk game yang cuek
                            // ke suntikan VK biasa.
                            bool scanKey = GetBool(root, "scan", false);
                            // holdMs (v5.11.0 — Bagian A.5):
                            // jeda tahan tiap ketukan 0–5000,
                            // untuk game polling yang kehilangan
                            // ketukan sekejap.
                            int holdMs = GetInt(root, "holdMs", 0);
                            try
                            {
                                OnUi(() =>
                                {
                                    if (scanKey) NativeInput.PressKeyScan(keys, holdMs);
                                    else NativeInput.PressKey(keys, holdMs);
                                    return true;
                                });
                            }
                            catch (ArgumentException ex)
                            {
                                // v3.6.0 (BUG-05): nama tombol tak
                                // dikenal kini 400 — bukan diketik
                                // literal ke jendela user.
                                return Response.JsonObject(new Dictionary<string, object>
                                {
                                    ["ok"] = false,
                                    ["error"] = ex.Message,
                                }, 400);
                            }
                            _onActivity();
                            _log("tombol: " + keys);
                            return Ok();
                        }
                        case "/clipboard":
                        {
                            string text = GetString(root, "text", "");
                            OnUi(() => { SetClipboardText(text); return true; });
                            _onActivity();
                            // Isi clipboard TIDAK PERNAH dicatat — hanya panjangnya.
                            _log($"clipboard diisi ({text.Length} karakter)");
                            return Ok();
                        }
                        case "/paste":
                        {
                            string text = GetString(root, "text", "");
                            // keepClipboard (v5.11.0): true =
                            // biarkan teks tempel menetap di
                            // clipboard (perilaku lama). Bawaan
                            // kini FALSE: isi clipboard user
                            // disimpan dulu & dipulihkan sesudah
                            // tempel (Bagian A.7).
                            bool keepClip = GetBool(root, "keepClipboard", false);
                            OnUi(() =>
                            {
                                // Potret clipboard PER FORMAT
                                // (v5.11.1): objek IDataObject
                                // mentah dari GetDataObject tidak
                                // andal dipasang ulang (terbukti
                                // clipboard jadi KOSONG sesudah
                                // restore di v5.11.0) — simpan
                                // DATA konkretnya, prioritas
                                // FileDrop > gambar > teks.
                                System.Collections.Specialized.StringCollection savedFiles = null;
                                System.Windows.Media.Imaging.BitmapSource savedImage = null;
                                string savedText = null;
                                bool hasSaved = false;
                                if (!keepClip)
                                {
                                    try
                                    {
                                        if (System.Windows.Clipboard.ContainsFileDropList())
                                        {
                                            savedFiles = System.Windows.Clipboard.GetFileDropList();
                                            hasSaved = savedFiles != null && savedFiles.Count > 0;
                                        }
                                        else if (System.Windows.Clipboard.ContainsImage())
                                        {
                                            savedImage = System.Windows.Clipboard.GetImage();
                                            hasSaved = savedImage != null;
                                        }
                                        else if (System.Windows.Clipboard.ContainsText())
                                        {
                                            savedText = System.Windows.Clipboard.GetText();
                                            hasSaved = savedText != null;
                                        }
                                    }
                                    catch { hasSaved = false; }
                                }
                                SetClipboardText(text);
                                NativeInput.PressKey("^v"); // Ctrl+V di jendela yang sedang fokus
                                if (hasSaved)
                                {
                                    // Beri aplikasi tujuan waktu
                                    // membaca clipboard sebelum
                                    // isinya dipulihkan.
                                    Thread.Sleep(250);
                                    try
                                    {
                                        var data = new System.Windows.DataObject();
                                        if (savedFiles != null) data.SetFileDropList(savedFiles);
                                        else if (savedImage != null) data.SetImage(savedImage);
                                        else data.SetText(savedText);
                                        System.Windows.Clipboard.SetDataObject(data, true);
                                    }
                                    catch { /* clipboard gagal dipulihkan — tempel tetap sah */ }
                                }
                                return true;
                            });
                            _onActivity();
                            _log($"tempel teks ({text.Length} karakter)");
                            return Ok();
                        }
                        case "/clipboard/image":
                        {
                            // Taruh gambar PNG ke clipboard
                            // (v5.10.0). Isi tidak pernah dicatat —
                            // hanya ukurannya.
                            string b64 = GetString(root, "pngBase64", "");
                            byte[] pngBytes;
                            try { pngBytes = Convert.FromBase64String(b64); }
                            catch { return Response.Json("{\"ok\":false,\"error\":\"pngBase64 tidak valid\"}", 400); }
                            if (pngBytes.Length == 0 || pngBytes.Length > 16 * 1024 * 1024)
                                return Response.Json("{\"ok\":false,\"error\":\"ukuran gambar di luar batas\"}", 400);
                            bool okImg = OnUi(() =>
                            {
                                try
                                {
                                    using (var ms = new MemoryStream(pngBytes))
                                    {
                                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                                        bmp.BeginInit();
                                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                        bmp.StreamSource = ms;
                                        bmp.EndInit();
                                        bmp.Freeze();
                                        System.Windows.Clipboard.SetImage(bmp);
                                    }
                                    return true;
                                }
                                catch { return false; }
                            });
                            _onActivity();
                            _log($"clipboard gambar diisi ({pngBytes.Length} byte)");
                            return okImg ? Ok() : Response.Json("{\"ok\":false,\"error\":\"gagal menaruh gambar ke clipboard\"}", 500);
                        }
                        case "/record/start":
                        {
                            // Rekam makro (v5.10.0) — HANYA mulai
                            // atas panggilan eksplisit ini.
                            string recName = GetString(root, "name", "makro");
                            if (MacroRecorder.IsPlaying)
                                return Response.Json("{\"ok\":false,\"error\":\"sedang memutar makro\"}", 409);
                            if (!MacroRecorder.StartRecording(recName))
                                return Response.Json("{\"ok\":false,\"error\":\"sudah merekam atau hook gagal terpasang\"}", 409);
                            _onActivity();
                            _log("rekam makro MULAI: " + MacroRecorder.RecordingName);
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true, ["recording"] = true, ["name"] = MacroRecorder.RecordingName,
                            });
                        }
                        case "/record/stop":
                        {
                            // Berhenti merekam & simpan. Endpoint
                            // ini SENGAJA di luar InputPaths:
                            // berhenti tidak boleh terblokir
                            // Emergency Stop.
                            var mf = MacroRecorder.StopRecording();
                            if (mf == null)
                                return Response.Json("{\"ok\":false,\"error\":\"tidak sedang merekam\"}", 409);
                            _onActivity();
                            _log($"rekam makro STOP: {mf.Name} ({mf.Events.Count} kejadian, {mf.DurationMs}ms{(mf.Truncated ? ", terpotong batas" : "")})");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true, ["name"] = mf.Name, ["events"] = mf.Events.Count,
                                ["durationMs"] = mf.DurationMs, ["truncated"] = mf.Truncated,
                            });
                        }
                        case "/macros/run":
                        {
                            string mName = GetString(root, "name", "");
                            double mSpeed = 1.0;
                            if (root.TryGetProperty("speed", out var spEl) && spEl.ValueKind == JsonValueKind.Number)
                                mSpeed = spEl.GetDouble();
                            var macro = MacroRecorder.LoadMacro(mName);
                            if (macro == null)
                                return Response.Json("{\"ok\":false,\"error\":\"makro tidak ditemukan\"}", 404);
                            // Putar ulang di utas MacroRecorder;
                            // lambda halt membaca Emergency Stop.
                            // Yield (v5.13.0): sentuhan FISIK user
                            // sesudah putar mulai menghentikan
                            // putaran; alasan selesai tercatat.
                            long playStartTick = Environment.TickCount64;
                            if (!MacroRecorder.Play(macro, mSpeed, () => _inputHalted,
                                () => PhysicalInput.TouchedSince(playStartTick),
                                (reason) =>
                                {
                                    if (reason == "preempted")
                                    {
                                        _log($"putar makro '{macro.Name}' BERHENTI: user mengambil alih (sentuhan fisik)");
                                        EventLog.Add("yield", "makro " + macro.Name);
                                    }
                                    else if (reason == "halted")
                                        _log($"putar makro '{macro.Name}' berhenti: Emergency Stop");
                                    else if (reason == "done")
                                        _log($"putar makro '{macro.Name}' selesai penuh");
                                }))
                                return Response.Json("{\"ok\":false,\"error\":\"sedang merekam/memutar makro lain\"}", 409);
                            _onActivity();
                            _log($"putar makro: {macro.Name} ({macro.Events.Count} kejadian, {mSpeed}x)");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true, ["started"] = true, ["name"] = macro.Name,
                                ["events"] = macro.Events.Count, ["speed"] = mSpeed,
                            });
                        }
                        case "/macros/stop":
                        {
                            MacroRecorder.StopPlaying();
                            _onActivity();
                            _log("putar makro dihentikan");
                            return Ok();
                        }
                        case "/setvalue":
                        {
                            // Isi kolom TERVERIFIKASI (v3.5.0): jalur
                            // utama LATAR lewat ValuePattern UIA —
                            // tanpa klik, tanpa fokus, tanpa mouse
                            // (Background Interaction). Cadangan fisik
                            // bila UIA tidak mendukung: klik -> Ctrl+A
                            // -> tempel -> baca balik isi kolom lewat
                            // Ctrl+A + Ctrl+C -> bandingkan -> Enter
                            // (opsional). Lahir dari pahitnya mengisi
                            // kolom angka Figma (Electron): /type
                            // tidak selalu masuk dan dulu verifikasi
                            // butuh OCR berkali-kali.
                            if (!HasInt(root, "x") || !HasInt(root, "y")) return MissingField("x/y");
                            int x = GetInt(root, "x", 0), y = GetInt(root, "y", 0);
                            string text = GetString(root, "text", "");
                            bool enter = GetBool(root, "enter", true);
                            int clicks = GetInt(root, "clicks", 1);
                            if (clicks < 1) clicks = 1;
                            if (clicks > 2) clicks = 2;
                            string via = "uia";
                            string readback = OnUi(() =>
                            {
                                if (UiaScan.TrySetValueAtPoint(x, y, text, out var rb1, out var _en)) return rb1;
                                return null;
                            });
                            if (readback == null)
                            {
                                via = "fisik";
                                int nClick = clicks;
                                readback = OnUi(() =>
                                {
                                    NativeInput.ClickTimes(x, y, "left", nClick);
                                    System.Threading.Thread.Sleep(80);
                                    NativeInput.PressKey("^a");
                                    SetClipboardText(text);
                                    NativeInput.PressKey("^v");
                                    System.Threading.Thread.Sleep(120);
                                    // Baca balik: pilih semua isi kolom
                                    // lalu salin — clipboard kini
                                    // memegang isi kolom yang SEBENARNYA.
                                    NativeInput.PressKey("^a");
                                    NativeInput.PressKey("^c");
                                    System.Threading.Thread.Sleep(60);
                                    try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; }
                                    catch { return ""; }
                                });
                            }
                            bool entered = false;
                            if (enter)
                            {
                                OnUi(() =>
                                {
                                    // Jalur UIA tidak memindahkan fokus;
                                    // fokuskan elemennya agar Enter
                                    // sampai ke kolom yang benar.
                                    if (via == "uia") { try { UiaScan.FocusAtPoint(x, y); } catch { } }
                                    NativeInput.PressKey("ENTER");
                                    return true;
                                });
                                entered = true;
                            }
                            _onActivity();
                            _lastClickX = x; _lastClickY = y; _lastClickTick = Environment.TickCount64;
                            string rb = (readback ?? "").Trim();
                            bool match = string.Equals(rb, text.Trim(), StringComparison.Ordinal);
                            _log($"setvalue {x},{y} via {via}: '{(text.Length > 20 ? text.Substring(0, 20) + "…" : text)}' -> baca balik {(match ? "COCOK" : "BEDA '" + (rb.Length > 20 ? rb.Substring(0, 20) + "…" : rb) + "'")}");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["via"] = via,
                                ["readback"] = rb,
                                ["match"] = match,
                                ["entered"] = entered,
                            });
                        }
                        case "/readvalue":
                        {
                            // Baca nilai elemen di titik layar tanpa
                            // menyentuhnya (v3.5.0) — pasangan
                            // /setvalue untuk cek sebelum/sesudah.
                            if (!HasInt(root, "x") || !HasInt(root, "y")) return MissingField("x/y");
                            int x = GetInt(root, "x", 0), y = GetInt(root, "y", 0);
                            string val = "", nm = "", tp = "";
                            bool hasVal = false;
                            bool okR = OnUi(() => UiaScan.ReadAtPoint(x, y, out val, out nm, out tp, out hasVal));
                            _onActivity();
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = okR,
                                ["value"] = val,
                                ["hasValue"] = hasVal,
                                ["name"] = nm,
                                ["type"] = tp,
                            });
                        }
                        case "/uia/act":
                        {
                            // Operasi LATAR via pola UIA (v3.5.0 —
                            // Background Interaction): invoke/toggle/
                            // expand/collapse/select/setvalue/focus
                            // tanpa mouse & keyboard fisik. Sasaran:
                            // titik {x,y}, atau {automationId}, atau
                            // {name}, atau {index} pindai jendela depan.
                            string action = GetString(root, "action", "invoke");
                            string text = GetString(root, "text", "");
                            string name = GetString(root, "name", "");
                            string aid = GetString(root, "automationId", "");
                            int index = GetInt(root, "index", -1);
                            int? px = HasInt(root, "x") ? GetInt(root, "x", 0) : (int?)null;
                            int? py = HasInt(root, "y") ? GetInt(root, "y", 0) : (int?)null;
                            // waitMs (v5.11.0 — Bagian C): cari
                            // elemen dengan coba-lagi sampai
                            // batas waktu — elemen yang belum
                            // muncul tidak lagi langsung gagal.
                            // 0 = perilaku lama (sekali coba).
                            int waitMs = GetInt(root, "waitMs", 0);
                            if (waitMs < 0) waitMs = 0;
                            if (waitMs > 60000) waitMs = 60000;
                            var oc = OnUi(() => UiaScan.Act(px, py, aid, name, index, action, text));
                            var swAct = Stopwatch.StartNew();
                            while (!oc.Ok && swAct.ElapsedMilliseconds < waitMs)
                            {
                                Thread.Sleep(250);
                                oc = OnUi(() => UiaScan.Act(px, py, aid, name, index, action, text));
                            }
                            _onActivity();
                            _log($"uia/act {action} '{oc.ElementName}' {(oc.Ok ? "ok" : "gagal: " + oc.Error)}");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = oc.Ok,
                                ["action"] = action,
                                ["element"] = oc.ElementName,
                                ["type"] = oc.ElementType,
                                ["value"] = oc.Value,
                                ["error"] = oc.Error,
                            });
                        }
                        case "/browser/start":
                        {
                            // Otomasi peramban CDP (v5.11.0 —
                            // Bagian E): Chrome TERPASANG dengan
                            // PROFIL KHUSUS otomasi (profil utama
                            // user tidak disentuh). Lihat
                            // CdpBrowser.cs soal keputusan CDP
                            // mentah vs pustaka Playwright.
                            var rb = CdpBrowser.Start();
                            _onActivity();
                            _log("browser/start: " + (rb.Ok ? rb.Text : "gagal: " + rb.Error));
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = rb.Ok,
                                ["status"] = rb.Ok ? rb.Text : "",
                                ["error"] = rb.Error,
                            }, rb.Ok ? 200 : 500);
                        }
                        case "/browser/stop":
                        {
                            var rb = CdpBrowser.Stop(GetBool(root, "closeBrowser", false));
                            _onActivity();
                            _log("browser/stop");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = rb.Ok, ["error"] = rb.Error,
                            }, rb.Ok ? 200 : 500);
                        }
                        case "/browser/goto":
                        {
                            string url = GetString(root, "url", "");
                            if (string.IsNullOrWhiteSpace(url))
                                return Response.Json("{\"ok\":false,\"error\":\"url wajib\"}", 400);
                            var rb = CdpBrowser.Navigate(url);
                            _onActivity();
                            _log("browser/goto " + url + (rb.Ok ? "" : " GAGAL: " + rb.Error));
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = rb.Ok, ["error"] = rb.Error,
                            }, rb.Ok ? 200 : 500);
                        }
                        case "/browser/eval":
                        {
                            string expr = GetString(root, "expr", "");
                            if (string.IsNullOrWhiteSpace(expr))
                                return Response.Json("{\"ok\":false,\"error\":\"expr wajib\"}", 400);
                            var rb = CdpBrowser.Eval(expr);
                            _onActivity();
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = rb.Ok, ["value"] = rb.Text, ["error"] = rb.Error,
                            }, rb.Ok ? 200 : 500);
                        }
                        case "/browser/waitfor":
                        {
                            string sel = GetString(root, "selector", "");
                            if (string.IsNullOrWhiteSpace(sel))
                                return Response.Json("{\"ok\":false,\"error\":\"selector wajib\"}", 400);
                            int tw = GetInt(root, "timeoutMs", 10000);
                            var rb = CdpBrowser.WaitFor(sel, tw);
                            _onActivity();
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = rb.Ok, ["status"] = rb.Text, ["error"] = rb.Error,
                            }, rb.Ok ? 200 : 404);
                        }
                        case "/browser/click":
                        {
                            string sel = GetString(root, "selector", "");
                            if (string.IsNullOrWhiteSpace(sel))
                                return Response.Json("{\"ok\":false,\"error\":\"selector wajib\"}", 400);
                            var rb = CdpBrowser.ClickSelector(sel);
                            _onActivity();
                            _log("browser/click " + sel + (rb.Ok ? "" : " GAGAL: " + rb.Error));
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = rb.Ok, ["error"] = rb.Error,
                            }, rb.Ok ? 200 : 404);
                        }
                        case "/browser/setvalue":
                        {
                            string sel = GetString(root, "selector", "");
                            if (string.IsNullOrWhiteSpace(sel))
                                return Response.Json("{\"ok\":false,\"error\":\"selector wajib\"}", 400);
                            var rb = CdpBrowser.SetValue(sel, GetString(root, "value", ""));
                            _onActivity();
                            _log("browser/setvalue " + sel + (rb.Ok ? "" : " GAGAL: " + rb.Error));
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = rb.Ok, ["error"] = rb.Error,
                            }, rb.Ok ? 200 : 404);
                        }
                        case "/browser/text":
                        {
                            var rb = CdpBrowser.GetText(GetString(root, "selector", ""));
                            _onActivity();
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = rb.Ok, ["text"] = rb.Text, ["error"] = rb.Error,
                            }, rb.Ok ? 200 : 404);
                        }
                        case "/waitfor":
                        {
                            // Tunggu CERDAS sisi server (v3.5.0 —
                            // Smart Waiting): berhenti saat kondisi
                            // terpenuhi, bukan durasi tetap. kind:
                            // text (teks muncul di wilayah, OCR),
                            // uia (elemen bernama muncul di jendela
                            // depan), window (jendela berjudul
                            // muncul), stable (wilayah layar berhenti
                            // berubah), process (proses muncul/hilang).
                            string kind = GetString(root, "kind", "");
                            if (kind != "text" && kind != "uia" && kind != "window" && kind != "stable" && kind != "process")
                                return Response.Json("{\"ok\":false,\"error\":\"kind harus text/uia/window/stable/process\"}", 400);
                            string text = GetString(root, "text", "");
                            string pname = GetString(root, "process", "");
                            bool gone = GetBool(root, "gone", false);
                            bool fuzzy = GetBool(root, "fuzzy", false);
                            int timeoutMs = GetInt(root, "timeoutMs", 10000);
                            if (timeoutMs < 250) timeoutMs = 250;
                            if (timeoutMs > 120000) timeoutMs = 120000;
                            int stableMs = GetInt(root, "stableMs", 800);
                            int rx = GetInt(root, "x", 0), ry = GetInt(root, "y", 0);
                            int rw = GetInt(root, "w", 0), rh = GetInt(root, "h", 0);
                            var wil = (rw > 0 && rh > 0)
                                ? new Rectangle(rx, ry, rw, rh)
                                : (NativeInput.PrimaryBounds());
                            var swW = System.Diagnostics.Stopwatch.StartNew();
                            bool met = false;
                            bool metFuzzy = false;
                            string lastHash = null;
                            long stableSince = -1;
                            while (swW.ElapsedMilliseconds < timeoutMs)
                            {
                                try
                                {
                                    switch (kind)
                                    {
                                        case "text":
                                        {
                                            using (var bmp = CaptureRegionBitmap(wil, 0, out var eff))
                                            {
                                                var rr = OcrReader.Read(bmp, eff.X, eff.Y);
                                                if (rr.Text != null && rr.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) met = true;
                                                else if (fuzzy && rr.Text != null)
                                                {
                                                    // Lolos kabur OPT-IN
                                                    // (v3.7.0): hanya query
                                                    // berdigit; menukar
                                                    // pasangan glyph yang
                                                    // sering tertukar di OCR
                                                    // (0/O/ø, 1/l/I, 5/S, 8/B).
                                                    // Exact selalu dicoba
                                                    // lebih dulu dan tetap
                                                    // perilaku bawaan.
                                                    string hayN = NormalizeOcrFuzzy(rr.Text);
                                                    string qN = NormalizeOcrFuzzy(text);
                                                    if (hayN != null && qN != null &&
                                                        hayN.IndexOf(qN, StringComparison.OrdinalIgnoreCase) >= 0)
                                                    { met = true; metFuzzy = true; }
                                                }
                                            }
                                            break;
                                        }
                                        case "uia":
                                        {
                                            var items = OnUi(() => UiaScan.ScanForeground(200));
                                            foreach (var it in items)
                                                if (it.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) { met = true; break; }
                                            break;
                                        }
                                        case "window":
                                        {
                                            var wins = OnUi(NativeInput.ListWindows);
                                            foreach (var w in wins)
                                                if (w.Title.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) { met = true; break; }
                                            break;
                                        }
                                        case "stable":
                                        {
                                            string h;
                                            using (var bmp = CaptureRegionBitmap(wil, 320, out var eff2))
                                            using (var ms = new MemoryStream())
                                            {
                                                bmp.Save(ms, ImageFormat.Jpeg);
                                                var bytes = ms.ToArray();
                                                using (var md5 = System.Security.Cryptography.MD5.Create())
                                                    h = Convert.ToBase64String(md5.ComputeHash(bytes));
                                            }
                                            if (h == lastHash)
                                            {
                                                if (stableSince < 0) stableSince = swW.ElapsedMilliseconds;
                                                if (swW.ElapsedMilliseconds - stableSince >= stableMs) met = true;
                                            }
                                            else { lastHash = h; stableSince = -1; }
                                            break;
                                        }
                                        case "process":
                                        {
                                            bool exists = false;
                                            try { exists = Process.GetProcessesByName(pname).Length > 0; } catch { }
                                            met = gone ? !exists : exists;
                                            break;
                                        }
                                    }
                                }
                                catch { /* kondisi belum bisa dibaca — lanjut polling */ }
                                if (met) break;
                                System.Threading.Thread.Sleep(250);
                            }
                            _onActivity();
                            _log($"waitfor {kind} '{text}{pname}': {(met ? "TERPENUHI" : "timeout")} ({swW.ElapsedMilliseconds}ms)");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["met"] = met,
                                ["kind"] = kind,
                                ["fuzzy"] = fuzzy,
                                ["matchedVia"] = met ? (metFuzzy ? "fuzzy" : "exact") : "",
                                ["waitedMs"] = (int)swW.ElapsedMilliseconds,
                            });
                        }
                        case "/focus":
                        {
                            string query = GetString(root, "title", "");
                            // Penargetan hwnd (v3.8.0 — DSA-01),
                            // sama seperti /window.
                            long hwndVal = GetLong(root, "hwnd", 0);
                            if (string.IsNullOrWhiteSpace(query) && hwndVal == 0)
                                return Response.Json("{\"ok\":false,\"error\":\"title kosong (atau kirim hwnd)\"}", 400);
                            var wins = OnUi(NativeInput.ListWindows);
                            NativeInput.WindowInfo match = null;
                            foreach (var w in wins)
                            {
                                if (hwndVal != 0)
                                {
                                    if (w.Handle == (IntPtr)hwndVal) { match = w; break; }
                                }
                                else if (w.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    match = w;
                                    break;
                                }
                            }
                            if (match == null)
                                return Response.Json("{\"ok\":false,\"error\":\"jendela tidak ditemukan\"}", 404);
                            var m = match;
                            OnUi(() => { NativeInput.FocusWindow(m.Handle); return true; });
                            _onActivity();
                            _log("fokus ke jendela: " + m.Title);
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["focused"] = m.Title,
                                ["hwnd"] = m.Handle.ToInt64(),
                            });
                        }
                        case "/shell":
                            return Shell(root);
                        case "/shell/restart":
                            lock (_shellLock)
                            {
                                KillShell();
                                EnsureShell();
                            }
                            _onActivity();
                            _log("shell dimulai ulang atas permintaan");
                            return Ok();
                        case "/exec":
                            return Exec(root);
                        case "/open":
                            return OpenPath(root);
                    }
                }
            }

            return Response.Json("{\"ok\":false,\"error\":\"endpoint tidak dikenal\"}", 404);
        }
        catch (UiCancelledException cex)
        {
            // v3.10.0: operasi dibatalkan selagi mengantre — aksi
            // tidak pernah berjalan, jadi aman diulang pemanggil.
            return Response.JsonObject(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "operasi dibatalkan sebelum mulai (POST /op/cancel)",
                ["code"] = "op_cancelled",
                ["retryable"] = true,
                ["opId"] = cex.OpId,
            }, 409);
        }
        catch (UiTimeoutException tex)
        {
            // v3.7.0 (BUG-10): request timeout, operasi TIDAK
            // dibatalkan — hasil akhirnya bisa dicek lewat /op.
            // v3.10.0: catat untuk /health + kode taksonomi.
            _lastUiTimeoutTick = Environment.TickCount64;
            _lastUiTimeoutName = tex.Name;
            _lastUiTimeoutTime = DateTime.Now.ToString("HH:mm:ss");
            return Response.JsonObject(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "ui-timeout",
                ["code"] = "ui_timeout",
                ["retryable"] = false,
                ["message"] = tex.Message,
                ["opId"] = tex.OpId,
                ["timeoutMs"] = tex.TimeoutMs,
                ["note"] = "operasi tidak dibatalkan dan tidak diulang otomatis; cek GET /op?id=" + tex.OpId,
            }, 504);
        }
        catch (JsonException)
        {
            return Response.Json("{\"ok\":false,\"error\":\"body bukan JSON yang valid\"}", 400);
        }
        catch (Exception ex)
        {
            _log("error: " + ex.Message);
            return Response.JsonObject(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = ex.Message,
            }, 500);
        }
    }

    // Args berisi "-EncodedCommand <base64>" dipotong jadi
    // ringkasan untuk keperluan log (v2.5.7).
    private static string SummarizeEncoded(string args)
    {
        int i = args.IndexOf("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return args;
        return args.Substring(0, i) + "-EncodedCommand (diringkas)";
    }

    private static Response Ok()
    {
        return Response.Json("{\"ok\":true}");
    }

    // ---------- Aksi-aksi ----------

    private Response Exec(JsonElement root)
    {
        string command = GetString(root, "command", "");
        string args = GetString(root, "args", "");

        // Jalur script terenkode (v2.4.0): "scriptB64" = script
        // PowerShell utuh ter-Base64 (UTF-8). Dijalankan lewat
        // -EncodedCommand sehingga TIDAK ada lagi rantai tanda kutip
        // yang bisa merusak perintah kompleks. command/args diabaikan
        // bila scriptB64 ada.
        string scriptB64 = GetString(root, "scriptB64", "");
        if (!string.IsNullOrWhiteSpace(scriptB64))
        {
            string script;
            try { script = Encoding.UTF8.GetString(Convert.FromBase64String(scriptB64)); }
            catch { return Response.Json("{\"ok\":false,\"error\":\"scriptB64 bukan Base64 yang valid\"}", 400); }
            command = "powershell";
            args = "-NoProfile -EncodedCommand " +
                   Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            _log($"jalanin: script terenkode ({script.Length} karakter)");
        }

        if (string.IsNullOrWhiteSpace(command))
            return Response.Json("{\"ok\":false,\"error\":\"command kosong\"}", 400);

        int timeoutMs = GetInt(root, "timeoutMs", DefaultExecTimeoutMs);
        if (timeoutMs < 1000) timeoutMs = 1000;
        if (timeoutMs > MaxExecTimeoutMs) timeoutMs = MaxExecTimeoutMs;

        _onActivity();
        // v2.5.7: scriptB64 sudah dicatat ringkas di atas; args
        // untuk jalur itu berisi Base64 raksasa yang TIDAK boleh
        // masuk log (dulu tercatat dua kali dan tumpah memenuhi
        // panel log jendela).
        if (string.IsNullOrWhiteSpace(scriptB64))
            _log("jalanin: " + command + (args.Length > 0 ? " " + SummarizeEncoded(args) : ""));

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using (var p = Process.Start(psi))
        {
            if (p == null)
                return Response.Json("{\"ok\":false,\"error\":\"proses gagal dimulai\"}", 500);

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();

            bool exited = p.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { p.Kill(true); } catch { /* abaikan */ }
                try { p.WaitForExit(3000); } catch { /* abaikan */ }
            }

            string stdout = Truncate(SafeResult(outTask));
            string stderr = Truncate(SafeResult(errTask));
            int exitCode = -1;
            try { exitCode = p.ExitCode; } catch { /* proses dimatikan dsb. */ }

            _log($"selesai: {command} (exit {exitCode}{(exited ? "" : ", timeout")})");
            // Semantik timeout DISAMAKAN dengan /shell (v3.10.0 —
            // DSA-11): timeout selalu ok:false + code + retryable,
            // keluaran parsial tetap dikembalikan. Dulu /exec
            // menjawab ok:true saat timeout, /shell ok:false —
            // pemanggil tidak bisa membedakan tanpa menebak.
            if (!exited)
            {
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["exitCode"] = exitCode,
                    ["timeout"] = true,
                    ["code"] = "exec_timeout",
                    ["retryable"] = false,
                    ["stdout"] = stdout,
                    ["stderr"] = stderr,
                });
            }
            return Response.JsonObject(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["exitCode"] = exitCode,
                ["timeout"] = false,
                ["stdout"] = stdout,
                ["stderr"] = stderr,
            });
        }
    }

    private Response OpenPath(JsonElement root)
    {
        string path = GetString(root, "path", "");
        if (string.IsNullOrWhiteSpace(path))
            return Response.Json("{\"ok\":false,\"error\":\"path kosong\"}", 400);

        _onActivity();
        _log("buka: " + path);
        var psi = new ProcessStartInfo { FileName = path, UseShellExecute = true };
        try
        {
            // Folder kerja = folder berkasnya (v3.6.0, BUG-03
            // audit): aplikasi yang membaca berkas data relatif
            // terhadap folder kerja (OBS: locale/en-US.ini) gagal
            // total saat dibuka dari folder kerja MuseLink.
            // "cwd" eksplisit menimpa bawaan; "args" meneruskan
            // argumen baris perintah.
            if (File.Exists(path))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            }
            string cwdReq = GetString(root, "cwd", "");
            if (!string.IsNullOrWhiteSpace(cwdReq) && Directory.Exists(cwdReq))
                psi.WorkingDirectory = cwdReq;
            string argsReq = GetString(root, "args", "");
            if (!string.IsNullOrWhiteSpace(argsReq)) psi.Arguments = argsReq;
        }
        catch { /* folder kerja gagal diatur — buka seperti biasa */ }
        Process.Start(psi);
        // Verifikasi buka (v3.5.0 — Action Verification): caller
        // boleh meminta bukti jendela berjudul tertentu muncul.
        string expect = GetString(root, "expectWindow", "");
        if (!string.IsNullOrWhiteSpace(expect))
        {
            int timeoutMs = GetInt(root, "expectTimeoutMs", 8000);
            if (timeoutMs < 250) timeoutMs = 250;
            if (timeoutMs > 30000) timeoutMs = 30000;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool found = false;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    var wins = OnUi(NativeInput.ListWindows);
                    foreach (var w in wins)
                        if (w.Title.IndexOf(expect, StringComparison.OrdinalIgnoreCase) >= 0) { found = true; break; }
                }
                catch { /* daftar jendela belum bisa dibaca — coba lagi */ }
                if (found) break;
                System.Threading.Thread.Sleep(250);
            }
            _log($"buka verifikasi jendela '{expect}': {(found ? "MUNCUL" : "tidak muncul")} ({sw.ElapsedMilliseconds}ms)");
            return Response.JsonObject(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["verified"] = found,
                ["expectWindow"] = expect,
                ["waitedMs"] = (int)sw.ElapsedMilliseconds,
            });
        }
        return Ok();
    }

    // ---------- Shell persisten (v2.1) ----------

    /// <summary>
    /// Satu proses powershell.exe (-NoProfile -NoExit) yang hidup terus:
    /// folder kerja & variabel bertahan antar panggilan /shell — beda
    /// dengan /exec yang selalu proses baru. Dibuat malas (lazy) saat
    /// pertama dipakai, dan dibuat ulang bila prosesnya mati.
    /// Keluaran stdout+stderr dipompa terus ke satu buffer bersama;
    /// tiap perintah ditutup baris penanda (sentinel) yang memuat
    /// $LASTEXITCODE, jadi pemanggil tahu kapan perintah selesai.
    /// </summary>
    private void EnsureShell()
    {
        if (_shell != null && !_shell.Proc.HasExited) return;
        KillShell();

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NoExit",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var proc = Process.Start(psi);
        if (proc == null) throw new InvalidOperationException("powershell.exe gagal dimulai");
        var session = new ShellSession { Proc = proc, In = proc.StandardInput };
        _shell = session;
        _ = Task.Run(() => PumpLines(proc.StandardOutput, session));
        _ = Task.Run(() => PumpLines(proc.StandardError, session));
        try
        {
            // Pastikan $LASTEXITCODE selalu angka sejak awal sesi
            // (perintah cmdlet tidak mengubahnya, perintah native iya).
            session.In.WriteLine("$LASTEXITCODE = 0");
            session.In.Flush();
        }
        catch { /* abaikan — panggilan berikutnya akan gagal tulis & ditangani */ }
    }

    private static void PumpLines(StreamReader reader, ShellSession session)
    {
        try
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lock (session.BufferLock)
                    session.Buffer.Append(line).Append('\n');
            }
        }
        catch { /* proses mati dsb. — pompa berhenti */ }
    }

    private void KillShell()
    {
        try { _shell?.Proc.Kill(true); } catch { /* abaikan */ }
        try { _shell?.Proc.Dispose(); } catch { /* abaikan */ }
        _shell = null;
    }

    private Response Shell(JsonElement root)
    {
        string command = GetString(root, "command", "");
        // scriptB64 (v2.4.0): script PowerShell utuh ter-Base64
        // (UTF-8) — didekode jadi teks perintah apa adanya, tanpa
        // risiko rusak di tanda kutip. Menggantikan "command".
        string scriptB64 = GetString(root, "scriptB64", "");
        if (!string.IsNullOrWhiteSpace(scriptB64))
        {
            try { command = Encoding.UTF8.GetString(Convert.FromBase64String(scriptB64)); }
            catch { return Response.Json("{\"ok\":false,\"error\":\"scriptB64 bukan Base64 yang valid\"}", 400); }
        }
        if (string.IsNullOrWhiteSpace(command))
            return Response.Json("{\"ok\":false,\"error\":\"command kosong\"}", 400);

        int timeoutMs = GetInt(root, "timeoutMs", DefaultExecTimeoutMs);
        if (timeoutMs < 1000) timeoutMs = 1000;
        if (timeoutMs > MaxExecTimeoutMs) timeoutMs = MaxExecTimeoutMs;

        _onActivity();
        // Log cukup ±80 karakter pertama perintah — keluaran TIDAK dicatat.
        _log("shell: " + (command.Length > 80 ? command.Substring(0, 80) + "…" : command));

        lock (_shellLock)
        {
            EnsureShell();
            var session = _shell;
            int seq = ++_shellSeq;
            string marker = "__JEMBATAN_END_" + seq + "__";

            int start;
            lock (session.BufferLock) { start = session.Buffer.Length; }

            try
            {
                session.In.WriteLine(command);
                session.In.WriteLine("echo \"" + marker + "$($LASTEXITCODE)\"");
                session.In.Flush();
            }
            catch
            {
                // Pipa tulis putus = shell mati di tengah jalan; buat ulang
                // supaya panggilan berikutnya mulai dari shell yang bersih.
                KillShell();
                EnsureShell();
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "shell mati saat perintah dikirim; shell baru sudah dimulai",
                    ["restarted"] = true,
                }, 500);
            }

            var sw = Stopwatch.StartNew();
            string raw = "";
            int markerIdx = -1;
            int exitCode = 0;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (session.BufferLock)
                    raw = session.Buffer.ToString(start, session.Buffer.Length - start);
                // Kejadian TERAKHIR yang dicari: baris sentinel yang mungkin
                // tergema sebagai teks perintah mengandung teks yang sama.
                markerIdx = raw.LastIndexOf(marker, StringComparison.Ordinal);
                if (markerIdx >= 0)
                {
                    int p = markerIdx + marker.Length;
                    int q = p;
                    while (q < raw.Length && (char.IsDigit(raw[q]) || raw[q] == '-')) q++;
                    if (!int.TryParse(raw.Substring(p, q - p), out exitCode)) exitCode = 0;
                    break;
                }
                if (session.Proc.HasExited) break;
                Thread.Sleep(25);
            }

            if (markerIdx < 0)
            {
                // Timeout: penanda tak pernah tiba (perintah menggantung /
                // menunggu masukan). Bunuh + buat ulang shell agar panggilan
                // berikutnya bersih; keluaran sebagian tetap dikembalikan.
                string partial = CleanShellOutput(raw, command);
                KillShell();
                EnsureShell();
                _log("shell timeout — shell dimulai ulang");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["timeout"] = true,
                    ["code"] = "shell_timeout",
                    ["retryable"] = false,
                    ["exitCode"] = -1,
                    ["restarted"] = true,
                    ["stdout"] = partial,
                });
            }

            string stdout = CleanShellOutput(raw.Substring(0, markerIdx), command);
            return Response.JsonObject(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["exitCode"] = exitCode,
                ["stdout"] = stdout,
            });
        }
    }

    /// <summary>
    /// Bersihkan keluaran shell mentah: buang gema baris perintah yang
    /// dikirim, buang awalan prompt "PS C:\...&gt; " bila ada, dan buang
    /// baris yang mengandung teks sentinel. PowerShell non-interaktif
    /// biasanya tidak menggemakan masukan — ini lapisan pengaman saja.
    /// </summary>
    private static string CleanShellOutput(string raw, string command)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string text = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        var kept = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains("__JEMBATAN_END_")) continue; // sisa baris sentinel
            string l = line;
            if (l.StartsWith("PS ", StringComparison.Ordinal))
            {
                int gt = l.IndexOf('>');
                if (gt >= 0 && gt < 80) l = l.Substring(gt + 1).TrimStart();
            }
            kept.Add(l);
        }
        if (kept.Count > 0 && kept[0].Trim() == command.Trim())
            kept.RemoveAt(0);
        return string.Join("\n", kept).Trim('\n');
    }

    // ---------- Endpoint streaming v2 ----------

    private void HandleFileDownload(NetworkStream stream, RequestHead head)
    {
        string path = GetQueryParam(head.Query, "path");
        _onActivity();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _log("unduh berkas GAGAL (tidak ditemukan): " + path);
            WriteResponse(stream, Response.Json("{\"ok\":false,\"error\":\"berkas tidak ditemukan\"}", 404));
            return;
        }

        var info = new FileInfo(path);
        _log($"unduh berkas: {path} ({info.Length} byte)");
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 200 OK\r\n");
        sb.Append("Content-Type: application/octet-stream\r\n");
        sb.Append("Content-Length: ").Append(info.Length).Append("\r\n");
        sb.Append("Content-Disposition: attachment; filename=\"")
          .Append(info.Name.Replace("\"", "")).Append("\"\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);

        // Salin per blok 64 KB — berkas besar tidak dimuat utuh ke memori.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var buf = new byte[65536];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                stream.Write(buf, 0, n);
        }
        stream.Flush();
    }

    private void HandleUpload(NetworkStream stream, RequestHead head)
    {
        string path = GetQueryParam(head.Query, "path");
        _onActivity();
        if (string.IsNullOrWhiteSpace(path))
        {
            WriteResponse(stream, Response.Json("{\"ok\":false,\"error\":\"path kosong\"}", 400));
            return;
        }

        string parent;
        try
        {
            parent = Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            WriteResponse(stream, Response.JsonObject(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "path tidak valid: " + ex.Message,
            }, 400));
            return;
        }
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        // Badan request mentah dialirkan langsung ke berkas, per blok 64 KB.
        long total = 0;
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[65536];
            long remaining = head.ContentLength;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buf.Length, remaining);
                int n = stream.Read(buf, 0, want);
                if (n == 0) break;
                fs.Write(buf, 0, n);
                total += n;
                remaining -= n;
            }
        }

        _log($"unggah berkas: {path} ({total} byte)");
        WriteResponse(stream, Response.JsonObject(new Dictionary<string, object>
        {
            ["ok"] = true,
            ["bytes"] = total,
        }));
    }

    private int _streamClients;

    private void HandleStream(NetworkStream stream, RequestHead head)
    {
        // Batas klien stream (v3.11.0 — DSA-18): maks 2 serentak.
        // Tiap klien memicu tangkap+encode layar penuh di thread
        // sendiri; tanpa batas, klien menumpuk bisa membebani PC.
        int jumlah = Interlocked.Increment(ref _streamClients);
        if (jumlah > 2)
        {
            Interlocked.Decrement(ref _streamClients);
            try
            {
                var body429 = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"stream penuh — maksimal 2 klien serentak\",\"code\":\"stream_busy\",\"retryable\":true}");
                var head429 = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 429 Too Many Requests\r\nContent-Type: application/json\r\nContent-Length: " +
                    body429.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(head429, 0, head429.Length);
                stream.Write(body429, 0, body429.Length);
            }
            catch { /* klien sudah hilang — abaikan */ }
            _log("stream ditolak: sudah 2 klien aktif");
            return;
        }
        _onActivity();
        int maxWidth = ParseClamp(GetQueryParam(head.Query, "w"), 1280, 320, 1920);
        int quality = ParseClamp(GetQueryParam(head.Query, "q"), 60, 30, 90);
        int fps = ParseClamp(GetQueryParam(head.Query, "fps"), 10, 1, 60);
        _log($"stream dimulai (lebar {maxWidth}, kualitas {quality}, target {fps} fps)");
        try
        {
            // WriteTimeout pendek: klien yang hilang terdeteksi dari gagal tulis.
            stream.WriteTimeout = 5000;
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n" +
                "Cache-Control: no-cache\r\n" +
                "Connection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);

            var crlf = new byte[] { 13, 10 };
            int frameMs = 1000 / fps;
            byte[] lastHash = null;
            var encMs = new MemoryStream(256 * 1024); // dipakai ulang tiap frame (v2.7.5)
            long lastSentMs = -1000; // frame pertama selalu terkirim
            var clock = Stopwatch.StartNew();
            while (IsRunning)
            {
                long frameStart = clock.ElapsedMilliseconds;

                // v2.1: tangkap + encode LANGSUNG di thread pekerja stream ini.
                // Graphics.CopyFromScreen (BitBlt) tidak butuh thread UI —
                // v2 meng-Invoke per frame ke thread UI dan di dunia nyata
                // hanya mencapai ±1–2 fps; sekarang thread UI bebas untuk
                // klik/ketik yang datang bersamaan.
                Bitmap bmp;
                try { bmp = NativeInput.CaptureScreenScaled(maxWidth); }
                catch { bmp = CaptureScaledBitmap(maxWidth); }
                using (bmp)
                {
                    // v2.7.5: sidik jari SAMPLING (32 baris x 256 byte)
                    // menggantikan MD5 layar-penuh — deteksi "frame
                    // sama" nyaris gratis; perubahan yang lolos dari
                    // sampel tetap terkirim oleh keepalive 1 detik.
                    var hash = FingerprintBitmap(bmp);
                    bool unchanged = lastHash != null && hash.SequenceEqual(lastHash);
                    // Frame identik tidak dikirim — kecuali sudah ±1 detik
                    // diam, kirim satu frame penjaga (keepalive).
                    bool keepaliveDue = frameStart - lastSentMs >= 1000;
                    if (!unchanged || keepaliveDue)
                    {
                        EncodeJpegInto(bmp, quality, encMs);
                        int jpegLen = (int)encMs.Length;
                        var jpeg = encMs.GetBuffer();
                        var partHead = Encoding.ASCII.GetBytes(
                            "--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " +
                            jpegLen + "\r\n\r\n");
                        // v2.7.2: header+JPEG+CRLF digabung jadi SATU tulis
                        // per frame — tiga tulis kecil kena jeda Nagle /
                        // delayed-ACK (±10ms per frame terbuang di v2.7.1).
                        var frameBytes = new byte[partHead.Length + jpegLen + crlf.Length];
                        Buffer.BlockCopy(partHead, 0, frameBytes, 0, partHead.Length);
                        Buffer.BlockCopy(jpeg, 0, frameBytes, partHead.Length, jpegLen);
                        Buffer.BlockCopy(crlf, 0, frameBytes, partHead.Length + jpegLen, crlf.Length);
                        stream.Write(frameBytes, 0, frameBytes.Length);
                        stream.Flush();
                        lastHash = hash;
                        lastSentMs = clock.ElapsedMilliseconds;
                    }
                }

                // Kejar target fps: tidur hanya sisa waktu bingkai setelah
                // waktu tangkap+encode diperhitungkan.
                int sleep = (int)(frameMs - (clock.ElapsedMilliseconds - frameStart));
                // v2.7.1: Thread.Sleep Windows kasar (granularitas ±15ms)
                // akan menahan laju di bawah target — tidur hanya sampai
                // 2ms terakhir, sisanya putar singkat agar 60fps tercapai.
                if (sleep > 2) Thread.Sleep(sleep - 2);
                while (clock.ElapsedMilliseconds - frameStart < frameMs)
                    Thread.SpinWait(200);
            }
        }
        catch
        {
            // Klien memutus stream / listener dimatikan — akhiri dengan diam.
        }
        finally
        {
            Interlocked.Decrement(ref _streamClients);
            _log("stream berhenti");
        }
    }

    private static string _relayCache = "";
    private static long _relayCacheTick;

    /// <summary>
    /// Nama relay DERP yang dipakai jalur ke VM Muse ("nyc" dsb.),
    /// dibaca dari `tailscale status --json` di PC ini dan di-cache
    /// 60 detik agar /sysinfo tetap ringan. "" bila tidak terbaca.
    /// </summary>
    internal static string TailscaleRelay()
    {
        long now = Environment.TickCount64;
        if (now - _relayCacheTick < 60000) return _relayCache;
        _relayCacheTick = now;
        try
        {
            var exe = @"C:\Program Files\Tailscale\tailscale.exe";
            if (!File.Exists(exe)) return _relayCache;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "status --json",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using (var proc = Process.Start(psi))
            {
                string json = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(4000)) { try { proc.Kill(); } catch { } return _relayCache; }
                using (var doc = JsonDocument.Parse(json))
                {
                    var rootEl = doc.RootElement;
                    if (rootEl.TryGetProperty("Peer", out var peers) &&
                        peers.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var peer in peers.EnumerateObject())
                        {
                            var pv = peer.Value;
                            string host = "";
                            if (pv.TryGetProperty("HostName", out var hn)) host = hn.GetString() ?? "";
                            if (host.StartsWith("muse", StringComparison.OrdinalIgnoreCase) &&
                                pv.TryGetProperty("Relay", out var rl))
                            {
                                _relayCache = rl.GetString() ?? "";
                                return _relayCache;
                            }
                        }
                    }
                    if (rootEl.TryGetProperty("Self", out var self) &&
                        self.TryGetProperty("Relay", out var sr))
                        _relayCache = sr.GetString() ?? "";
                }
            }
        }
        catch { /* relay hanya keterangan */ }
        return _relayCache;
    }

    // Keadaan /diff (v2.6.0): garis dasar perbandingan layar.
    private static Bitmap _diffBase;
    private static Rectangle _diffRegion;
    private static readonly object _diffLock = new object();

    // Garis dasar smart frame /delta (v2.6.4).
    private static Bitmap _deltaBase;
    private static readonly object _deltaLock = new object();

    /// <summary>
    /// Gambar kisi koordinat LAYAR di atas salinan bitmap
    /// (v2.6.0): garis tiap step piksel layar + label angka
    /// koordinatnya. Layar user sendiri tidak pernah disentuh —
    /// grid hanya ada di gambar yang dikirim ke Muse, supaya
    /// koordinat klik dibaca dari label, bukan ditebak.
    /// </summary>
    /// <summary>
    /// Normalisasi kabur OCR (v3.6.0): petakan huruf yang sering
    /// tertukar dengan angka (O/ø→0, l/I→1, S→5, B→8). Mengembalikan
    /// null bila teks tidak mengandung angka — pemanggil lalu
    /// memakai teks asli (query huruf murni tetap dicocokkan persis).
    /// </summary>
    /// <summary>
    /// Petakan query /locate ke label objek COCO yang dikenal
    /// YOLO (v5.5.0): label Inggris apa adanya, atau alias
    /// bahasa Indonesia. Null = bukan objek yang dikenal YOLO.
    /// </summary>
    private static string MapCocoLabel(string q)
    {
        string s = (q ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        switch (s)
        {
            case "person": case "orang": case "manusia": return "person";
            case "bicycle": case "sepeda": return "bicycle";
            case "car": case "mobil": return "car";
            case "motorcycle": case "motor": case "sepeda motor": return "motorcycle";
            case "airplane": case "pesawat": return "airplane";
            case "bus": return "bus";
            case "train": case "kereta": return "train";
            case "truck": case "truk": return "truck";
            case "boat": case "kapal": case "perahu": return "boat";
            case "bird": case "burung": return "bird";
            case "cat": case "kucing": return "cat";
            case "dog": case "anjing": return "dog";
            case "horse": case "kuda": return "horse";
            case "backpack": case "ransel": case "tas": return "backpack";
            case "umbrella": case "payung": return "umbrella";
            case "handbag": case "tas tangan": return "handbag";
            case "suitcase": case "koper": return "suitcase";
            case "bottle": case "botol": return "bottle";
            case "cup": case "gelas": case "cangkir": return "cup";
            case "fork": case "garpu": return "fork";
            case "knife": case "pisau": return "knife";
            case "spoon": case "sendok": return "spoon";
            case "bowl": case "mangkok": return "bowl";
            case "banana": case "pisang": return "banana";
            case "apple": case "apel": return "apple";
            case "orange": case "jeruk": return "orange";
            case "carrot": case "wortel": return "carrot";
            case "pizza": return "pizza";
            case "donut": case "donat": return "donut";
            case "cake": case "kue": return "cake";
            case "chair": case "kursi": return "chair";
            case "couch": case "sofa": return "couch";
            case "bed": case "tempat tidur": case "kasur": return "bed";
            case "dining table": case "meja makan": case "meja": return "dining table";
            case "tv": case "televisi": case "layar": case "monitor": return "tv";
            case "laptop": return "laptop";
            case "mouse": return "mouse";
            case "remote": return "remote";
            case "keyboard": return "keyboard";
            case "cell phone": case "hp": case "ponsel": case "handphone":
            case "telepon": case "smartphone": return "cell phone";
            case "microwave": return "microwave";
            case "oven": return "oven";
            case "refrigerator": case "kulkas": return "refrigerator";
            case "book": case "buku": return "book";
            case "clock": case "jam": return "clock";
            case "scissors": case "gunting": return "scissors";
            case "toothbrush": case "sikat gigi": return "toothbrush";
            case "sports ball": case "bola": return "sports ball";
            case "potted plant": case "tanaman": case "pot": return "potted plant";
            default: return null;
        }
    }

    private static string NormalizeOcrFuzzy(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        bool hasDigit = false;
        foreach (char c in s)
            if (c >= '0' && c <= '9') { hasDigit = true; break; }
        if (!hasDigit) return null;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case 'O': case 'o': case 'ø': case 'Ø': sb.Append('0'); break;
                case 'l': case 'I': case '|': sb.Append('1'); break;
                case 'S': sb.Append('5'); break;
                case 'B': sb.Append('8'); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Kotak bernomor di atas kandidat /locate (v3.6.0 —
    /// Set-of-Marks): nomor pada gambar = urutan kandidat pada
    /// respons JSON, supaya hasil grounding bisa diverifikasi
    /// visual sebelum diklik.
    /// </summary>
    private static void DrawMarks(Bitmap bmp, Rectangle eff, List<LocateHit> hits)
    {
        if (eff.Width <= 0 || eff.Height <= 0) return;
        double sx = bmp.Width / (double)eff.Width;
        double sy = bmp.Height / (double)eff.Height;
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            int fontPx = Math.Max(11, bmp.Width / 90);
            using (var font = new Font("Consolas", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var pen = new Pen(Color.FromArgb(255, 255, 90, 0), Math.Max(2, bmp.Width / 640)))
            using (var bg = new SolidBrush(Color.FromArgb(230, 255, 90, 0)))
            using (var fg = new SolidBrush(Color.Black))
            {
                for (int i = 0; i < hits.Count && i < 10; i++)
                {
                    var r = hits[i].Rect;
                    int ix = (int)((r.X - eff.X) * sx), iy = (int)((r.Y - eff.Y) * sy);
                    int iw = Math.Max(2, (int)(r.Width * sx)), ih = Math.Max(2, (int)(r.Height * sy));
                    g.DrawRectangle(pen, ix, iy, iw, ih);
                    string t = (i + 1).ToString();
                    var sz = g.MeasureString(t, font);
                    float lx = ix, ly = iy - sz.Height - 4;
                    if (ly < 0) ly = iy + 2;
                    g.FillRectangle(bg, lx, ly, sz.Width + 6, sz.Height + 3);
                    g.DrawString(t, font, fg, lx + 3, ly + 1);
                }
            }
        }
    }

    private static void DrawGrid(Bitmap bmp, Rectangle eff, int step)
    {
        if (eff.Width <= 0 || eff.Height <= 0) return;
        double sx = bmp.Width / (double)eff.Width;
        double sy = bmp.Height / (double)eff.Height;
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            int fontPx = Math.Max(9, bmp.Width / 110);
            using (var font = new Font("Consolas", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var pen = new Pen(Color.FromArgb(120, 0, 200, 255), 1))
            using (var bg = new SolidBrush(Color.FromArgb(175, 0, 0, 0)))
            using (var fg = new SolidBrush(Color.FromArgb(255, 255, 230, 120)))
            {
                int stepImg = Math.Max(1, (int)(step * sx));
                int skip = Math.Max(1, (int)Math.Ceiling(34.0 / stepImg));
                int k = 0;
                for (int x = (int)(Math.Ceiling(eff.X / (double)step) * step); x < eff.X + eff.Width; x += step, k++)
                {
                    int ix = (int)((x - eff.X) * sx);
                    g.DrawLine(pen, ix, 0, ix, bmp.Height);
                    if (k % skip == 0)
                    {
                        string t = x.ToString();
                        var sz = g.MeasureString(t, font);
                        g.FillRectangle(bg, ix + 1, 1, sz.Width + 4, sz.Height + 2);
                        g.DrawString(t, font, fg, ix + 3, 2);
                    }
                }
                int stepImgY = Math.Max(1, (int)(step * sy));
                int skipY = Math.Max(1, (int)Math.Ceiling(34.0 / stepImgY));
                k = 0;
                for (int y = (int)(Math.Ceiling(eff.Y / (double)step) * step); y < eff.Y + eff.Height; y += step, k++)
                {
                    int iy = (int)((y - eff.Y) * sy);
                    g.DrawLine(pen, 0, iy, bmp.Width, iy);
                    if (k % skipY == 0)
                    {
                        string t = y.ToString();
                        var sz = g.MeasureString(t, font);
                        g.FillRectangle(bg, 1, iy + 1, sz.Width + 4, sz.Height + 2);
                        g.DrawString(t, font, fg, 3, iy + 2);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Bandingkan dua bitmap seukuran per blok (v2.6.0):
    /// kecilkan keduanya ke kisi blok (tiap piksel kisi =
    /// rata-rata satu blok), tandai blok yang selisih warna
    /// rata-ratanya melewati ambang. Hasil: daftar persegi
    /// blok dalam koordinat layar.
    /// </summary>
    // Penanda klik terakhir (v2.6.2): /click & /uia/click
    // mencatat titik + waktunya; /frame & /screenshot ?mark=1
    // menggambar cincin di titik itu (bila < 60 detik) —
    // Muse bisa MEMERIKSA mata kliknya sendiri sesudah
    // mengklik, bukan sekadar percaya koordinatnya.
    private static int _lastClickX = -1;
    private static int _lastClickY = -1;
    private static long _lastClickTick = 0;

    /// <summary>
    /// Gambar penanda klik terakhir: cincin merah-putih +
    /// titik tengah di koordinat layar klik terakhir,
    /// dipetakan ke bitmap wilayah eff (diskala sesuai
    /// ukuran bitmap). Tidak menggambar bila klik sudah
    /// lewat 60 detik atau titiknya di luar wilayah.
    /// </summary>
    /// <summary>
    /// OCR adaptif (v2.6.3): jalankan mode biasa + diperkuat,
    /// kembalikan yang lebih kaya kata. mode = pemenangnya.
    /// </summary>
    private static OcrReader.Result OcrAdaptivePick(Bitmap bmp, int ox, int oy, out string mode, bool withTess = false)
    {
        var biasa = OcrReader.Read(bmp, ox, oy);
        var kuat = OcrReader.ReadEnhanced(bmp, ox, oy);
        int skorBiasa = biasa.Words.Count * 1000 + (biasa.Text != null ? biasa.Text.Length : 0);
        int skorKuat = kuat.Words.Count * 1000 + (kuat.Text != null ? kuat.Text.Length : 0);
        OcrReader.Result best = skorKuat > skorBiasa ? kuat : biasa;
        mode = skorKuat > skorBiasa ? "enhanced" : "normal";
        int skorBest = Math.Max(skorBiasa, skorKuat);
        if (withTess)
        {
            // Mesin ketiga (v2.6.5): Tesseract ind+eng —
            // ikut lomba skor yang sama, yang terkaya menang.
            var tess = OcrReader.ReadTesseract(bmp, ox, oy);
            int skorTess = tess.Words.Count * 1000 + (tess.Text != null ? tess.Text.Length : 0);
            if (skorTess > skorBest) { mode = "tesseract"; return tess; }
        }
        return best;
    }

    /// <summary>
    /// Cari teks (kata tunggal atau frasa sebaris) di
    /// daftar kata OCR; kembalikan titik tengah
    /// gabungannya atau null (v2.6.5, untuk /clickwhen).
    /// </summary>
    private static Point? CariTeksDiKata(List<OcrReader.Word> words, string cari)
    {
        string target = (cari ?? "").Trim();
        if (target.Length == 0 || words == null) return null;
        foreach (var w in words)
        {
            if (!string.IsNullOrEmpty(w.Text) && w.Text.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                return new Point(w.Rect.Left + w.Rect.Width / 2, w.Rect.Top + w.Rect.Height / 2);
        }
        int jumlahKata = target.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
        if (jumlahKata > 1)
        {
            for (int i = 0; i < words.Count; i++)
            {
                var gab = words[i].Rect;
                string teks = words[i].Text ?? "";
                for (int j = i + 1; j < Math.Min(words.Count, i + jumlahKata + 2); j++)
                {
                    teks += " " + (words[j].Text ?? "");
                    gab = Rectangle.Union(gab, words[j].Rect);
                    if (teks.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                        return new Point(gab.Left + gab.Width / 2, gab.Top + gab.Height / 2);
                }
            }
        }
        return null;
    }


    /// <summary>
    /// Skema jendela TUNGGAL (v3.8.0 — DSA-01/03/04/05): dipakai
    /// /state, /windows, dan /context agar ketiganya selalu
    /// melaporkan jendela dengan field yang sama — hwnd (identitas
    /// stabil), pid, rect, foreground, hung, state
    /// (normal/minimized/maximized), dialog, ownerHwnd. Field lama
    /// tidak ada yang dihapus; ini murni penambahan.
    /// </summary>
    private static Dictionary<string, object> WindowJson(NativeInput.WindowInfo w, IntPtr fgH)
    {
        bool hung = false;
        try { hung = NativeInput.IsHung(w.Handle); } catch { }
        return new Dictionary<string, object>
        {
            ["hwnd"] = w.Handle.ToInt64(),
            ["title"] = w.Title,
            ["process"] = w.Process,
            ["pid"] = w.Pid,
            ["x"] = w.Rect.X, ["y"] = w.Rect.Y,
            ["w"] = w.Rect.Width, ["h"] = w.Rect.Height,
            ["foreground"] = w.Handle == fgH,
            ["hung"] = hung,
            ["state"] = w.State ?? "normal",
            ["dialog"] = w.IsDialog,
            ["ownerHwnd"] = w.OwnerHandle.ToInt64(),
        };
    }

    /// <summary>
    /// Cari jendela berdasarkan potongan judul (v2.6.3) —
    /// untuk mode koordinat relatif. Jendela pertama yang
    /// judulnya mengandung teks itu (abaikan huruf besar).
    /// </summary>
    private NativeInput.WindowInfo FindWindowByTitle(string part)
    {
        if (string.IsNullOrWhiteSpace(part)) return null;
        var wins = OnUi(() => NativeInput.ListWindows());
        foreach (var w in wins)
        {
            if (w.Title != null && w.Title.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                return w;
        }
        return null;
    }

    private static void DrawClickMark(Bitmap bmp, Rectangle eff)
    {
        if (_lastClickX < 0) return;
        if (Environment.TickCount64 - _lastClickTick > 60000) return;
        double kx = (_lastClickX - eff.X) * (double)bmp.Width / Math.Max(1, eff.Width);
        double ky = (_lastClickY - eff.Y) * (double)bmp.Height / Math.Max(1, eff.Height);
        if (kx < -20 || ky < -20 || kx > bmp.Width + 20 || ky > bmp.Height + 20) return;
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var ringPutih = new Pen(Color.White, 6))
            using (var ring = new Pen(Color.FromArgb(255, 69, 58), 3))
            using (var titik = new SolidBrush(Color.FromArgb(255, 69, 58)))
            {
                g.DrawEllipse(ringPutih, (float)kx - 13, (float)ky - 13, 26, 26);
                g.DrawEllipse(ring, (float)kx - 13, (float)ky - 13, 26, 26);
                g.FillEllipse(titik, (float)kx - 3, (float)ky - 3, 6, 6);
            }
        }
    }

    private static List<Rectangle> CompareBlocks(Bitmap a, Bitmap b, int block, int threshold, int offX, int offY, out int total)
    {
        int gw = Math.Max(1, (int)Math.Ceiling(a.Width / (double)block));
        int gh = Math.Max(1, (int)Math.Ceiling(a.Height / (double)block));
        total = gw * gh;
        var hasil = new List<Rectangle>();
        using (var sa = new Bitmap(a, new Size(gw, gh)))
        using (var sb = new Bitmap(b, new Size(gw, gh)))
        {
            for (int y = 0; y < gh; y++)
            {
                for (int x = 0; x < gw; x++)
                {
                    var pa = sa.GetPixel(x, y);
                    var pb = sb.GetPixel(x, y);
                    int d = Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B);
                    if (d > threshold * 3)
                    {
                        int bw = Math.Min(block, a.Width - x * block);
                        int bh = Math.Min(block, a.Height - y * block);
                        hasil.Add(new Rectangle(offX + x * block, offY + y * block, bw, bh));
                    }
                }
            }
        }
        return hasil;
    }

    /// <summary>
    /// Tangkap SATU wilayah layar (v2.4.4), dijepit ke batas layar,
    /// lalu kecilkan ke lebar maks bila maxWidth > 0 dan wilayah
    /// lebih lebar. eff = wilayah efektif sesudah penjepitan.
    /// Aman dipanggil dari thread mana pun. Pemanggil membuang
    /// bitmap-nya.
    /// </summary>
    /// <summary>
    /// Bingkai layar penuh resolusi asli: DXGI lebih dulu
    /// (v2.7.0 — permukaan D3D seperti pratinjau OBS & game
    /// tertangkap benar), GDI (CopyFromScreen) sebagai cadangan.
    /// Pemanggil membuang bitmap-nya.
    /// </summary>
    private static Bitmap CaptureFullBitmap()
    {
        try
        {
            var dx = DxgiCapture.TryCapture();
            if (dx != null) return dx;
        }
        catch { /* jatuh ke GDI */ }
        var bounds = NativeInput.PrimaryBounds();
        var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return bmp;
    }

    private static Bitmap CaptureRegionBitmap(Rectangle region, int maxWidth, out Rectangle eff)
    {
        var bounds = NativeInput.PrimaryBounds();
        eff = Rectangle.Intersect(bounds, region);
        if (eff.Width <= 0 || eff.Height <= 0)
            // v3.6.0 (BUG-17 audit): wilayah sepenuhnya di luar
            // layar utama TIDAK lagi senyap diganti seluruh layar
            // utama — caller menerima galat jujur, bukan gambar
            // layar yang salah berklaim sukses.
            throw new InvalidOperationException("wilayah tangkapan di luar layar utama");
        Bitmap bmp;
        using (var full = CaptureFullBitmap())
            bmp = full.Clone(new Rectangle(eff.X - bounds.X, eff.Y - bounds.Y, eff.Width, eff.Height), full.PixelFormat);
        if (maxWidth <= 0 || bmp.Width <= maxWidth)
            return bmp;
        int w = maxWidth;
        int h = (int)(bmp.Height * (maxWidth / (double)bmp.Width));
        var scaled = new Bitmap(bmp, new Size(w, h));
        bmp.Dispose();
        return scaled;
    }

    /// <summary>
    /// Tangkap layar utama dan kecilkan ke lebar maks (bila perlu).
    /// Aman dipanggil dari thread mana pun. Pemanggil membuang bitmap-nya.
    /// </summary>
    private static Bitmap CaptureScaledBitmap(int maxWidth)
    {
        var bmp = CaptureFullBitmap();
        if (bmp.Width <= maxWidth)
            return bmp;
        int w = maxWidth;
        int h = (int)(bmp.Height * (maxWidth / (double)bmp.Width));
        var scaled = new Bitmap(bmp, new Size(w, h));
        bmp.Dispose();
        return scaled;
    }

    /// <summary>Hash MD5 piksel bitmap — cukup murah untuk deteksi "frame tidak berubah".</summary>
    private static byte[] HashBitmap(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int len = Math.Abs(data.Stride) * bmp.Height;
            var buf = new byte[len];
            Marshal.Copy(data.Scan0, buf, 0, len);
            using (var md5 = MD5.Create())
                return md5.ComputeHash(buf);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // Sidik jari murah untuk deteksi frame-sama di /stream:
    // MD5 atas 32 baris tersebar x 256 byte (total ±8KB berapa
    // pun ukuran frame) + ukuran frame sebagai garam. Bukan
    // untuk keputusan selain "kirim / lewati frame ini".
    private static byte[] FingerprintBitmap(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            int rowBytes = bmp.Width * 4;
            const int rows = 32, seg = 256;
            var buf = new byte[rows * seg + 8];
            BitConverter.GetBytes(bmp.Width).CopyTo(buf, 0);
            BitConverter.GetBytes(bmp.Height).CopyTo(buf, 4);
            int pos = 8;
            for (int i = 0; i < rows; i++)
            {
                int y = rows == 1 ? 0 : (int)((long)i * (bmp.Height - 1) / (rows - 1));
                int maxOff = Math.Max(0, rowBytes - seg);
                int off = maxOff == 0 ? 0 : (int)((long)(i * 7919) % (maxOff + 1)) & ~3;
                Marshal.Copy(data.Scan0 + y * stride + off, buf, pos, Math.Min(seg, rowBytes));
                pos += seg;
            }
            using (var md5 = MD5.Create())
                return md5.ComputeHash(buf);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // PNG tanpa kompresi hilang (v3.5.0): untuk /frame?fmt=png —
    // teks kecil di panel aplikasi terbaca tajam, tidak blur JPEG.
    private static byte[] EncodePng(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    // Tunggu teks muncul di sekitar titik (OCR wilayah, polling
    // 250ms) — dipakai verifikasi aksi (v3.5.0, Action
    // Verification). Pola sama dengan verifikasi /click v2.6.4.
    /// <summary>
    /// Verifikasi anak tangga /act (v5.14.0): tanpa expectText
    /// = lulus langsung; dengan expectText = teks harus muncul
    /// di sekitar titik aksi (OCR, pola WaitTextNear /click).
    /// </summary>
    private bool VerifyAct(int cx, int cy, string expect, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(expect)) return true;
        if (cx <= 0 && cy <= 0) return false;
        return WaitTextNear(cx, cy, expect, timeoutMs, 500, out _);
    }

    private bool WaitTextNear(int cx, int cy, string text, int timeoutMs, int radius, out int waitedMs)
    {
        if (radius < 100) radius = 100;
        if (radius > 1000) radius = 1000;
        var wil = new Rectangle(cx - radius, cy - radius, radius * 2, radius * 2);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool found = false;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using (var bmp = CaptureRegionBitmap(wil, 0, out var eff))
                {
                    var rr = OcrReader.Read(bmp, eff.X, eff.Y);
                    if (rr.Text != null && rr.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) { found = true; break; }
                }
            }
            catch { /* wilayah belum bisa dibaca — coba lagi */ }
            System.Threading.Thread.Sleep(250);
        }
        waitedMs = (int)sw.ElapsedMilliseconds;
        return found;
    }

    private static byte[] EncodeJpeg(Bitmap bmp, long quality)
    {
        using (var ms = new MemoryStream())
        {
            EncodeJpegInto(bmp, quality, ms);
            return ms.ToArray();
        }
    }

    // Encode ke MemoryStream milik pemanggil (dipakai ulang antar
    // frame oleh /stream — tanpa alokasi + salinan ToArray).
    private static void EncodeJpegInto(Bitmap bmp, long quality, MemoryStream ms)
    {
        ms.SetLength(0);
        ms.Position = 0;
        var encoder = GetJpegEncoder();
        if (encoder != null)
        {
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            bmp.Save(ms, encoder, ep);
        }
        else
        {
            bmp.Save(ms, ImageFormat.Jpeg);
        }
    }

    private static ImageCodecInfo _jpegCodec;
    private static bool _jpegCodecTried;

    private static ImageCodecInfo GetJpegEncoder()
    {
        // v2.7.5: hasil pencarian codec di-cache (dulu diiterasi
        // ulang pada SETIAP encode). Balapan antar-thread jinak:
        // yang tertulis selalu codec yang sama.
        if (_jpegCodecTried) return _jpegCodec;
        _jpegCodecTried = true;
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
            {
                _jpegCodec = codec;
                break;
            }
        }
        return _jpegCodec;
    }

    private static byte[] CaptureScreenshot()
    {
        using (var bmp = CaptureFullBitmap())
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    /// <summary>
    /// Versi bitmap dari screenshot wilayah (v2.6.1) — untuk
    /// pemanggil yang perlu menggambar di atasnya dulu (grid)
    /// sebelum mengenkode PNG. Pemanggil membuang bitmap-nya.
    /// eff = wilayah efektif sesudah penjepitan ke layar.
    /// </summary>
    private static Bitmap CaptureScreenshotRegionBitmap(int x, int y, int w, int h, out Rectangle eff)
    {
        var bounds = NativeInput.PrimaryBounds();
        eff = Rectangle.Intersect(bounds, new Rectangle(x, y, w, h));
        if (eff.Width <= 0 || eff.Height <= 0)
            // v3.6.0 (BUG-17): sama seperti CaptureRegionBitmap —
            // jangan senyap mengganti wilayah luar-layar dengan
            // seluruh layar utama.
            throw new InvalidOperationException("wilayah tangkapan di luar layar utama");
        using (var full = CaptureFullBitmap())
            return full.Clone(new Rectangle(eff.X - bounds.X, eff.Y - bounds.Y, eff.Width, eff.Height), full.PixelFormat);
    }

    /// <summary>
    /// Screenshot satu wilayah saja (v2.4.0): wilayah dijepit ke
    /// batas layar; lebih ringan dikirim dan teks kecil lebih tajam
    /// dibaca daripada memotong hasil layar penuh di sisi Muse.
    /// </summary>
    private static byte[] CaptureScreenshotRegion(int x, int y, int w, int h)
    {
        var bounds = NativeInput.PrimaryBounds();
        var region = Rectangle.Intersect(bounds, new Rectangle(x, y, w, h));
        if (region.Width <= 0 || region.Height <= 0) region = bounds;
        using (var full = CaptureFullBitmap())
        using (var bmp = full.Clone(new Rectangle(region.X - bounds.X, region.Y - bounds.Y, region.Width, region.Height), full.PixelFormat))
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    /// <summary>
    /// Hash murah piksel satu wilayah layar (v2.4.2) untuk mode
    /// tunggu layar /wait: wilayah ditangkap lalu dikecilkan ke
    /// lebar maks 96px sebelum di-hash — cukup peka untuk mendeteksi
    /// perubahan tampilan, cukup ringan untuk diperiksa tiap 250ms.
    /// Wilayah kosong/tidak valid = layar penuh.
    /// </summary>
    private static byte[] RegionHash(int x, int y, int w, int h)
    {
        var bounds = NativeInput.PrimaryBounds();
        var region = (w > 0 && h > 0)
            ? Rectangle.Intersect(bounds, new Rectangle(x, y, w, h))
            : bounds;
        if (region.Width <= 0 || region.Height <= 0) region = bounds;
        using (var full = CaptureFullBitmap())
        using (var bmp = full.Clone(new Rectangle(region.X - bounds.X, region.Y - bounds.Y, region.Width, region.Height), full.PixelFormat))
        {
            if (bmp.Width <= 96)
                return HashBitmap(bmp);
            int ph = Math.Max(1, (int)(bmp.Height * (96.0 / bmp.Width)));
            using (var probe = new Bitmap(bmp, new Size(96, ph)))
                return HashBitmap(probe);
        }
    }

    private static bool HashesEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    // ---------- Pembantu ----------

    /// <summary>Isi clipboard (thread UI). Teks kosong = clipboard dibersihkan.</summary>
    private static void SetClipboardText(string text)
    {
        // Clipboard.SetText menolak string kosong — pakai Clear() untuk itu.
        if (string.IsNullOrEmpty(text)) System.Windows.Clipboard.Clear();
        else System.Windows.Clipboard.SetText(text);
    }

    /// <summary>
    /// Tempel teks ke jendela fokus lewat clipboard, lalu pulihkan
    /// isi clipboard user (thread UI). Mekanisme yang sama dengan
    /// /paste v5.11.1 (potret per format FileDrop > gambar > teks)
    /// — dipakai juga oleh /type pintar (v5.12.0) untuk aplikasi
    /// yang tumpukan teksnya merusak ketikan tombol.
    /// </summary>
    private static void PasteTextInternal(string text)
    {
        System.Collections.Specialized.StringCollection savedFiles = null;
        System.Windows.Media.Imaging.BitmapSource savedImage = null;
        string savedText = null;
        bool hasSaved = false;
        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                savedFiles = System.Windows.Clipboard.GetFileDropList();
                hasSaved = savedFiles != null && savedFiles.Count > 0;
            }
            else if (System.Windows.Clipboard.ContainsImage())
            {
                savedImage = System.Windows.Clipboard.GetImage();
                hasSaved = savedImage != null;
            }
            else if (System.Windows.Clipboard.ContainsText())
            {
                savedText = System.Windows.Clipboard.GetText();
                hasSaved = savedText != null;
            }
        }
        catch { hasSaved = false; }
        SetClipboardText(text);
        NativeInput.PressKey("^v");
        if (hasSaved)
        {
            Thread.Sleep(250);
            try
            {
                var data = new System.Windows.DataObject();
                if (savedFiles != null) data.SetFileDropList(savedFiles);
                else if (savedImage != null) data.SetImage(savedImage);
                else data.SetText(savedText);
                System.Windows.Clipboard.SetDataObject(data, true);
            }
            catch { /* clipboard gagal dipulihkan — tempel tetap sah */ }
        }
    }

    /// <summary>Bentuk JSON satu aturan refleks (v5.13.0).</summary>
    private static Dictionary<string, object> RuleJson(ReflexEngine.Rule r)
    {
        return new Dictionary<string, object>
        {
            ["id"] = r.Id,
            ["trigger"] = r.Trigger,
            ["match"] = r.Match,
            ["pattern"] = r.Pattern,
            ["on"] = r.On,
            ["action"] = r.Action,
            ["cooldownSec"] = r.CooldownSec,
        };
    }

    /// <summary>
    /// Klik tombol berteks PERSIS pada jendela tertentu via UIA
    /// (v5.13.0 — aksi dismiss Refleks). Hanya InvokePattern;
    /// tidak ditemukan / tidak bisa di-invoke = false jujur.
    /// </summary>
    private bool DismissButtonInWindow(IntPtr hwnd, string buttonText)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(buttonText)) return false;
        return OnUi(() =>
        {
            try
            {
                var rootEl = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                if (rootEl == null) return false;
                var cond = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.NameProperty, buttonText);
                var el = rootEl.FindFirst(System.Windows.Automation.TreeScope.Descendants, cond);
                if (el == null) return false;
                if (el.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var pat))
                {
                    ((System.Windows.Automation.InvokePattern)pat).Invoke();
                    return true;
                }
                return false;
            }
            catch { return false; }
        });
    }

    /// <summary>
    /// True bila jendela depan adalah aplikasi yang tumpukan
    /// teksnya (TSF) terbukti merusak ketikan per tombol
    /// (v5.12.0): Notepad Windows 11 — vonis eksperimen belah
    /// 26 Nov 2026 (teks persis lewat tempel, rusak acak lewat
    /// tombol; penerima bersih seperti omnibox Chrome tidak
    /// bermasalah). Daftar sengaja pendek & berbasis bukti.
    /// </summary>
    private static bool IsFussyTextAppForeground()
    {
        try
        {
            var fg = NativeInput.ForegroundHandle();
            if (fg == IntPtr.Zero) return false;
            foreach (var w in NativeInput.ListWindows())
            {
                if (w.Handle != fg) continue;
                string p = (w.Process ?? "").ToLowerInvariant();
                return p == "notepad";
            }
        }
        catch { /* gagal membaca = anggap tidak rewel */ }
        return false;
    }

    /// <summary>Parse angka query string lalu jepit ke rentang [min, max].</summary>
    private static int ParseClamp(string raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw, out int v)) v = fallback;
        if (v < min) v = min;
        if (v > max) v = max;
        return v;
    }

    // ---------- Sesi grounding (v3.7.0) ----------
    //
    // /locate menyimpan hasilnya sebagai sesi: kandidat punya cid
    // stabil, sesi berlaku 60 detik dan mengingat jendela asal
    // (HWND + PID + judul). /locate/act mengklik kandidat dari
    // sesi itu SESUDAH validasi ulang — jendela depan harus sama
    // dan target harus masih di tempatnya (UIA: pindai segar;
    // OCR: baca ulang wilayahnya). Gagal validasi -> 409, tidak
    // diklik. Sesudah klik, perubahan layar di sekitar titik
    // klik diverifikasi dan dilaporkan ke pemanggil.

    private sealed class GroundCandidate
    {
        public string Cid = "";
        public string Via = "";
        public string Text = "";
        public string Type = "";
        public Rectangle Rect;
        public bool DigitFuzzy; // query berdigit -> lolos kabur OCR berlaku
        public int CX => Rect.X + Rect.Width / 2;
        public int CY => Rect.Y + Rect.Height / 2;
    }

    private sealed class GroundSession
    {
        public string Id = "";
        public string Query = "";
        public long CreatedTick;
        public long Hwnd;
        public int Pid;
        public string WindowTitle = "";
        public readonly List<GroundCandidate> Candidates = new List<GroundCandidate>();
    }

    private long _groundSeq;
    private readonly Dictionary<string, GroundSession> _groundSessions = new Dictionary<string, GroundSession>();
    private readonly object _groundLock = new object();
    private const int GroundSessionTtlMs = 60000;

    private void PruneGroundSessions(long now)
    {
        lock (_groundLock)
        {
            var dead = new List<string>();
            foreach (var kv in _groundSessions)
                if (now - kv.Value.CreatedTick > GroundSessionTtlMs)
                    dead.Add(kv.Key);
            foreach (var k in dead) _groundSessions.Remove(k);
            while (_groundSessions.Count > 8)
            {
                string oldest = null; long ot = long.MaxValue;
                foreach (var kv in _groundSessions)
                    if (kv.Value.CreatedTick < ot) { ot = kv.Value.CreatedTick; oldest = kv.Key; }
                if (oldest == null) break;
                _groundSessions.Remove(oldest);
            }
        }
    }

    // ---------- Operasi UI berbatas waktu (v3.7.0 — BUG-10) ----------
    //
    // Dulu OnUi memakai Control.Invoke tanpa batas: thread UI yang
    // macet menggantung request selamanya. Kini setiap operasi UI:
    //  - punya ID & nama (dari request yang melayaninya),
    //  - ditunggu maksimal Settings.OnUiTimeoutMs (bawaan 10 dtk),
    //  - TIMEOUT TIDAK membatalkan pekerjaannya — ia jalan terus di
    //    thread UI dan hasil akhirnya tercatat (state "done-late"),
    //    bisa dicek lewat GET /op?id= dan GET /ops,
    //  - pemanggil menerima UiTimeoutException -> respons 504,
    //    dan TIDAK ada pengulangan otomatis di lapisan mana pun.

    private sealed class UiOp
    {
        public long Id;
        public string Name = "";
        public long StartedTick;
        public long FinishedTick; // 0 = belum selesai
        public string State = "running"; // running|done|error|timeout|done-late
        public string Error = "";
        public bool TimedOut;
        public string TaskId; // korelasi tugas (v3.9.0)
        // Pembatalan selagi antre (v3.10.0): Started dipasang
        // callback saat benar-benar mulai; CancelRequested oleh
        // POST /op/cancel — diperiksa callback sebelum mulai.
        public bool Started;
        public bool CancelRequested;
    }

    private sealed class UiTimeoutException : Exception
    {
        public readonly long OpId;
        public readonly int TimeoutMs;
        public readonly string Name;
        public UiTimeoutException(long id, string name, int timeoutMs)
            : base("operasi UI '" + name + "' melewati batas " + timeoutMs + "ms")
        {
            OpId = id;
            TimeoutMs = timeoutMs;
            Name = name;
        }
    }

    /// <summary>Operasi UI dibatalkan selagi masih mengantre
    /// (v3.10.0 — POST /op/cancel): aksinya tidak pernah jalan.</summary>
    private sealed class UiCancelledException : Exception
    {
        public readonly long OpId;
        public UiCancelledException(long id, string name)
            : base("operasi UI '" + name + "' dibatalkan sebelum mulai")
        {
            OpId = id;
        }
    }

    private long _uiOpSeq;
    private readonly ConcurrentDictionary<long, UiOp> _uiOps = new ConcurrentDictionary<long, UiOp>();
    private readonly Queue<long> _uiOpOrder = new Queue<long>();
    private readonly object _uiOpOrderLock = new object();
    [ThreadStatic] private static string _opNameTls;

    // Korelasi tugas operasi berjalan (v3.9.0 — TaskJournal):
    // diisi Route dari body "taskId", dibaca RecordOp & OnUi di
    // thread yang sama; dikosongkan HandleClient tiap request.
    [ThreadStatic] private static string _opTaskTls;

    // Timeout UI terakhir (v3.10.0 — dibaca /health).
    private long _lastUiTimeoutTick;
    private string _lastUiTimeoutName;
    private string _lastUiTimeoutTime;

    // InputArbiter (v3.11.0 — DSA-15): lease input eksklusif
    // dipegang /batch selama rangkaiannya berjalan, agar dua
    // rangkaian tidak saling menyela di tengah jalan. Pemilik =
    // taskId batch (atau id tergenerasi untuk batch tanpa
    // taskId). TTL 180 dtk, diperbarui tiap aksi — lease yang
    // pemegangnya mati/hang kedaluwarsa sendiri, tidak mengunci
    // input selamanya.
    private readonly object _leaseLock = new object();
    private string _leaseOwner;
    private long _leaseExpiryTick;
    private long _leaseSeq;
    private const int LeaseTtlMs = 180000;
    // TTL lease berjalan (v5.13.0): lease API boleh meminta
    // TTL sendiri (10-300 dtk); lease batch tetap 180 dtk.
    private int _leaseTtlMs = LeaseTtlMs;

    private static readonly HashSet<string> InputPaths = new HashSet<string>
    {
        "/move", "/click", "/type", "/key", "/keydown", "/keyup",
        "/mousedown", "/mouseup", "/macros/run",
        "/scroll", "/drag", "/focus", "/window", "/paste",
        "/uia/click", "/uia/act", "/locate/act", "/routines/run",
        // v5.11.0: aksi peramban CDP juga masukan — tunduk
        // pada lease & Emergency Stop yang sama.
        "/browser/goto", "/browser/click", "/browser/setvalue", "/browser/eval",
        "/penstroke",
        // v5.14.0: tangga /act, aksi /uia3, dan perintah
        // Playwright yang mengubah halaman.
        "/act", "/uia3/act",
        "/pw/open", "/pw/goto", "/pw/click", "/pw/fill", "/pw/close",
    };

    /// <summary>Pemilik lease aktif, atau null. Lease lewat TTL
    /// dilepas malas di sini (dicatat ke log).</summary>
    private string ActiveLeaseOwner()
    {
        lock (_leaseLock)
        {
            if (_leaseOwner == null) return null;
            if (Environment.TickCount64 > _leaseExpiryTick)
            {
                _log("lease input dari " + _leaseOwner + " kedaluwarsa — dilepas otomatis");
                _leaseOwner = null;
                return null;
            }
            return _leaseOwner;
        }
    }

    private bool TryAcquireLease(string owner)
    {
        return TryAcquireLease(owner, LeaseTtlMs);
    }

    private bool TryAcquireLease(string owner, int ttlMs)
    {
        lock (_leaseLock)
        {
            string aktif = ActiveLeaseOwner();
            if (aktif != null && aktif != owner) return false;
            _leaseOwner = owner;
            _leaseTtlMs = ttlMs;
            _leaseExpiryTick = Environment.TickCount64 + ttlMs;
            return true;
        }
    }

    private void TouchLease(string owner)
    {
        lock (_leaseLock)
        {
            if (_leaseOwner == owner)
                _leaseExpiryTick = Environment.TickCount64 + _leaseTtlMs;
        }
    }

    /// <summary>Potret lease untuk endpoint &amp; /health:
    /// (pemilik, sisa detik) — pemilik null bila bebas.</summary>
    private (string owner, long remainingSec) LeaseInfo()
    {
        lock (_leaseLock)
        {
            string aktif = ActiveLeaseOwner();
            if (aktif == null) return (null, 0);
            long sisa = (_leaseExpiryTick - Environment.TickCount64) / 1000;
            return (aktif, sisa < 0 ? 0 : sisa);
        }
    }

    private Dictionary<string, object> LeaseInfoJson()
    {
        var (pemilik, sisa) = LeaseInfo();
        if (pemilik == null) return null;
        return new Dictionary<string, object>
        {
            ["owner"] = pemilik,
            ["remainingSec"] = sisa,
        };
    }

    private void ReleaseLease(string owner)
    {
        lock (_leaseLock)
        {
            if (_leaseOwner == owner) _leaseOwner = null;
        }
    }

    // Emergency Stop (v4.0.0 — brief Control Room Fase 4):
    // bendera tahan-input global. Saat aktif, semua POST yang
    // mengubah keadaan PC ditolak 423; GET (pemantauan) tetap
    // jalan. Mengaktifkan Stop juga membatalkan operasi UI yang
    // masih antre dan menghentikan shell persisten. Pelepasan
    // HANYA dari aplikasi (SetHalt dipanggil UI langsung);
    // POST /halt dari jaringan hanya boleh memasang, tak pernah
    // melepas.
    private volatile bool _inputHalted;

    /// <summary>True selama Emergency Stop aktif.</summary>
    public bool InputHalted => _inputHalted;

    /// <summary>Pasang/lepas Emergency Stop. Dipanggil UI
    /// aplikasi (dua arah) atau handler /halt (pasang saja).</summary>
    public void SetHalt(bool on, string source)
    {
        if (on == _inputHalted) return;
        _inputHalted = on;
        if (on)
        {
            int dibatalkan = 0;
            foreach (var op in _uiOps.Values)
            {
                if (op.FinishedTick == 0 && !op.Started && !op.CancelRequested)
                {
                    op.CancelRequested = true;
                    dibatalkan++;
                }
            }
            try { KillShell(); } catch { /* abaikan */ }
            _log("EMERGENCY STOP aktif dari " + source + " — " + dibatalkan + " operasi antre dibatalkan, shell dihentikan, semua input ditahan");
            EventLog.Add("emergency-stop", "Emergency Stop AKTIF dari " + source + " (" + dibatalkan + " operasi antre dibatalkan)");
        }
        else
        {
            _log("Emergency Stop dilepas dari " + source + " — input berjalan lagi");
            EventLog.Add("emergency-stop", "Emergency Stop dilepas dari " + source);
        }
    }

    /// <summary>Isi GET /health sebagai data (v4.0.0): handler
    /// HTTP dan UI Control Room membaca dari sini agar tidak
    /// pernah berbeda. Probe UI TIDAK lewat OnUi — justru untuk
    /// mendeteksi UI macet.</summary>
    public Dictionary<string, object> BuildHealth()
    {
        long probeMs = -1;
        try
        {
            using (var probeDone = new ManualResetEventSlim(false))
            {
                var swP = Stopwatch.StartNew();
                _ui.BeginInvoke((Action)(() => probeDone.Set()));
                if (probeDone.Wait(1500)) probeMs = swP.ElapsedMilliseconds;
            }
        }
        catch { /* UI tak terjangkau — probeMs tetap -1 */ }
        int running = 0;
        foreach (var u in _uiOps.Values) if (u.State == "running") running++;
        bool shellAlive = false;
        try { lock (_shellLock) { shellAlive = _shell != null && !_shell.Proc.HasExited; } } catch { }
        object lastTimeout = null;
        if (_lastUiTimeoutTick > 0)
        {
            lastTimeout = new Dictionary<string, object>
            {
                ["time"] = _lastUiTimeoutTime,
                ["name"] = _lastUiTimeoutName,
                ["agoSec"] = (Environment.TickCount64 - _lastUiTimeoutTick) / 1000,
            };
        }
        string fgTitle = "";
        try { fgTitle = NativeInput.TitleOf(NativeInput.ForegroundHandle()); } catch { }
        // v5.12.1: umur PROSES di /health (terpisah dari uptimeSec
        // = umur listener). Perbaikan penempatan: di 5.12.0 field
        // ini tak sengaja mendarat di endpoint diagnostik lain.
        long processUptimeSec = -1;
        int processPid = -1;
        try
        {
            var self = Process.GetCurrentProcess();
            processPid = self.Id;
            processUptimeSec = (long)(DateTime.Now - self.StartTime).TotalSeconds;
        }
        catch { }
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["version"] = AppVersion,
            ["uptimeSec"] = (Environment.TickCount64 - _startTick) / 1000,
            ["processUptimeSec"] = processUptimeSec,
            ["pid"] = processPid,
            ["listener"] = IsRunning,
            // Arbitrase & Refleks (v5.13.0): sensor fisik,
            // lease berjalan, dan mesin aturan — terbaca
            // sekilas dari /health.
            ["physicalInput"] = new Dictionary<string, object>
            {
                ["agoSec"] = PhysicalInput.AgoSec,
                ["count"] = PhysicalInput.Count,
                ["lastKind"] = PhysicalInput.LastKind,
            },
            ["lease"] = LeaseInfoJson(),
            ["reflex"] = new Dictionary<string, object>
            {
                ["rules"] = ReflexEngine.Counts().rules,
                ["fires"] = ReflexEngine.Counts().fires,
            },
            // Playwright (v5.14.0): pendamping terpasang?
            // (kesehatan penuh di GET /pw/state — /health
            // tidak memanggil jaringan sidecar.)
            ["pw"] = new Dictionary<string, object>
            {
                ["installed"] = PwBridge.Installed,
            },
            ["ui"] = new Dictionary<string, object>
            {
                ["ok"] = probeMs >= 0,
                ["responsiveMs"] = probeMs,
            },
            ["uiOpsRunning"] = running,
            ["lastUiTimeout"] = lastTimeout,
            ["inputLease"] = ActiveLeaseOwner(),
            ["inputHalted"] = _inputHalted,
            ["shellAlive"] = shellAlive,
            ["eventsLastSeq"] = EventLog.LastSeq,
            ["foreground"] = fgTitle,
            ["ai"] = AiBridge.HealthInfo(), // v5.4.0: status sidecar AI visual (GPU)
            ["media"] = MediaTools.HealthInfo(), // v5.6.0: FFmpeg untuk /media/*
            ["scheduler"] = Schedules.HealthInfo(), // v5.6.0: ringkasan mesin jadwal
        };
    }

    /// <summary>Snapshot operasi Route terakhir untuk UI
    /// (v4.0.0): [time, method, path, status, ms, taskId,
    /// summary], terbaru dulu.</summary>
    public List<string[]> RecentOpsSnapshot(int max)
    {
        var hasil = new List<string[]>();
        lock (_opsLock)
        {
            for (int i = _ops.Count - 1; i >= 0 && hasil.Count < max; i--)
            {
                var o = _ops[i];
                hasil.Add(new[]
                {
                    o.Time, o.Method, o.Path, o.Status.ToString(),
                    o.Ms.ToString(), o.TaskId ?? "", o.Summary ?? "",
                });
            }
        }
        return hasil;
    }

    /// <summary>Snapshot operasi UI terakhir untuk UI (v4.0.0):
    /// [id, name, state, durationMs, taskId], terbaru dulu.</summary>
    public List<string[]> RecentUiOpsSnapshot(int max)
    {
        var salin = new List<UiOp>(_uiOps.Values);
        salin.Sort((a, b) => b.Id.CompareTo(a.Id));
        var hasil = new List<string[]>();
        foreach (var u in salin)
        {
            if (hasil.Count >= max) break;
            long dur = (u.FinishedTick > 0 ? u.FinishedTick : Environment.TickCount64) - u.StartedTick;
            hasil.Add(new[]
            {
                u.Id.ToString(), u.Name, u.State, dur.ToString(), u.TaskId ?? "",
            });
        }
        return hasil;
    }

    /// <summary>Aktivitas tugas terakhir untuk kartu Active Task
    /// (v4.0.0): [taskId, jumlah operasi, operasi terakhir,
    /// waktu terakhir] atau null bila belum ada tugas.</summary>
    public string[] LatestTaskActivity()
    {
        lock (_opsLock)
        {
            for (int i = _ops.Count - 1; i >= 0; i--)
            {
                var o = _ops[i];
                if (string.IsNullOrEmpty(o.TaskId)) continue;
                int jumlah = 0;
                foreach (var x in _ops) if (x.TaskId == o.TaskId) jumlah++;
                return new[] { o.TaskId, jumlah.ToString(), o.Method + " " + o.Path, o.Time };
            }
        }
        return null;
    }

    /// <summary>Ambil "taskId" dari body JSON request (POST);
    /// null bila tidak ada/rusak. Dipotong 64 karakter.</summary>
    private static string ExtractTaskId(Request req)
    {
        if (req == null || req.Method != "POST" || string.IsNullOrEmpty(req.Body)) return null;
        try
        {
            using (var doc = JsonDocument.Parse(req.Body))
            {
                string t = GetString(doc.RootElement, "taskId", null);
                if (string.IsNullOrWhiteSpace(t)) return null;
                t = t.Trim();
                return t.Length > 64 ? t.Substring(0, 64) : t;
            }
        }
        catch { return null; }
    }
    private int _uiTimeoutCache;
    private long _uiTimeoutCacheTick;

    private int UiTimeoutMs()
    {
        long now = Environment.TickCount64;
        if (_uiTimeoutCache > 0 && now - _uiTimeoutCacheTick < 5000)
            return _uiTimeoutCache;
        _uiTimeoutCache = Settings.OnUiTimeoutMs;
        _uiTimeoutCacheTick = now;
        return _uiTimeoutCache;
    }

    private void RegisterUiOp(UiOp op)
    {
        _uiOps[op.Id] = op;
        lock (_uiOpOrderLock)
        {
            _uiOpOrder.Enqueue(op.Id);
            while (_uiOpOrder.Count > 100)
                _uiOps.TryRemove(_uiOpOrder.Dequeue(), out _);
        }
    }

    private T OnUi<T>(Func<T> func)
    {
        if (!_ui.InvokeRequired)
            return func(); // sudah di thread UI — menunggu diri sendiri tak ada artinya

        long id = Interlocked.Increment(ref _uiOpSeq);
        var op = new UiOp
        {
            Id = id,
            Name = string.IsNullOrEmpty(_opNameTls) ? "ui" : _opNameTls,
            TaskId = _opTaskTls,
            StartedTick = Environment.TickCount64,
        };
        RegisterUiOp(op);
        T result = default;
        Exception error = null;
        using (var done = new ManualResetEventSlim(false))
        {
            _ui.BeginInvoke((Action)(() =>
            {
                // Pembatalan selagi antre (v3.10.0): giliran tiba
                // tetapi operasi sudah diminta batal — lewati
                // aksinya sama sekali, jangan jalankan fungsi.
                if (op.CancelRequested)
                {
                    op.State = "cancelled";
                    op.FinishedTick = Environment.TickCount64;
                    done.Set();
                    return;
                }
                op.Started = true;
                try { result = func(); }
                catch (Exception ex) { error = ex; }
                finally
                {
                    op.FinishedTick = Environment.TickCount64;
                    op.State = op.TimedOut ? "done-late" : (error == null ? "done" : "error");
                    if (error != null) op.Error = error.Message;
                    done.Set();
                }
            }));
            int timeout = UiTimeoutMs();
            if (!done.Wait(timeout))
            {
                op.TimedOut = true;
                op.State = "timeout";
                _log($"operasi UI #{id} ({op.Name}) TIMEOUT {timeout}ms — tetap jalan di latar; cek GET /op?id={id}");
                throw new UiTimeoutException(id, op.Name, timeout);
            }
        }
        if (op.State == "cancelled") throw new UiCancelledException(id, op.Name);
        if (error != null) throw error;
        return result;
    }

    private static string SafeResult(Task<string> task)
    {
        try { return task.IsCompleted ? task.Result : ""; }
        catch { return ""; }
    }

    private static string Truncate(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > MaxOutputChars ? s.Substring(0, MaxOutputChars) + "…[dipotong]" : s;
    }

    /// <summary>
    /// Urai daftar tombol penahan "ctrl,shift" menjadi kode VK
    /// (gelombang 2). Nama yang tidak dikenal dilewati diam-diam.
    /// </summary>
    private static List<ushort> ParseModifiers(string raw)
    {
        var result = new List<ushort>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var part in raw.Split(','))
        {
            ushort vk = NativeInput.VkFromName(part);
            if (vk != 0 && !result.Contains(vk)) result.Add(vk);
        }
        return result;
    }

    private static string GetString(JsonElement root, string name, string fallback)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? fallback;
        return fallback;
    }

    private static bool GetBool(JsonElement root, string name, bool fallback)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var el) &&
            (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
            return el.GetBoolean();
        return fallback;
    }

    private static int GetInt(JsonElement root, string name, int fallback)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out int i))
            return i;
        return fallback;
    }

    /// <summary>Seperti GetInt tetapi 64-bit (v3.8.0 — untuk hwnd,
    /// yang nilainya tidak dijamin muat di int32).</summary>
    private static long GetLong(JsonElement root, string name, long fallback)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt64(out long l))
            return l;
        return fallback;
    }

    /// <summary>True bila field angka benar-benar dikirim di body
    /// (v2.5.5: bedakan "tidak dikirim" dari nilai 0 yang sah —
    /// dulu body kosong untuk /click tetap dijawab ok dan klik
    /// terjadi di pojok 0,0).</summary>
    private static bool HasInt(JsonElement root, string name)
    {
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty(name, out var v) &&
               v.ValueKind == JsonValueKind.Number;
    }

    /// <summary>Jawaban 400 baku untuk field wajib yang hilang.</summary>
    private static Response MissingField(string field)
    {
        return Response.Json("{\"ok\":false,\"error\":\"field wajib hilang: " + field + "\"}", 400);
    }
}
