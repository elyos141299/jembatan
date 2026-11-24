using System;
using System.Collections.Generic;
using System.IO;
using System.Speech.Synthesis;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Jembatan;

/// <summary>
/// Indra dengar &amp; suara MuseLink (v2.5.0):
///
/// - <see cref="CaptureLoopbackWav"/> (/listen): merekam audio
///   yang SEDANG dimainkan PC (loopback WASAPI lewat NAudio)
///   selama beberapa detik, lalu mengecilkan hasilnya jadi WAV
///   mono 16kHz 16-bit — cukup jernih untuk ucapan &amp; bunyi
///   isyarat, dan cukup ringan (±32KB/detik) untuk menyeberangi
///   relay. Bila tidak ada bunyi sama sekali, sebagian driver
///   tidak mengirim data; sisanya diisi keheningan agar durasi
///   rekaman tetap sesuai permintaan (hening pun jawaban sah:
///   memang tidak ada yang terdengar).
///
/// - <see cref="Speak"/> (/say): bicara lewat speaker PC memakai
///   mesin TTS bawaan Windows (System.Speech / SAPI). Suara
///   mengikuti suara sistem terpasang (umumnya Inggris).
/// </summary>
public static class AudioTools
{
    // ---------- /listen ----------

    /// <summary>Rekam loopback selama seconds detik → byte WAV mono 16kHz.</summary>
    public static byte[] CaptureLoopbackWav(int seconds)
    {
        if (seconds < 1) seconds = 1;
        if (seconds > 30) seconds = 30;

        var mentah = new List<byte>();
        var kunci = new object();
        WaveFormat fmt;
        using (var cap = new WasapiLoopbackCapture())
        {
            fmt = cap.WaveFormat;
            cap.DataAvailable += (s, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                var potong = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, potong, e.BytesRecorded);
                lock (kunci) { mentah.AddRange(potong); }
            };
            cap.StartRecording();
            Thread.Sleep(seconds * 1000);
            cap.StopRecording();
        }

        byte[] data;
        lock (kunci) { data = mentah.ToArray(); }
        return ConvertToWav16kMono(data, fmt, seconds);
    }

    /// <summary>
    /// Rekam MIKROFON (perangkat tangkap bawaan Windows) selama
    /// seconds detik → byte WAV mono 16kHz (v2.7.0). Pasangan
    /// sumber "mic" di /listen: mendengar ruangan lewat mic —
    /// beda dari loopback yang mendengar suara yang DIMAINKAN PC.
    /// </summary>
    public static byte[] CaptureMicWav(int seconds)
    {
        if (seconds < 1) seconds = 1;
        if (seconds > 30) seconds = 30;

        var mentah = new List<byte>();
        var kunci = new object();
        WaveFormat fmt;
        using (var cap = new WasapiCapture())
        {
            fmt = cap.WaveFormat;
            cap.DataAvailable += (s, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                var potong = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, potong, e.BytesRecorded);
                lock (kunci) { mentah.AddRange(potong); }
            };
            cap.StartRecording();
            Thread.Sleep(seconds * 1000);
            cap.StopRecording();
        }
        byte[] data;
        lock (kunci) { data = mentah.ToArray(); }
        return ConvertToWav16kMono(data, fmt, seconds);
    }

    /// <summary>
    /// Ubah tangkapan WASAPI mentah (umumnya float 32-bit,
    /// 44,1/48kHz, stereo) menjadi WAV mono 16kHz 16-bit:
    /// tiap jendela cuplikan dirata-rata (downsample kasar,
    /// cukup untuk ucapan & bunyi isyarat). Kekurangan data
    /// diisi keheningan agar durasi sesuai permintaan.
    /// </summary>
    private static byte[] ConvertToWav16kMono(byte[] data, WaveFormat fmt, int seconds)
    {
        // Format sumber umumnya float 32-bit, 44,1/48kHz, stereo.
        int channels = Math.Max(1, fmt.Channels);
        int rate = fmt.SampleRate;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
        int bytesPerSample = fmt.BitsPerSample / 8;
        if (bytesPerSample <= 0) bytesPerSample = isFloat ? 4 : 2;
        int frameBytes = bytesPerSample * channels;
        int frames = frameBytes > 0 ? data.Length / frameBytes : 0;

        const int outRate = 16000;
        int outFrames = seconds * outRate;
        var pcm = new short[outFrames];
        double step = rate / (double)outRate;
        for (int i = 0; i < outFrames; i++)
        {
            int start = (int)(i * step);
            int end = Math.Min(frames, (int)((i + 1) * step));
            if (start >= frames) break; // sisa = hening (0)
            double acc = 0;
            int n = 0;
            for (int f = start; f < end; f++)
            {
                double mono = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    int off = (f * channels + ch) * bytesPerSample;
                    if (off + bytesPerSample > data.Length) break;
                    double v;
                    if (isFloat) v = BitConverter.ToSingle(data, off);
                    else if (bytesPerSample == 2) v = BitConverter.ToInt16(data, off) / 32768.0;
                    else if (bytesPerSample == 4) v = BitConverter.ToInt32(data, off) / 2147483648.0;
                    else v = 0;
                    mono += v;
                }
                acc += mono / channels;
                n++;
            }
            double avg = n > 0 ? acc / n : 0;
            if (avg > 1) avg = 1;
            if (avg < -1) avg = -1;
            pcm[i] = (short)(avg * 32767);
        }

        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            int byteRate = outRate * 2;
            int dataLen = pcm.Length * 2;
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataLen);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);          // PCM
            bw.Write((short)1);          // mono
            bw.Write(outRate);
            bw.Write(byteRate);
            bw.Write((short)2);          // block align
            bw.Write((short)16);         // bits
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(dataLen);
            foreach (var s in pcm) bw.Write(s);
            bw.Flush();
            return ms.ToArray();
        }
    }

    // ---------- /say ----------

    /// <summary>
    /// Ucapkan teks lewat speaker. Berjalan di thread STA sendiri
    /// (kebutuhan SAPI); menunggu sampai selesai (maks ±60 detik).
    /// Mengembalikan galat sebagai teks ("" = berhasil).
    /// </summary>
    public static string Speak(string text, int rate)
    {
        if (rate < -3) rate = -3;
        if (rate > 3) rate = 3;
        string error = "";
        var t = new Thread(() =>
        {
            try
            {
                using (var synth = new SpeechSynthesizer())
                {
                    synth.Rate = rate;
                    synth.SetOutputToDefaultAudioDevice();
                    synth.Speak(text);
                }
            }
            catch (Exception ex) { error = ex.Message; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(60)))
            return "ucapan melebihi 60 detik dan ditinggal berjalan di latar";
        return error;
    }
}
