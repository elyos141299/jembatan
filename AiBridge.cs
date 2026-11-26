using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Jembatan;

/// <summary>
/// Jembatan ke pendamping AI visual (v5.4.0): proses Python
/// (venv AI-Tools, torch CUDA) yang memuat YOLO (deteksi objek)
/// + EasyOCR (OCR) SEKALI ke VRAM GPU dan melayani HTTP lokal
/// 127.0.0.1:8770. Dimulai MALAS pada panggilan AI pertama
/// (pemuatan model ±15-30 dtk), dipakai ulang selama sehat, dan
/// dimatikan bersama server HANYA bila kamilah yang memulainya.
/// Semua kegagalan dilaporkan jujur lewat parameter error —
/// pemanggil memutuskan cadangan (OCR bawaan/UIA).
/// </summary>
public static class AiBridge
{
    private const string Base = "http://127.0.0.1:8770";
    private static readonly HttpClient Http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(180),
    };
    private static readonly object Lock = new object();
    private static Process _proc;
    private static bool _weStarted;
    private static string _lastError = "";
    private static DateTime _cacheAt = DateTime.MinValue;
    private static bool _cacheHealthy;

    public static string AiToolsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AI-Tools");
    public static string PythonExe => Path.Combine(AiToolsDir, "venv", "Scripts", "python.exe");
    public static string SidecarDir => Path.Combine(AiToolsDir, "sidecar");
    public static string SidecarScript => Path.Combine(SidecarDir, "ai_sidecar.py");
    public static bool Installed => File.Exists(PythonExe);

    public static bool Healthy()
    {
        try
        {
            using var cts = new CancellationTokenSource(900);
            using var req = new HttpRequestMessage(HttpMethod.Get, Base + "/health");
            using var resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Pastikan sidecar hidup; false + LastError bila gagal.</summary>
    public static bool EnsureRunning()
    {
        if (Healthy()) return true;
        lock (Lock)
        {
            if (Healthy()) return true;
            if (!Installed) { _lastError = "AI-Tools (Python venv) belum terpasang"; return false; }
            try
            {
                DeployScript();
                if (!File.Exists(SidecarScript)) { _lastError = "ai_sidecar.py tidak ditemukan"; return false; }
                var psi = new ProcessStartInfo
                {
                    FileName = PythonExe,
                    Arguments = "\"" + SidecarScript + "\"",
                    WorkingDirectory = AiToolsDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                _proc = new Process { StartInfo = psi };
                string logPath = Path.Combine(SidecarDir, "sidecar.log");
                _proc.OutputDataReceived += (_, e) => AppendLog(logPath, e.Data);
                _proc.ErrorDataReceived += (_, e) => AppendLog(logPath, e.Data);
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                _weStarted = true;
            }
            catch (Exception ex) { _lastError = ex.Message; return false; }
            for (int i = 0; i < 90; i++)
            {
                if (Healthy()) return true;
                try { if (_proc == null || _proc.HasExited) { _lastError = "sidecar mati saat memuat model"; return false; } }
                catch { _lastError = "sidecar mati saat memuat model"; return false; }
                Thread.Sleep(1000);
            }
            _lastError = "sidecar timeout saat memuat model (>90 dtk)";
            return false;
        }
    }

    private static void AppendLog(string path, string line)
    {
        if (line == null) return;
        try { File.AppendAllText(path, line + "\n"); } catch { /* abaikan */ }
    }

    /// <summary>Salin ai_sidecar.py bawaan aplikasi (di samping
    /// exe) ke AI-Tools\sidecar\ bila belum ada / lebih lama.</summary>
    private static void DeployScript()
    {
        Directory.CreateDirectory(SidecarDir);
        var bundled = Path.Combine(AppContext.BaseDirectory, "ai_sidecar.py");
        if (!File.Exists(bundled)) return;
        if (!File.Exists(SidecarScript) ||
            File.GetLastWriteTimeUtc(bundled) > File.GetLastWriteTimeUtc(SidecarScript))
            File.Copy(bundled, SidecarScript, true);
    }

    /// <summary>Deteksi objek YOLO di GPU. Mengembalikan JSON
    /// mentah sidecar (detections berkoordinat GAMBAR) atau null.</summary>
    public static string DetectJson(Bitmap bmp, out string error)
        => Call("/detect", bmp, out error);

    /// <summary>OCR EasyOCR di GPU -> OcrReader.Result dengan
    /// koordinat layar (offset wilayah ditambahkan di sini).</summary>
    public static OcrReader.Result OcrResult(Bitmap bmp, int offX, int offY, out string error)
    {
        var raw = Call("/ocr", bmp, out error);
        if (raw == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
            {
                error = root.TryGetProperty("error", out var eEl) ? eEl.GetString() : "sidecar menolak";
                return null;
            }
            var res = new OcrReader.Result { Text = root.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? "" : "" };
            if (root.TryGetProperty("words", out var words))
            {
                foreach (var w in words.EnumerateArray())
                {
                    res.Words.Add(new OcrReader.Word
                    {
                        Text = w.TryGetProperty("t", out var wt) ? wt.GetString() ?? "" : "",
                        Rect = new Rectangle(
                            offX + (w.TryGetProperty("x", out var wx) ? wx.GetInt32() : 0),
                            offY + (w.TryGetProperty("y", out var wy) ? wy.GetInt32() : 0),
                            w.TryGetProperty("w", out var ww) ? ww.GetInt32() : 0,
                            w.TryGetProperty("h", out var wh) ? wh.GetInt32() : 0),
                    });
                }
            }
            return res;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    private static string Call(string path, Bitmap bmp, out string error)
    {
        error = "";
        if (!EnsureRunning()) { error = _lastError; return null; }
        try
        {
            string b64;
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                b64 = Convert.ToBase64String(ms.ToArray());
            }
            using var content = new StringContent(
                "{\"image\":\"" + b64 + "\"}", Encoding.UTF8, "application/json");
            using var resp = Http.PostAsync(Base + path, content).GetAwaiter().GetResult();
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                error = "sidecar HTTP " + (int)resp.StatusCode + ": " +
                        (body.Length > 200 ? body.Substring(0, 200) : body);
                return null;
            }
            return body;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>Ringkasan untuk /health (status sidecar di-cache
    /// 10 detik agar probe kesehatan UI tetap ringan).</summary>
    public static Dictionary<string, object> HealthInfo()
    {
        if (DateTime.UtcNow - _cacheAt > TimeSpan.FromSeconds(10))
        {
            _cacheHealthy = Healthy();
            _cacheAt = DateTime.UtcNow;
        }
        return new Dictionary<string, object>
        {
            ["installed"] = Installed,
            ["sidecar"] = _cacheHealthy ? "online" : "offline",
            ["device"] = "cuda",
        };
    }

    /// <summary>Matikan sidecar HANYA bila kami yang memulainya.</summary>
    public static void Shutdown()
    {
        try
        {
            if (_weStarted && _proc != null && !_proc.HasExited)
                _proc.Kill(true);
        }
        catch { /* abaikan */ }
    }
}
