using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Jembatan;

/// <summary>
/// Perkakas media (v5.6.0): FFmpeg/ffprobe yang terpasang di PC
/// (lewat WinGet — paket Gyan.FFmpeg) dijalankan sebagai proses
/// berargumen rapi (ArgumentList, TANPA shell) untuk probe,
/// thumbnail, trim, dan convert berkas video/audio lokal.
/// Semua operasi berbatas waktu dan mengembalikan ringkasan
/// terstruktur; galat membawa ekor stderr ffmpeg agar bisa
/// didiagnosis dari jauh.
/// </summary>
public static class MediaTools
{
    private static readonly object Lock = new object();
    private static bool _searched;
    private static string _ffmpeg;
    private static string _ffprobe;
    private static string _version;

    private static void Locate()
    {
        lock (Lock)
        {
            if (_searched) return;
            _searched = true;
            var dirs = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WinGet", "Links"),
                @"C:\ffmpeg\bin",
            };
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var d in pathEnv.Split(';'))
                if (d.Trim().Length > 0) dirs.Add(d.Trim());
            foreach (var d in dirs)
            {
                try
                {
                    string f = Path.Combine(d, "ffmpeg.exe");
                    if (_ffmpeg == null && File.Exists(f)) _ffmpeg = f;
                    string p = Path.Combine(d, "ffprobe.exe");
                    if (_ffprobe == null && File.Exists(p)) _ffprobe = p;
                }
                catch { /* direktori PATH tak terbaca — lewati */ }
                if (_ffmpeg != null && _ffprobe != null) break;
            }
        }
    }

    public static bool Available { get { Locate(); return _ffmpeg != null; } }

    public static string Version()
    {
        Locate();
        if (_ffmpeg == null) return "";
        if (_version != null) return _version;
        try
        {
            var r = Run(_ffmpeg, new[] { "-version" }, 5000);
            string first = (r.Stdout ?? "").Split('\n')[0].Trim();
            int cut = first.IndexOf(" Copyright", StringComparison.Ordinal);
            _version = cut > 0 ? first.Substring(0, cut).Trim() : first;
        }
        catch { _version = ""; }
        return _version;
    }

    public static Dictionary<string, object> HealthInfo() => new Dictionary<string, object>
    {
        ["ffmpeg"] = Available,
        ["ffprobe"] = _ffprobe != null,
        ["version"] = Version(),
    };

    private sealed class RunResult
    {
        public int ExitCode = -1;
        public string Stdout = "";
        public string Stderr = "";
        public bool TimedOut;
        public long Ms;
    }

    private static RunResult Run(string exe, string[] args, int timeoutMs)
    {
        var hasil = new RunResult();
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using (var proc = Process.Start(psi))
            {
                var tOut = proc.StandardOutput.ReadToEndAsync();
                var tErr = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(timeoutMs))
                {
                    hasil.TimedOut = true;
                    try { proc.Kill(true); } catch { /* sudah mati */ }
                    proc.WaitForExit(3000);
                }
                hasil.ExitCode = hasil.TimedOut ? -1 : proc.ExitCode;
                hasil.Stdout = tOut.Wait(3000) ? tOut.Result : "";
                hasil.Stderr = tErr.Wait(3000) ? tErr.Result : "";
            }
        }
        catch (Exception ex)
        {
            hasil.Stderr = ex.Message;
        }
        hasil.Ms = sw.ElapsedMilliseconds;
        return hasil;
    }

    private static string Tail(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        return s.Length <= n ? s : s.Substring(s.Length - n);
    }

    private static Dictionary<string, object> Fail(string error, RunResult r) =>
        new Dictionary<string, object>
        {
            ["ok"] = false,
            ["error"] = error,
            ["detail"] = r != null ? Tail(r.Stderr, 300) : "",
            ["timedOut"] = r != null && r.TimedOut,
        };

    private static void EnsureOutDir(string outPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>Probe: metadata berkas media dari ffprobe (JSON).</summary>
    public static Dictionary<string, object> Probe(string path)
    {
        Locate();
        if (_ffprobe == null)
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = "ffprobe tidak ditemukan di PC" };
        var r = Run(_ffprobe, new[]
        {
            "-v", "error", "-show_format", "-show_streams", "-of", "json", path,
        }, 30000);
        if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Stdout)) return Fail("ffprobe gagal membaca berkas", r);
        try
        {
            using (var doc = JsonDocument.Parse(r.Stdout))
            {
                var root = doc.RootElement;
                var hasil = new Dictionary<string, object> { ["ok"] = true, ["path"] = path };
                if (root.TryGetProperty("format", out var fmt))
                {
                    if (fmt.TryGetProperty("duration", out var dur) &&
                        double.TryParse(dur.GetString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double dsec))
                        hasil["durationSec"] = Math.Round(dsec, 2);
                    if (fmt.TryGetProperty("size", out var sz) &&
                        long.TryParse(sz.GetString(), out long lsz))
                        hasil["sizeBytes"] = lsz;
                    if (fmt.TryGetProperty("format_name", out var fn))
                        hasil["format"] = fn.GetString();
                }
                var streams = new List<object>();
                if (root.TryGetProperty("streams", out var arr))
                {
                    foreach (var s in arr.EnumerateArray())
                    {
                        var sd = new Dictionary<string, object>();
                        if (s.TryGetProperty("codec_type", out var ct)) sd["type"] = ct.GetString();
                        if (s.TryGetProperty("codec_name", out var cn)) sd["codec"] = cn.GetString();
                        if (s.TryGetProperty("width", out var w)) sd["width"] = w.GetInt32();
                        if (s.TryGetProperty("height", out var h)) sd["height"] = h.GetInt32();
                        if (s.TryGetProperty("avg_frame_rate", out var fr))
                        {
                            string f = fr.GetString() ?? "";
                            var parts = f.Split('/');
                            if (parts.Length == 2 &&
                                double.TryParse(parts[0], out double num) &&
                                double.TryParse(parts[1], out double den) && den > 0)
                                sd["fps"] = Math.Round(num / den, 2);
                        }
                        if (s.TryGetProperty("channels", out var ch)) sd["channels"] = ch.GetInt32();
                        streams.Add(sd);
                    }
                }
                hasil["streams"] = streams;
                return hasil;
            }
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = "JSON ffprobe tak terbaca: " + ex.Message };
        }
    }

    /// <summary>Thumbnail: satu bingkai pada detik tertentu → gambar.</summary>
    public static Dictionary<string, object> Thumb(string path, double atSec, string outPath, int timeoutMs)
    {
        Locate();
        if (_ffmpeg == null)
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = "ffmpeg tidak ditemukan di PC" };
        EnsureOutDir(outPath);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var r = Run(_ffmpeg, new[]
        {
            "-y", "-ss", atSec.ToString("0.###", inv), "-i", path,
            "-frames:v", "1", "-q:v", "2", outPath,
        }, timeoutMs);
        if (r.ExitCode != 0 || !File.Exists(outPath)) return Fail("ffmpeg gagal membuat thumbnail", r);
        return new Dictionary<string, object>
        {
            ["ok"] = true, ["out"] = outPath,
            ["bytes"] = new FileInfo(outPath).Length, ["ms"] = r.Ms,
        };
    }

    /// <summary>Trim: potong [start, start+dur) — stream copy bila
    /// diminta (cepat, tanpa encode ulang), selain itu H.264/AAC.</summary>
    public static Dictionary<string, object> Trim(
        string path, double startSec, double durSec, string outPath, bool copy, int timeoutMs)
    {
        Locate();
        if (_ffmpeg == null)
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = "ffmpeg tidak ditemukan di PC" };
        EnsureOutDir(outPath);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "-y", "-ss", startSec.ToString("0.###", inv), "-i", path,
            "-t", durSec.ToString("0.###", inv),
        };
        if (copy) { args.Add("-c"); args.Add("copy"); }
        else { args.Add("-c:v"); args.Add("libx264"); args.Add("-c:a"); args.Add("aac"); }
        args.Add(outPath);
        var r = Run(_ffmpeg, args.ToArray(), timeoutMs);
        if (r.ExitCode != 0 || !File.Exists(outPath)) return Fail("ffmpeg gagal memotong berkas", r);
        return new Dictionary<string, object>
        {
            ["ok"] = true, ["out"] = outPath,
            ["bytes"] = new FileInfo(outPath).Length, ["ms"] = r.Ms, ["copied"] = copy,
        };
    }

    /// <summary>Convert: transkode menurut ekstensi tujuan —
    /// .wav → PCM 16-bit 16 kHz mono (siap Whisper), .mp3 → lame,
    /// .mp4/.mkv → H.264/AAC, selain itu encoder bawaan ffmpeg.</summary>
    public static Dictionary<string, object> Convert(string path, string outPath, int timeoutMs)
    {
        Locate();
        if (_ffmpeg == null)
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = "ffmpeg tidak ditemukan di PC" };
        EnsureOutDir(outPath);
        string ext = Path.GetExtension(outPath).ToLowerInvariant();
        var args = new List<string> { "-y", "-i", path };
        switch (ext)
        {
            case ".wav":
                args.AddRange(new[] { "-vn", "-acodec", "pcm_s16le", "-ar", "16000", "-ac", "1" });
                break;
            case ".mp3":
                args.AddRange(new[] { "-vn", "-codec:a", "libmp3lame", "-q:a", "2" });
                break;
            case ".mp4":
            case ".mkv":
            case ".mov":
                args.AddRange(new[] { "-codec:v", "libx264", "-codec:a", "aac" });
                break;
        }
        args.Add(outPath);
        var r = Run(_ffmpeg, args.ToArray(), timeoutMs);
        if (r.ExitCode != 0 || !File.Exists(outPath)) return Fail("ffmpeg gagal mengonversi berkas", r);
        return new Dictionary<string, object>
        {
            ["ok"] = true, ["out"] = outPath,
            ["bytes"] = new FileInfo(outPath).Length, ["ms"] = r.Ms,
        };
    }
}
