using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jembatan;

/// <summary>
/// Otomasi peramban lewat Chrome DevTools Protocol mentah
/// (v5.11.0 — Bagian E rencana "Playwright/CDP").
///
/// KEPUTUSAN TEKNIS: rencana menyebut Playwright/CDP. Yang
/// dibangun adalah CDP langsung lewat WebSocket bawaan .NET —
/// TANPA dependensi Playwright — karena: (1) exe MuseLink
/// terbit sebagai berkas tunggal dan driver Playwright (Node
/// + folder .playwright) merusak model itu, (2) fungsi yang
/// dijanjikan rencana sama persis: kemudikan Chrome TERPASANG
/// lewat CDP dengan PROFIL KHUSUS otomasi, cari elemen via
/// DOM, klik & isi nilai di tingkat DOM, baca isi halaman,
/// tunggu elemen dengan polling (pola Smart Waiting yang
/// sama seperti /waitfor). Profil utama Chrome user TIDAK
/// PERNAH disentuh (Chrome melarang CDP di profil bawaan) —
/// profil otomasi tinggal di LocalAppData\Jembatan\
/// browser-profile dengan loginnya sendiri.
///
/// Endpoint debug hanya mengikat 127.0.0.1 (bawaan Chrome).
/// </summary>
public static class CdpBrowser
{
    private const int Port = 9223;
    private static readonly object Lock = new object();
    private static ClientWebSocket _ws;
    private static CancellationTokenSource _cts;
    private static Process _chrome;
    private static string _sessionId = "";
    private static int _nextId;
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> Pending =
        new ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>();
    private static string _lastError = "";

    public static string LastError => _lastError;
    public static bool IsRunning
    {
        get { lock (Lock) { return _ws != null && _ws.State == WebSocketState.Open; } }
    }

    public static string ProfileDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jembatan", "browser-profile");

    public sealed class Result
    {
        public bool Ok;
        public string Error = "";
        public string ValueJson = "null"; // nilai JS sebagai JSON mentah
        public string Text = "";          // nilai sebagai teks polos
    }

    private static string FindChrome()
    {
        string[] cands =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
        };
        foreach (var c in cands) if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>Mulai Chrome otomasi + sambungkan CDP. Aman
    /// dipanggil berulang (sudah jalan = langsung ok).</summary>
    public static Result Start()
    {
        lock (Lock)
        {
            if (_ws != null && _ws.State == WebSocketState.Open)
                return new Result { Ok = true, Text = "sudah berjalan" };
        }
        try
        {
            return StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) { _lastError = ex.Message; return new Result { Ok = false, Error = ex.Message }; }
    }

