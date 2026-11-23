using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
/// KEAMANAN v1 — TANPA password/token, dengan sadar:
/// Perlindungan layanan ini adalah jaringan Tailscale milik user sendiri.
/// Port 8765 hanya bisa dijangkau oleh perangkat yang SUDAH disetujui
/// user di admin Tailscale-nya (PC ini + VM Muse bernama "muse").
/// Jangan pernah mengekspos port ini ke internet publik (port forwarding).
/// </summary>
public sealed class AgentServer
{
    public const int Port = 8765;
    private const int ExecTimeoutMs = 60000;
    private const int MaxOutputChars = 8000;

    private readonly Control _ui;               // jendela utama, untuk Invoke ke thread UI
    private readonly Action<string> _log;       // tulis ke log jendela
    private readonly Action _onActivity;        // dipanggil tiap ada permintaan masuk

    private TcpListener _listener;
    private CancellationTokenSource _cts;

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

    private sealed class Request
    {
        public string Method;
        public string Path;
        public string Body;
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
                var req = ReadRequest(stream);
                if (req == null) return;
                var resp = Route(req);
                WriteResponse(stream, resp);
            }
            catch
            {
                // Koneksi putus di tengah jalan dsb. — tidak perlu mengganggu user.
            }
        }
    }

    private static Request ReadRequest(NetworkStream stream)
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

        string body = "";
        if (contentLength > 0)
        {
            if (contentLength > 1048576) contentLength = 1048576; // batas waras 1 MB
            var buf = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = stream.Read(buf, read, contentLength - read);
                if (n == 0) break;
                read += n;
            }
            body = Encoding.UTF8.GetString(buf, 0, read);
        }

        string path = parts[1];
        int q = path.IndexOf('?');
        if (q >= 0) path = path.Substring(0, q);

        return new Request { Method = parts[0].ToUpperInvariant(), Path = path, Body = body };
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
                return Response.Json("{\"ok\":true,\"app\":\"jembatan\",\"version\":\"1.0\"}");
            }

            if (req.Method == "GET" && req.Path == "/screenshot")
            {
                _onActivity();
                var png = OnUi(CaptureScreenshot);
                _log("screenshot layar diambil");
                return new Response { ContentType = "image/png", BodyBytes = png };
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

            bool exited = p.WaitForExit(ExecTimeoutMs);
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
