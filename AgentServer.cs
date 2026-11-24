using System;
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
using System.Windows.Forms;

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
/// </summary>
public sealed class AgentServer
{
    public const int Port = 8765;
    private const int DefaultExecTimeoutMs = 60000;
    private const int MaxExecTimeoutMs = 300000;
    private const int MaxOutputChars = 100000;

    private readonly Control _ui;               // jendela utama, untuk Invoke ke thread UI
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

    public AgentServer(Control ui, Action<string> log, Action onActivity)
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
        // Penghangat cache relay Tailscale (v2.5.5): /sysinfo
        // pertama sesudah aplikasi mulai dulu lambat (query
        // `tailscale status` dingin). Thread ini mengisi cache
        // sejak start dan menyegarkannya tiap 50 detik (umur
        // cache 60 detik), jadi /sysinfo selalu dari cache.
        var warmer = new Thread(() =>
        {
            while (true)
            {
                try { TailscaleRelay(); } catch { /* hanya keterangan */ }
                Thread.Sleep(50000);
            }
        }) { IsBackground = true, Name = "RelayWarmer" };
        warmer.Start();
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* abaikan */ }
        try { _listener?.Stop(); } catch { /* abaikan */ }
        _listener = null;
        IsRunning = false;
        // PUTUSKAN / jendela ditutup = shell persisten ikut dimatikan.
        lock (_shellLock) { KillShell(); }
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
                var sw = Stopwatch.StartNew();
                var resp = Route(new Request { Method = head.Method, Path = head.Path, Query = head.Query ?? "", Body = body });
                sw.Stop();
                // Catat operasi SETELAH Route selesai tetapi SEBELUM
                // respons ditulis — bila koneksi putus saat menulis,
                // catatannya sudah ada untuk dicek lewat /lastops.
                RecordOp(head.Method, head.Path, resp.Status, (int)sw.ElapsedMilliseconds);
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
    }

    private readonly List<OpRecord> _ops = new List<OpRecord>();
    private readonly object _opsLock = new object();
    private long _opSeq;

    private void RecordOp(string method, string path, int status, int ms)
    {
        // v2.4.2: cek kesehatan (/ping) dan cek gambar cepat (/frame)
        // tidak dicatat — keduanya panggilan latar yang sering dan
        // hanya menggeser operasi penting keluar dari cincin 50.
        if (path == "/ping" || path == "/frame") return;
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
            });
            if (_ops.Count > 50) _ops.RemoveRange(0, _ops.Count - 50);
        }
    }

    // ---------- Routing ----------

    private Response Route(Request req)
    {
        try
        {
            if (req.Method == "GET" && req.Path == "/ping")
            {
                _onActivity();
                // v2.4.2: ping TIDAK dicatat di log jendela — cek
                // kesehatan Muse sering datang dan membanjiri log;
                // label status "aktivitas terakhir" tetap diperbarui
                // lewat _onActivity di atas.
                return Response.Json("{\"ok\":true,\"app\":\"jembatan\",\"version\":\"2.5.9\",\"admin\":"
                    + (Settings.IsElevated() ? "true" : "false") + "}");
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
                var png = (sw > 0 && sh > 0)
                    ? OnUi(() => CaptureScreenshotRegion(sx, sy, sw, sh))
                    : OnUi(CaptureScreenshot);
                _log("screenshot layar diambil" + (sw > 0 && sh > 0 ? $" (wilayah {sx},{sy} {sw}x{sh})" : ""));
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
                    try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
                    catch { return ""; }
                });
                _log($"clipboard dibaca ({text.Length} karakter)");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["text"] = text,
                });
            }

            if (req.Method == "GET" && req.Path == "/windows")
            {
                _onActivity();
                var wins = OnUi(NativeInput.ListWindows);
                var list = new List<Dictionary<string, object>>();
                foreach (var w in wins)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["title"] = w.Title,
                        ["process"] = w.Process,
                        ["x"] = w.Rect.X,
                        ["y"] = w.Rect.Y,
                        ["w"] = w.Rect.Width,
                        ["h"] = w.Rect.Height,
                    });
                }
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
                var bounds = Screen.PrimaryScreen != null
                    ? Screen.PrimaryScreen.Bounds
                    : new Rectangle(0, 0, 0, 0);
                // Metrik kesehatan PC (v2.4.0 gelombang 2) — diukur
                // di thread koneksi ini (±250ms untuk cuplikan CPU).
                var met = NativeInput.SystemMetrics();
                _log("info sistem diminta");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["version"] = "2.5.9",
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
                    ["themeDark"] = AppTheme.IsDark,
                    ["tailscaleRelay"] = TailscaleRelay(),
                    ["eventsLastSeq"] = EventLog.LastSeq,
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
                    try { clipLen = Clipboard.ContainsText() ? Clipboard.GetText().Length : 0; }
                    catch { /* clipboard terkunci sesaat */ }
                    return (wins, fg, clipLen);
                });
                var met = NativeInput.SystemMetrics();
                var winList = new List<Dictionary<string, object>>();
                foreach (var w in snap.wins)
                {
                    if (winList.Count >= 25) break;
                    winList.Add(new Dictionary<string, object>
                    {
                        ["title"] = w.Title,
                        ["process"] = w.Process,
                        ["x"] = w.Rect.X, ["y"] = w.Rect.Y,
                        ["w"] = w.Rect.Width, ["h"] = w.Rect.Height,
                    });
                }
                Dictionary<string, object> fgDict = null;
                if (snap.fg != null)
                {
                    fgDict = new Dictionary<string, object>
                    {
                        ["title"] = snap.fg.Title,
                        ["process"] = snap.fg.Process,
                        ["x"] = snap.fg.Rect.X, ["y"] = snap.fg.Rect.Y,
                        ["w"] = snap.fg.Rect.Width, ["h"] = snap.fg.Rect.Height,
                    };
                }
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
                var bounds = Screen.PrimaryScreen != null
                    ? Screen.PrimaryScreen.Bounds
                    : new Rectangle(0, 0, 0, 0);
                _log($"konteks instan diminta ({winList.Count} jendela)");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["version"] = "2.5.9",
                    ["admin"] = Settings.IsElevated(),
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
                int fw = ParseClamp(GetQueryParam(req.Query, "w"), 1280, 320, 1920);
                int fq = ParseClamp(GetQueryParam(req.Query, "q"), 70, 30, 95);
                // Wilayah (v2.4.4): rx/ry/rw/rh membatasi tangkapan ke
                // satu wilayah — muatan jauh lebih kecil daripada
                // bingkai layar penuh (kurva audit #1: ukuran muatan
                // adalah tuas kecepatan terbesar yang tersisa).
                int rx = ParseClamp(GetQueryParam(req.Query, "rx"), 0, 0, 7680);
                int ry = ParseClamp(GetQueryParam(req.Query, "ry"), 0, 0, 4320);
                int rw = ParseClamp(GetQueryParam(req.Query, "rw"), 0, 0, 7680);
                int rh = ParseClamp(GetQueryParam(req.Query, "rh"), 0, 0, 4320);
                byte[] jpeg;
                if (rw > 0 && rh > 0)
                {
                    using (var bmp = CaptureRegionBitmap(new Rectangle(rx, ry, rw, rh), fw, out _))
                        jpeg = EncodeJpeg(bmp, fq);
                    _log($"bingkai wilayah diambil ({rx},{ry} {rw}x{rh} -> {fw}px, q{fq})");
                }
                else
                {
                    // Tangkap cepat (v2.5.0): StretchBlt mengecilkan
                    // di pengemudi grafis; jalur lama jadi cadangan.
                    try
                    {
                        using (var bmp = NativeInput.CaptureScreenScaled(fw))
                            jpeg = EncodeJpeg(bmp, fq);
                    }
                    catch
                    {
                        using (var bmp = CaptureScaledBitmap(fw))
                            jpeg = EncodeJpeg(bmp, fq);
                    }
                    _log($"bingkai instan diambil ({fw}px, q{fq})");
                }
                return new Response { ContentType = "image/jpeg", BodyBytes = jpeg };
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
                    });
                }
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["events"] = elist,
                    ["lastSeq"] = EventLog.LastSeq,
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
                var scr = Screen.PrimaryScreen != null
                    ? Screen.PrimaryScreen.Bounds
                    : new Rectangle(0, 0, 1280, 720);
                var minta = (ow > 0 && oh > 0) ? new Rectangle(ox, oy, ow, oh) : scr;
                var wilayah = Rectangle.Intersect(scr, minta);
                if (wilayah.Width <= 0 || wilayah.Height <= 0) wilayah = scr;
                OcrReader.Result hasil;
                using (var bmp = CaptureRegionBitmap(wilayah, 0, out var eff))
                {
                    wilayah = eff;
                    try
                    {
                        hasil = OcrReader.Read(bmp, eff.X, eff.Y);
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
                _log($"OCR wilayah {wilayah.X},{wilayah.Y} {wilayah.Width}x{wilayah.Height}: {kata.Count} kata");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
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
                            bool stopOnError = !root.TryGetProperty("stopOnError", out var soe) ||
                                               soe.ValueKind != JsonValueKind.False;
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
                                results.Add(new Dictionary<string, object>
                                {
                                    ["do"] = doName,
                                    ["status"] = subResp.Status,
                                    ["result"] = parsed,
                                });
                                doneCount++;
                                if (subResp.Status >= 400 && stopOnError) break;
                            }
                            _onActivity();
                            _log($"batch: {doneCount} aksi dijalankan");
                            var batchResp = new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["count"] = doneCount,
                                ["results"] = results,
                            };
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
                            return Response.JsonObject(batchResp);
                        }
                        case "/move":
                        {
                            if (!HasInt(root, "x") || !HasInt(root, "y")) return MissingField("x/y");
                            int x = GetInt(root, "x", 0), y = GetInt(root, "y", 0);
                            OnUi(() => { NativeInput.Move(x, y); return true; });
                            _onActivity();
                            _log($"geser mouse ke {x},{y}");
                            return Ok();
                        }
                        case "/click":
                        {
                            if (!HasInt(root, "x") || !HasInt(root, "y")) return MissingField("x/y");
                            int x = GetInt(root, "x", 0), y = GetInt(root, "y", 0);
                            string button = GetString(root, "button", "left");
                            // count (v2.4.0): 2 = klik ganda, 3 = klik tiga.
                            int count = GetInt(root, "count", 1);
                            if (count < 1) count = 1;
                            if (count > 3) count = 3;
                            int n = count;
                            // modifiers (gelombang 2): "ctrl,shift,alt,win"
                            // ditahan selama klik berlangsung.
                            var mods = ParseModifiers(GetString(root, "modifiers", ""));
                            OnUi(() =>
                            {
                                foreach (var vk in mods) NativeInput.KeyDown(vk);
                                try { NativeInput.ClickTimes(x, y, button, n); }
                                finally { for (int i = mods.Count - 1; i >= 0; i--) NativeInput.KeyUp(mods[i]); }
                                return true;
                            });
                            _onActivity();
                            _log($"klik {x},{y} ({button}{(n > 1 ? ", " + n + "x" : "")}{(mods.Count > 0 ? ", +penahan" : "")})");
                            return Ok();
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
                            OnUi(() =>
                            {
                                if (down) NativeInput.KeyDown(vk);
                                else NativeInput.KeyUp(vk);
                                return true;
                            });
                            _onActivity();
                            _log((down ? "tahan tombol: " : "lepas tombol: ") + keyName);
                            return Ok();
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
                            if (string.IsNullOrWhiteSpace(wTitle) && string.IsNullOrWhiteSpace(fPath) &&
                                !hasChange && !hasQuiet &&
                                string.IsNullOrWhiteSpace(pGone) && string.IsNullOrWhiteSpace(pExists))
                                return Response.Json("{\"ok\":false,\"error\":\"windowTitle, filePath, screenChange, screenQuiet, processGone, atau processExists wajib diisi\"}", 400);
                            int wTimeout = GetInt(root, "timeoutMs", 30000);
                            if (wTimeout < 1000) wTimeout = 1000;
                            if (wTimeout > MaxExecTimeoutMs) wTimeout = MaxExecTimeoutMs;
                            _onActivity();
                            var condParts = new List<string>();
                            if (!string.IsNullOrWhiteSpace(wTitle)) condParts.Add("jendela '" + wTitle + "'");
                            if (!string.IsNullOrWhiteSpace(fPath)) condParts.Add("berkas " + fPath);
                            if (hasChange) condParts.Add("layar berubah");
                            if (hasQuiet) condParts.Add("layar diam");
                            if (!string.IsNullOrWhiteSpace(pGone)) condParts.Add("proses " + pGone + " selesai");
                            if (!string.IsNullOrWhiteSpace(pExists)) condParts.Add("proses " + pExists + " jalan");
                            _log($"tunggu kondisi (maks {wTimeout}ms): " + string.Join(" / ", condParts));
                            var wsw = Stopwatch.StartNew();
                            bool found = false;
                            string via = "";
                            byte[] changeBase = hasChange ? RegionHash(chX, chY, chW, chH) : null;
                            byte[] quietLast = hasQuiet ? RegionHash(quX, quY, quW, quH) : null;
                            long quietSince = 0;
                            int pollMs = (hasChange || hasQuiet) ? 250 : 500;
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
                                if (found) break;
                                Thread.Sleep(pollMs);
                            }
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
                            // Wait-and-see: sertakan gambar wilayah
                            // yang dipantau (yang berubah / yang
                            // sudah diam) supaya Muse tidak perlu
                            // satu panggilan gambar lagi.
                            if (see && found && (via == "screen-change" || via == "screen-quiet"))
                            {
                                var wr = via == "screen-change"
                                    ? new Rectangle(chX, chY, chW, chH)
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
                            int index = GetInt(root, "index", -1);
                            string name = GetString(root, "name", "");
                            var item = OnUi(() => UiaScan.Find(index, name));
                            if (item == null)
                                return Response.Json("{\"ok\":false,\"error\":\"elemen tidak ditemukan — pindai dulu lewat GET /uia\"}", 404);
                            string button = GetString(root, "button", "left");
                            int cx = item.CenterX, cy = item.CenterY;
                            OnUi(() => { NativeInput.Click(cx, cy, button); return true; });
                            _onActivity();
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
                                var scr = Screen.PrimaryScreen != null
                                    ? Screen.PrimaryScreen.Bounds
                                    : new Rectangle(0, 0, 1280, 720);
                                using (var bmp = CaptureRegionBitmap(scr, 0, out var eff))
                                {
                                    var hasil = OcrReader.Read(bmp, eff.X, eff.Y);
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
                            _log($"find '{q}': {matches.Count} cocok (uia {uiaCount}, ocr {ocrCount})" + (clicked ? ", pertama diklik" : ""));
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["count"] = matches.Count,
                                ["clicked"] = clicked,
                                ["matches"] = matches,
                            });
                        }
                        case "/listen":
                        {
                            // Dengar audio PC (v2.5.0): rekam loopback
                            // WASAPI selama {seconds} detik (bawaan 5,
                            // maks 30) → WAV mono 16kHz mentah.
                            int detik = GetInt(root, "seconds", 5);
                            if (detik < 1) detik = 1;
                            if (detik > 30) detik = 30;
                            _onActivity();
                            _log($"dengar audio PC ({detik} detik)…");
                            byte[] wav;
                            try
                            {
                                wav = AudioTools.CaptureLoopbackWav(detik);
                            }
                            catch (Exception ex)
                            {
                                _log("rekam audio gagal: " + ex.Message);
                                return Response.Json("{\"ok\":false,\"error\":\"rekam audio gagal: " +
                                    ex.Message.Replace("\\", " ").Replace("\"", " ") + "\"}", 500);
                            }
                            _log($"rekaman selesai ({wav.Length} byte)");
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
                            if (_ui is MainForm mf) mf.SetTheme(choice);
                            else
                            {
                                try { Settings.Theme = choice; } catch { }
                                _log("tema diganti: " + choice);
                            }
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["theme"] = choice,
                                ["dark"] = AppTheme.IsDark,
                            });
                        }
                        case "/keepawake":
                        {
                            // Saklar Jangan tidur lewat API (v2.5.0) —
                            // logika sama dengan saklar di Pengaturan.
                            bool on = GetBool(root, "on", false);
                            _onActivity();
                            if (_ui is MainForm mf2) mf2.ApplyKeepAwake(on);
                            else
                            {
                                try { Settings.KeepAwake = on; } catch { }
                                NativeInput.SetKeepAwake(on);
                                _log("jangan tidur: " + (on ? "AKTIF" : "MATI"));
                            }
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
                            if (string.IsNullOrWhiteSpace(query))
                                return Response.Json("{\"ok\":false,\"error\":\"title kosong\"}", 400);
                            if (action != "close" && action != "minimize" && action != "maximize" && action != "restore")
                                return Response.Json("{\"ok\":false,\"error\":\"action harus close/minimize/maximize/restore\"}", 400);
                            var wins = OnUi(NativeInput.ListWindows);
                            NativeInput.WindowInfo match = null;
                            foreach (var w in wins)
                            {
                                if (w.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    match = w;
                                    break;
                                }
                            }
                            if (match == null)
                                return Response.Json("{\"ok\":false,\"error\":\"jendela tidak ditemukan\"}", 404);
                            var m = match;
                            bool sent = OnUi(() => NativeInput.WindowAction(m.Handle, action));
                            _onActivity();
                            _log($"jendela '{m.Title}': {action}");
                            return Response.JsonObject(new Dictionary<string, object>
                            {
                                ["ok"] = sent,
                                ["window"] = m.Title,
                                ["action"] = action,
                            });
                        }
                        case "/scroll":
                        {
                            if (!HasInt(root, "x") || !HasInt(root, "y") || !HasInt(root, "delta")) return MissingField("x/y/delta");
                            int x = GetInt(root, "x", 0), y = GetInt(root, "y", 0);
                            int delta = GetInt(root, "delta", 0);
                            OnUi(() => { NativeInput.Scroll(x, y, delta); return true; });
                            _onActivity();
                            _log($"scroll di {x},{y} (delta {delta})");
                            return Ok();
                        }
                        case "/drag":
                        {
                            if (!HasInt(root, "x1") || !HasInt(root, "y1") || !HasInt(root, "x2") || !HasInt(root, "y2")) return MissingField("x1/y1/x2/y2");
                            int x1 = GetInt(root, "x1", 0), y1 = GetInt(root, "y1", 0);
                            int x2 = GetInt(root, "x2", 0), y2 = GetInt(root, "y2", 0);
                            string button = GetString(root, "button", "left");
                            var mods = ParseModifiers(GetString(root, "modifiers", ""));
                            OnUi(() =>
                            {
                                foreach (var vk in mods) NativeInput.KeyDown(vk);
                                try { NativeInput.Drag(x1, y1, x2, y2, button); }
                                finally { for (int i = mods.Count - 1; i >= 0; i--) NativeInput.KeyUp(mods[i]); }
                                return true;
                            });
                            _onActivity();
                            _log($"drag {x1},{y1} -> {x2},{y2} ({button}{(mods.Count > 0 ? ", +penahan" : "")})");
                            return Ok();
                        }
                        case "/type":
                        {
                            string text = GetString(root, "text", "");
                            // Mesin Unicode SendInput sejak v2.4.0 —
                            // SendKeys lama tidak stabil untuk karakter
                            // di luar tata letak keyboard aktif.
                            OnUi(() => { NativeInput.TypeUnicode(text); return true; });
                            _onActivity();
                            string preview = text.Length > 30 ? text.Substring(0, 30) + "…" : text;
                            _log($"ketik: {preview} ({text.Length} karakter)");
                            return Ok();
                        }
                        case "/key":
                        {
                            string keys = GetString(root, "keys", "");
                            OnUi(() => { NativeInput.PressKey(keys); return true; });
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
                            OnUi(() =>
                            {
                                SetClipboardText(text);
                                NativeInput.PressKey("^v"); // Ctrl+V di jendela yang sedang fokus
                                return true;
                            });
                            _onActivity();
                            _log($"tempel teks ({text.Length} karakter)");
                            return Ok();
                        }
                        case "/focus":
                        {
                            string query = GetString(root, "title", "");
                            if (string.IsNullOrWhiteSpace(query))
                                return Response.Json("{\"ok\":false,\"error\":\"title kosong\"}", 400);
                            var wins = OnUi(NativeInput.ListWindows);
                            NativeInput.WindowInfo match = null;
                            foreach (var w in wins)
                            {
                                if (w.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
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
            return Response.JsonObject(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["exitCode"] = exitCode,
                ["timeout"] = !exited,
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
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
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

    private void HandleStream(NetworkStream stream, RequestHead head)
    {
        _onActivity();
        int maxWidth = ParseClamp(GetQueryParam(head.Query, "w"), 1280, 320, 1920);
        int quality = ParseClamp(GetQueryParam(head.Query, "q"), 60, 30, 90);
        int fps = ParseClamp(GetQueryParam(head.Query, "fps"), 10, 1, 30);
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
                    var hash = HashBitmap(bmp);
                    bool unchanged = lastHash != null && hash.SequenceEqual(lastHash);
                    // Frame identik tidak dikirim — kecuali sudah ±1 detik
                    // diam, kirim satu frame penjaga (keepalive).
                    bool keepaliveDue = frameStart - lastSentMs >= 1000;
                    if (!unchanged || keepaliveDue)
                    {
                        var jpeg = EncodeJpeg(bmp, quality);
                        var partHead = Encoding.ASCII.GetBytes(
                            "--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " +
                            jpeg.Length + "\r\n\r\n");
                        stream.Write(partHead, 0, partHead.Length);
                        stream.Write(jpeg, 0, jpeg.Length);
                        stream.Write(crlf, 0, crlf.Length);
                        stream.Flush();
                        lastHash = hash;
                        lastSentMs = clock.ElapsedMilliseconds;
                    }
                }

                // Kejar target fps: tidur hanya sisa waktu bingkai setelah
                // waktu tangkap+encode diperhitungkan.
                int sleep = (int)(frameMs - (clock.ElapsedMilliseconds - frameStart));
                if (sleep > 0) Thread.Sleep(sleep);
            }
        }
        catch
        {
            // Klien memutus stream / listener dimatikan — akhiri dengan diam.
        }
        finally
        {
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

    /// <summary>
    /// Tangkap SATU wilayah layar (v2.4.4), dijepit ke batas layar,
    /// lalu kecilkan ke lebar maks bila maxWidth > 0 dan wilayah
    /// lebih lebar. eff = wilayah efektif sesudah penjepitan.
    /// Aman dipanggil dari thread mana pun. Pemanggil membuang
    /// bitmap-nya.
    /// </summary>
    private static Bitmap CaptureRegionBitmap(Rectangle region, int maxWidth, out Rectangle eff)
    {
        var bounds = Screen.PrimaryScreen != null
            ? Screen.PrimaryScreen.Bounds
            : new Rectangle(0, 0, 1280, 720);
        eff = Rectangle.Intersect(bounds, region);
        if (eff.Width <= 0 || eff.Height <= 0) eff = bounds;
        var bmp = new Bitmap(eff.Width, eff.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(eff.Location, Point.Empty, eff.Size);
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
        var bounds = Screen.PrimaryScreen != null
            ? Screen.PrimaryScreen.Bounds
            : new Rectangle(0, 0, 1280, 720);
        var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
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

    private static byte[] EncodeJpeg(Bitmap bmp, long quality)
    {
        using (var ms = new MemoryStream())
        {
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
            return ms.ToArray();
        }
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.MimeType == "image/jpeg")
                return codec;
        }
        return null;
    }

    private static byte[] CaptureScreenshot()
    {
        var bounds = Screen.PrimaryScreen != null
            ? Screen.PrimaryScreen.Bounds
            : new Rectangle(0, 0, 1280, 720);
        using (var bmp = new Bitmap(bounds.Width, bounds.Height))
        {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    /// <summary>
    /// Screenshot satu wilayah saja (v2.4.0): wilayah dijepit ke
    /// batas layar; lebih ringan dikirim dan teks kecil lebih tajam
    /// dibaca daripada memotong hasil layar penuh di sisi Muse.
    /// </summary>
    private static byte[] CaptureScreenshotRegion(int x, int y, int w, int h)
    {
        var bounds = Screen.PrimaryScreen != null
            ? Screen.PrimaryScreen.Bounds
            : new Rectangle(0, 0, 1280, 720);
        var region = Rectangle.Intersect(bounds, new Rectangle(x, y, w, h));
        if (region.Width <= 0 || region.Height <= 0) region = bounds;
        using (var bmp = new Bitmap(region.Width, region.Height))
        {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(region.Location, Point.Empty, region.Size);
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
        var bounds = Screen.PrimaryScreen != null
            ? Screen.PrimaryScreen.Bounds
            : new Rectangle(0, 0, 1280, 720);
        var region = (w > 0 && h > 0)
            ? Rectangle.Intersect(bounds, new Rectangle(x, y, w, h))
            : bounds;
        if (region.Width <= 0 || region.Height <= 0) region = bounds;
        using (var bmp = new Bitmap(region.Width, region.Height))
        {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(region.Location, Point.Empty, region.Size);
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
        if (string.IsNullOrEmpty(text)) Clipboard.Clear();
        else Clipboard.SetText(text);
    }

    /// <summary>Parse angka query string lalu jepit ke rentang [min, max].</summary>
    private static int ParseClamp(string raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw, out int v)) v = fallback;
        if (v < min) v = min;
        if (v > max) v = max;
        return v;
    }

    private T OnUi<T>(Func<T> func)
    {
        if (_ui.InvokeRequired)
            return (T)_ui.Invoke(func);
        return func();
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
