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
                var resp = Route(new Request { Method = head.Method, Path = head.Path, Body = body });
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

    // ---------- Routing ----------

    private Response Route(Request req)
    {
        try
        {
            if (req.Method == "GET" && req.Path == "/ping")
            {
                _onActivity();
                _log("ping — Muse mengecek koneksi");
                return Response.Json("{\"ok\":true,\"app\":\"jembatan\",\"version\":\"2.3.4\",\"admin\":"
                    + (Settings.IsElevated() ? "true" : "false") + "}");
            }

            if (req.Method == "GET" && req.Path == "/screenshot")
            {
                _onActivity();
                var png = OnUi(CaptureScreenshot);
                _log("screenshot layar diambil");
                return new Response { ContentType = "image/png", BodyBytes = png };
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
                _log("info sistem diminta");
                return Response.JsonObject(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["version"] = "2.3.4",
                    ["admin"] = Settings.IsElevated(),
                    ["pc"] = Environment.MachineName,
                    ["windows"] = Environment.OSVersion.VersionString,
                    ["screen"] = bounds.Width + "x" + bounds.Height,
                    ["uptimeSec"] = (int)(Environment.TickCount64 / 1000),
                    ["jembatanPort"] = Port,
                });
            }

            if (req.Method == "POST")
            {
                using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.Body) ? "{}" : req.Body))
                {
                    var root = doc.RootElement;
                    switch (req.Path)
                    {
                        case "/move":
                        {
                            int x = GetInt(root, "x", 0), y = GetInt(root, "y", 0);
                            OnUi(() => { NativeInput.Move(x, y); return true; });
                            _onActivity();
                            _log($"geser mouse ke {x},{y}");
                            return Ok();
                        }
                        case "/click":
                        {
                            int x = GetInt(root, "x", 0), y = GetInt(root, "y", 0);
                            string button = GetString(root, "button", "left");
                            OnUi(() => { NativeInput.Click(x, y, button); return true; });
                            _onActivity();
                            _log($"klik {x},{y} ({button})");
                            return Ok();
                        }
                        case "/scroll":
                        {
                            int x = GetInt(root, "x", 0), y = GetInt(root, "y", 0);
                            int delta = GetInt(root, "delta", 0);
                            OnUi(() => { NativeInput.Scroll(x, y, delta); return true; });
                            _onActivity();
                            _log($"scroll di {x},{y} (delta {delta})");
                            return Ok();
                        }
                        case "/drag":
                        {
                            int x1 = GetInt(root, "x1", 0), y1 = GetInt(root, "y1", 0);
                            int x2 = GetInt(root, "x2", 0), y2 = GetInt(root, "y2", 0);
                            string button = GetString(root, "button", "left");
                            OnUi(() => { NativeInput.Drag(x1, y1, x2, y2, button); return true; });
                            _onActivity();
                            _log($"drag {x1},{y1} -> {x2},{y2} ({button})");
                            return Ok();
                        }
                        case "/type":
                        {
                            string text = GetString(root, "text", "");
                            OnUi(() => { NativeInput.TypeText(text); return true; });
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

    private static Response Ok()
    {
        return Response.Json("{\"ok\":true}");
    }

    // ---------- Aksi-aksi ----------

    private Response Exec(JsonElement root)
    {
        string command = GetString(root, "command", "");
        string args = GetString(root, "args", "");
        if (string.IsNullOrWhiteSpace(command))
            return Response.Json("{\"ok\":false,\"error\":\"command kosong\"}", 400);

        int timeoutMs = GetInt(root, "timeoutMs", DefaultExecTimeoutMs);
        if (timeoutMs < 1000) timeoutMs = 1000;
        if (timeoutMs > MaxExecTimeoutMs) timeoutMs = MaxExecTimeoutMs;

        _onActivity();
        _log("jalanin: " + command + (args.Length > 0 ? " " + args : ""));

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
        int fps = ParseClamp(GetQueryParam(head.Query, "fps"), 10, 1, 15);
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
                using (var bmp = CaptureScaledBitmap(maxWidth))
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

    private static string GetString(JsonElement root, string name, string fallback)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? fallback;
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
}