    private static async Task<Result> StartAsync()
    {
        string chrome = FindChrome();
        if (chrome == null) return new Result { Ok = false, Error = "chrome.exe tidak ditemukan" };
        Directory.CreateDirectory(ProfileDir);
        // Chrome otomasi mungkin sudah hidup dari sesi lalu:
        // coba sambung dulu sebelum meluncurkan yang baru.
        string wsUrl = await GetBrowserWsAsync().ConfigureAwait(false);
        if (wsUrl == null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = chrome,
                Arguments = "--remote-debugging-port=" + Port +
                            " --remote-debugging-address=127.0.0.1" +
                            " --user-data-dir=\"" + ProfileDir + "\"" +
                            " --no-first-run --no-default-browser-check about:blank",
                UseShellExecute = false,
            };
            _chrome = Process.Start(psi);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20000)
            {
                wsUrl = await GetBrowserWsAsync().ConfigureAwait(false);
                if (wsUrl != null) break;
                await Task.Delay(400).ConfigureAwait(false);
            }
            if (wsUrl == null) return new Result { Ok = false, Error = "Chrome otomasi tidak membuka port debug dalam 20 detik" };
        }
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token).ConfigureAwait(false);
        _ = Task.Run(ReceiveLoop);
        // Lampirkan ke target halaman pertama (mode pipih).
        var targets = await SendAsync("Target.getTargets", new Dictionary<string, object>()).ConfigureAwait(false);
        if (!targets.Ok) return new Result { Ok = false, Error = targets.Error };
        string targetId = null;
        using (var doc = JsonDocument.Parse(targets.ValueJson))
        {
            foreach (var t in doc.RootElement.GetProperty("targetInfos").EnumerateArray())
            {
                if (t.GetProperty("type").GetString() == "page")
                { targetId = t.GetProperty("targetId").GetString(); break; }
            }
        }
        if (targetId == null) return new Result { Ok = false, Error = "tidak ada target halaman" };
        var att = await SendAsync("Target.attachToTarget",
            new Dictionary<string, object> { ["targetId"] = targetId, ["flatten"] = true }).ConfigureAwait(false);
        if (!att.Ok) return new Result { Ok = false, Error = att.Error };
        using (var doc = JsonDocument.Parse(att.ValueJson))
            _sessionId = doc.RootElement.GetProperty("sessionId").GetString() ?? "";
        await SendAsync("Page.enable", new Dictionary<string, object>()).ConfigureAwait(false);
        await SendAsync("Runtime.enable", new Dictionary<string, object>()).ConfigureAwait(false);
        return new Result { Ok = true, Text = "tersambung ke Chrome otomasi (profil khusus)" };
    }

    private static async Task<string> GetBrowserWsAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var json = await http.GetStringAsync("http://127.0.0.1:" + Port + "/json/version").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
        }
        catch { return null; }
    }

    private static async Task ReceiveLoop()
    {
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (_ws != null && _ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
                using var doc = JsonDocument.Parse(sb.ToString());
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) &&
                    Pending.TryRemove(idEl.GetInt32(), out var tcs))
                {
                    if (root.TryGetProperty("error", out var err))
                        tcs.TrySetResult(err);
                    else if (root.TryGetProperty("result", out var res))
                        tcs.TrySetResult(res.Clone());
                    else
                        tcs.TrySetResult(default);
                }
                // Kejadian (events) tidak dipakai ronde ini.
            }
        }
        catch { /* soket tutup = sesi selesai */ }
    }

    /// <summary>Kirim satu perintah CDP (ke sesi halaman bila
    /// sudah terlampir) dan tunggu jawabannya.</summary>
    public static Result Send(string method, Dictionary<string, object> pars, int timeoutMs = 15000)
    {
        try { return SendAsync(method, pars, timeoutMs).GetAwaiter().GetResult(); }
        catch (Exception ex) { return new Result { Ok = false, Error = ex.Message }; }
    }

    private static async Task<Result> SendAsync(string method, Dictionary<string, object> pars, int timeoutMs = 15000)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            return new Result { Ok = false, Error = "peramban otomasi belum tersambung — panggil /browser/start dulu" };
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = tcs;
        var msg = new Dictionary<string, object>
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = pars ?? new Dictionary<string, object>(),
        };
        if (!string.IsNullOrEmpty(_sessionId) &&
            !method.StartsWith("Target.") && !method.StartsWith("Browser."))
            msg["sessionId"] = _sessionId;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (done != tcs.Task)
        {
            Pending.TryRemove(id, out _);
            return new Result { Ok = false, Error = "CDP timeout " + timeoutMs + "ms: " + method };
        }
        var el = tcs.Task.Result;
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("message", out var mErr) && method != null && el.TryGetProperty("code", out _))
            return new Result { Ok = false, Error = "CDP " + method + ": " + mErr.GetString() };
        return new Result { Ok = true, ValueJson = el.ValueKind == JsonValueKind.Undefined ? "null" : el.GetRawText() };
    }

    // ---------- Operasi tingkat halaman ----------

    /// <summary>Navigasi ke URL.</summary>
    public static Result Navigate(string url)
        => Send("Page.navigate", new Dictionary<string, object> { ["url"] = url });

    /// <summary>Evaluasi JavaScript; nilai kembali sebagai
    /// JSON mentah + teks polos (untuk nilai string/angka).</summary>
    public static Result Eval(string expression)
    {
        var r = Send("Runtime.evaluate", new Dictionary<string, object>
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true,
        });
        if (!r.Ok) return r;
        try
        {
            using var doc = JsonDocument.Parse(r.ValueJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("exceptionDetails", out var ex))
                return new Result { Ok = false, Error = "JS: " + (ex.TryGetProperty("text", out var tx) ? tx.GetString() : "pengecualian") };
            var res = root.GetProperty("result");
            var outp = new Result { Ok = true };
            if (res.TryGetProperty("value", out var v))
            {
                outp.ValueJson = v.GetRawText();
                outp.Text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
            }
            else if (res.TryGetProperty("description", out var d))
            {
                outp.Text = d.GetString() ?? "";
                outp.ValueJson = JsonSerializer.Serialize(outp.Text);
            }
            return outp;
        }
        catch (Exception ex) { return new Result { Ok = false, Error = ex.Message }; }
    }

    /// <summary>Tunggu elemen muncul (polling Smart Waiting,
    /// pola yang sama seperti /waitfor): true bila ketemu
    /// sebelum batas waktu.</summary>
    public static Result WaitFor(string selector, int timeoutMs)
    {
        if (timeoutMs < 250) timeoutMs = 250;
        if (timeoutMs > 120000) timeoutMs = 120000;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var r = Eval("!!document.querySelector(" + JsonSerializer.Serialize(selector) + ")");
            if (r.Ok && r.Text == "true")
                return new Result { Ok = true, Text = "ketemu dalam " + sw.ElapsedMilliseconds + "ms" };
            Thread.Sleep(250);
        }
        return new Result { Ok = false, Error = "elemen tidak muncul dalam " + timeoutMs + "ms: " + selector };
    }

    /// <summary>Klik elemen di tingkat DOM: gulir ke
    /// pandangan, ambil pusat kotaknya, kirim kejadian
    /// mouse CDP (bukan mouse fisik).</summary>
    public static Result ClickSelector(string selector)
    {
        var js = "(function(){var el=document.querySelector(" + JsonSerializer.Serialize(selector) +
                 ");if(!el)return null;el.scrollIntoView({block:'center'});" +
                 "var r=el.getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()";
        var pos = Eval(js);
        if (!pos.Ok) return pos;
        if (pos.ValueJson == "null" || pos.Text == "null")
            return new Result { Ok = false, Error = "elemen tidak ditemukan: " + selector };
        double x, y;
        using (var doc = JsonDocument.Parse(pos.ValueJson))
        {
            x = doc.RootElement.GetProperty("x").GetDouble();
            y = doc.RootElement.GetProperty("y").GetDouble();
        }
        var down = Send("Input.dispatchMouseEvent", new Dictionary<string, object>
        { ["type"] = "mousePressed", ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = 1 });
        if (!down.Ok) return down;
        return Send("Input.dispatchMouseEvent", new Dictionary<string, object>
        { ["type"] = "mouseReleased", ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = 1 });
    }

    /// <summary>Isi nilai elemen formulir di tingkat DOM:
    /// fokus, pakai penyetel nilai bawaan (agar kerangka
    /// seperti React ikut tahu), lalu tembakkan kejadian
    /// input+change. Tanpa ketikan fisik sama sekali.</summary>
    public static Result SetValue(string selector, string value)
    {
        var js = "(function(){var el=document.querySelector(" + JsonSerializer.Serialize(selector) +
                 ");if(!el)return false;el.focus();" +
                 "var proto=el instanceof HTMLTextAreaElement?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;" +
                 "var set=Object.getOwnPropertyDescriptor(proto,'value'); " +
                 "if(set&&set.set){set.set.call(el," + JsonSerializer.Serialize(value) + ");}" +
                 "else{el.value=" + JsonSerializer.Serialize(value) + ";}" +
                 "el.dispatchEvent(new Event('input',{bubbles:true}));" +
                 "el.dispatchEvent(new Event('change',{bubbles:true}));return true;})()";
        var r = Eval(js);
        if (!r.Ok) return r;
        if (r.Text != "true") return new Result { Ok = false, Error = "elemen tidak ditemukan: " + selector };
        return new Result { Ok = true, Text = "nilai terisi" };
    }

    /// <summary>Teks halaman (innerText body atau elemen).</summary>
    public static Result GetText(string selector)
    {
        var js = string.IsNullOrEmpty(selector)
            ? "document.body?document.body.innerText:''"
            : "(function(){var el=document.querySelector(" + JsonSerializer.Serialize(selector) + ");return el?el.innerText:null;})()";
        var r = Eval(js);
        if (!r.Ok) return r;
        if (r.ValueJson == "null") return new Result { Ok = false, Error = "elemen tidak ditemukan: " + selector };
        return r;
    }

    /// <summary>Putuskan CDP. closeBrowser=true ikut
    /// menutup Chrome otomasi (profil & loginnya tetap
    /// tersimpan untuk sesi berikutnya).</summary>
    public static Result Stop(bool closeBrowser)
    {
        try
        {
            if (closeBrowser && IsRunning)
                Send("Browser.close", new Dictionary<string, object>(), 3000);
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Dispose(); } catch { }
            _ws = null; _sessionId = "";
            return new Result { Ok = true, Text = "terputus" };
        }
        catch (Exception ex) { return new Result { Ok = false, Error = ex.Message }; }
    }
}
