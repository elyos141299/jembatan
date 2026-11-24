using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Jembatan;

/// <summary>
/// Baca layar sebagai TEKS (v2.4.4) memakai mesin OCR bawaan
/// Windows (Windows.Media.Ocr lewat WinRT — makanya
/// TargetFramework diarahkan ke permukaan SDK Windows 10 19041
/// di Jembatan.csproj).
///
/// Alasan keberadaannya terukur dari audit kecepatan #1: biaya
/// panggilan didominasi UKURAN muatan yang menyeberangi relay
/// Tailscale (Indonesia -> New York -> VM). Respons OCR berupa
/// teks ±1-2KB tiba secepat /ping sambil membawa info yang bagi
/// Muse lebih berguna daripada bingkai JPEG puluhan KB: teks
/// persisnya PLUS kotak koordinat tiap kata, jadi kata di layar
/// bisa langsung diklik berdasar isinya.
///
/// Bahasa mesin mengikuti profil bahasa Windows user; cadangan
/// Inggris (en-US). Bila komponen OCR belum terpasang sama
/// sekali, Read melempar InvalidOperationException dan endpoint
/// /ocr menjawab error yang jelas — aplikasi tidak terganggu.
/// </summary>
public static class OcrReader
{
    /// <summary>Satu kata terbaca + kotak batasnya (koordinat layar absolut).</summary>
    public sealed class Word
    {
        public string Text = "";
        public Rectangle Rect;
    }

    /// <summary>Hasil baca: teks utuh + daftar kata (maks 600).</summary>
    public sealed class Result
    {
        public string Text = "";
        public List<Word> Words = new List<Word>();
    }

