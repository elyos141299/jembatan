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
        return Task.Run(() => ReadAsync(bmp, offsetX, offsetY)).GetAwaiter().GetResult();
    }

    private static async Task<Result> ReadAsync(Bitmap bmp, int offsetX, int offsetY)
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
                            offsetX + (int)r.X, offsetY + (int)r.Y,
                            Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height)),
                    });
                }
                if (hasil.Words.Count >= 600) break;
            }
            return hasil;
        }
    }
}