    /// <summary>
    /// Baca satu bitmap wilayah layar. offsetX/offsetY = titik
    /// asal wilayah di layar, ditambahkan ke kotak tiap kata agar
    /// koordinatnya absolut. Berjalan di thread latar (WinRT async
    /// tidak menyentuh thread UI).
    /// </summary>
    public static Result Read(Bitmap bmp, int offsetX, int offsetY)
    {
        return Task.Run(() => ReadAsync(bmp, offsetX, offsetY, 1.0)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Baca DIPERKUAT (v2.6.0): bitmap diperbesar dulu (maks
    /// 2x, dimensi terbesar dibatasi 2600) + abu-abu kontras
    /// tinggi — teks kecil yang luput dari mesin OCR pada
    /// ukuran asli terbaca jauh lebih sering. Kotak kata
    /// dipetakan kembali ke koordinat asli lewat faktor skala.
    /// </summary>
    public static Result ReadEnhanced(Bitmap bmp, int offsetX, int offsetY)
    {
        int maxDim = Math.Max(bmp.Width, bmp.Height);
        double f = Math.Min(2.0, 2600.0 / Math.Max(1, maxDim));
        if (f < 1.0) f = 1.0;
        using (var up = Enhance(bmp, f))
            return Task.Run(() => ReadAsync(up, offsetX, offsetY, f)).GetAwaiter().GetResult();
    }

    /// <summary>Perbesar + abu-abu + kontras untuk OCR.</summary>
    private static Bitmap Enhance(Bitmap src, double f)
    {
        int w = Math.Max(1, (int)(src.Width * f));
        int h = Math.Max(1, (int)(src.Height * f));
        var dst = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        float c = 1.35f;
        float t = 0.5f * (1f - c);
        var cm = new System.Drawing.Imaging.ColorMatrix(new[]
        {
            new[] { 0.299f * c, 0.587f * c, 0.114f * c, 0f, 0f },
            new[] { 0.299f * c, 0.587f * c, 0.114f * c, 0f, 0f },
            new[] { 0.299f * c, 0.587f * c, 0.114f * c, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { t, t, t, 0f, 1f },
        });
        using (var g = Graphics.FromImage(dst))
        using (var attrs = new System.Drawing.Imaging.ImageAttributes())
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            attrs.SetColorMatrix(cm);
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        }
        return dst;
    }


    // ===== Mesin OCR kedua: Tesseract (v2.6.5) =====
    // Windows tidak menyediakan OCR Bahasa Indonesia sama
    // sekali (capability Language.OCR id-ID memang tidak
    // ada di daftar Microsoft), jadi MuseLink membawa
    // mesinnya sendiri: tesseract.exe + data ind/eng di
    // subfolder "tesseract" di samping exe aplikasi
    // (diunduh & diverifikasi hash-nya di CI). Berkasnya
    // tidak ada -> mesin ini diam-diam tidak aktif.

    private static string TessExe =>
        Path.Combine(AppContext.BaseDirectory, "tesseract", "tesseract.exe");

    private static string TessDataDir =>
        Path.Combine(AppContext.BaseDirectory, "tesseract", "tessdata");

    /// <summary>True bila berkas mesin Tesseract terpasang.</summary>
    public static bool TesseractAvailable =>
        File.Exists(TessExe) && File.Exists(Path.Combine(TessDataDir, "ind.traineddata"));

    /// <summary>
    /// Baca bitmap dengan Tesseract (ind+eng, PSM 6).
    /// Koordinat kata digeser offsetX/offsetY seperti
    /// Read. Kegagalan apa pun -> hasil kosong (metode
    /// ini tidak pernah melempar).
    /// </summary>
    public static Result ReadTesseract(Bitmap bmp, int offsetX, int offsetY)
    {
        var hasil = new Result();
        if (!TesseractAvailable) return hasil;
        string tmp = Path.Combine(Path.GetTempPath(),
            "muselink-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = TessExe,
                Arguments = "\"" + tmp + "\" stdout --tessdata-dir \"" + TessDataDir + "\" -l ind+eng --psm 6 -c tessedit_create_tsv=1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                var baca = proc.StandardOutput.ReadToEndAsync();
                if (!proc.WaitForExit(20000))
                {
                    try { proc.Kill(); } catch { }
                    return hasil;
                }
                string tsv = baca.GetAwaiter().GetResult();
                var potongan = new List<string>();
                foreach (var baris in tsv.Split('\n'))
                {
                    var kol = baris.TrimEnd('\r').Split('\t');
                    if (kol.Length < 12) continue;
                    if (kol[0] != "5") continue; // level kata
                    string teks = kol[11].Trim();
                    if (teks.Length == 0) continue;
                    if (!int.TryParse(kol[6], out int lx) || !int.TryParse(kol[7], out int ly)
                        || !int.TryParse(kol[8], out int lw) || !int.TryParse(kol[9], out int lh))
                        continue;
                    hasil.Words.Add(new Word
                    {
                        Text = teks,
                        Rect = new Rectangle(offsetX + lx, offsetY + ly, lw, lh),
                    });
                    potongan.Add(teks);
                    if (hasil.Words.Count >= 600) break;
                }
                hasil.Text = string.Join(" ", potongan);
            }
        }
        catch { /* mesin kedua gagal -> hasil kosong */ }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        return hasil;
    }

    private static async Task<Result> ReadAsync(Bitmap bmp, int offsetX, int offsetY, double scale)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        if (engine == null)
            throw new InvalidOperationException(
                "Mesin OCR tidak tersedia di PC ini (komponen bahasa OCR Windows belum terpasang).");

        byte[] png;
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            png = ms.ToArray();
        }

        using (var stream = new InMemoryRandomAccessStream())
        {
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var sb = await decoder.GetSoftwareBitmapAsync();
            if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                sb = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var res = await engine.RecognizeAsync(sb);
            var hasil = new Result { Text = res.Text ?? "" };
            foreach (var line in res.Lines)
            {
                foreach (var w in line.Words)
                {
                    if (hasil.Words.Count >= 600) break;
                    var r = w.BoundingRect;
                    hasil.Words.Add(new Word
                    {
                        Text = w.Text ?? "",
                        Rect = new Rectangle(
                            offsetX + (int)(r.X / scale), offsetY + (int)(r.Y / scale),
                            Math.Max(1, (int)(r.Width / scale)), Math.Max(1, (int)(r.Height / scale))),
                    });
                }
                if (hasil.Words.Count >= 600) break;
            }
            return hasil;
        }
    }
}
