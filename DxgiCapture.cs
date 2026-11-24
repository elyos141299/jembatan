using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Jembatan;

/// <summary>
/// Tangkap layar lewat DXGI Desktop Duplication (v2.7.0) — bingkai
/// diambil langsung dari pipa GPU, jalur yang sama yang dipakai
/// Windows untuk menggambar layar. Keunggulannya atas GDI
/// (BitBlt/StretchBlt, jalur lama sejak v1):
///
/// - Permukaan D3D ikut tertangkap BENAR: pratinjau OBS, game,
///   dan video tidak lagi tampak putih/hitam polos seperti di
///   tangkapan GDI — selama ini kekurangan itu diakali dengan
///   verifikasi lewat frame rekaman.
/// - Jauh lebih cepat & ringan: tidak ada fotokopi piksel per
///   piksel oleh CPU.
///
/// Sifat jalur ini: duplikasi hanya menyerahkan bingkai saat
/// layar BERUBAH. Bingkai terakhir selalu disimpan (staging)
/// dan disajikan ulang bila tidak ada yang baru — untuk
/// keperluan screenshot/frame itu memang yang diminta.
///
/// Kegagalan apa pun (akses hilang karena ganti resolusi /
/// layar kunci / desktop aman UAC, perangkat dicabut, dsb.)
/// TIDAK menjatuhkan pemanggil: mesin dibongkar, jeda dingin
/// 30 detik dipasang, dan TryCapture mengembalikan null —
/// pemanggil jatuh ke jalur GDI lama. Sesudah jeda, inisialisasi
/// dicoba lagi dari nol, jadi DXGI bisa pulih sendiri.
/// </summary>
public static class DxgiCapture
{
    private static readonly object _lock = new object();

    private static ID3D11Device _device;
    private static ID3D11DeviceContext _context;
    private static IDXGIDevice _dxgiDevice;
    private static IDXGIAdapter _adapter;
    private static IDXGIOutput _output;
    private static IDXGIOutput1 _output1;
    private static IDXGIOutputDuplication _dup;
    private static ID3D11Texture2D _staging;
    private static int _width;
    private static int _height;
    private static bool _haveFrame;
    private static long _retryAfterTick;
    private static byte[] _rowBuf;
    private static bool _scaledOk;
    // Jalur GPU murni (v2.7.4): VideoProcessor mengecilkan
    // bingkai di GPU sebelum piksel menyentuh CPU.
    private static ID3D11VideoDevice _vpDevice;
    private static ID3D11VideoContext _vpContext;
    private static ID3D11VideoProcessorEnumerator _vpEnum;
    private static ID3D11VideoProcessor _vpProc;
    private static ID3D11Texture2D _vpOutTex;
    private static ID3D11VideoProcessorOutputView _vpOutView;
    private static ID3D11Texture2D _vpStaging;
    private static int _vpInW, _vpInH, _vpOutW, _vpOutH;
    private static bool _gpuOk;
    private static long _vpRetryAfterTick;

    // HRESULT DXGI yang perlu dibedakan dari kegagalan umum.
    private const int WaitTimeout = unchecked((int)0x887A0027);

    /// <summary>"dxgi" bila mesin duplikasi sedang hidup, "gdi" bila tidak.</summary>
    public static string Mode
    {
        get { lock (_lock) { return _dup != null ? "dxgi" : "gdi"; } }
    }

    /// <summary>
    /// Jalur skala yang terakhir terbukti hidup: "gpu"
    /// (VideoProcessor v2.7.4), "integrated" (skala saat salin
    /// staging, v2.7.3), atau "legacy" (bitmap resolusi penuh +
    /// gambar-ulang GDI+ seperti v2.7.0–v2.7.2).
    /// </summary>
    public static string ScaleMode
    {
        get { lock (_lock) { return _gpuOk ? "gpu" : _scaledOk ? "integrated" : "legacy"; } }
    }

    /// <summary>
    /// Ambil bingkai layar penuh terbaru (resolusi asli).
    /// null = DXGI tidak tersedia saat ini → pemanggil memakai GDI.
    /// Pemanggil membuang bitmap hasil.
    /// </summary>
    public static Bitmap TryCapture()
    {
        lock (_lock)
        {
            try
            {
                if (!EnsureFrameLocked()) return null;
                return ReadStaging();
            }
            catch
            {
                DisposeLocked();
                _retryAfterTick = Environment.TickCount64 + 30000;
                return null;
            }
        }
    }

    /// <summary>
    /// Seperti TryCapture, tapi bitmap terbit LANGSUNG pada lebar
    /// tujuan (v2.7.3): pengecilan dikerjakan saat piksel disalin
    /// keluar dari tekstur staging (rata-rata kotak, paralel per
    /// baris) — melewati bitmap resolusi penuh + gambar-ulang
    /// GDI+ yang memakan beberapa ms per bingkai di jalur /frame
    /// & /stream. targetWidth >= lebar layar → sama dengan
    /// TryCapture. Kegagalan apa pun → null (pemanggil jatuh ke
    /// jalur lama), persis kontrak TryCapture.
    /// </summary>
    public static Bitmap TryCaptureScaled(int targetWidth)
    {
        lock (_lock)
        {
            try
            {
                // v2.7.4: jalur GPU (VideoProcessor) lebih dulu —
                // kegagalannya tertangani di dalam (jatuh ke bawah).
                var gpu = TryGpuScaledLocked(targetWidth);
                if (gpu != null) return gpu;
                if (!EnsureFrameLocked()) return null;
                if (targetWidth <= 0 || targetWidth >= _width) return ReadStaging();
                int th = Math.Max(1, (int)(_height * (targetWidth / (double)_width)));
                var bmp = ReadStagingScaled(targetWidth, th);
                if (bmp != null) _scaledOk = true;
                return bmp;
            }
            catch
            {
                DisposeLocked();
                _retryAfterTick = Environment.TickCount64 + 30000;
                return null;
            }
        }
    }

    // Inisialisasi (bila perlu) + kuras antrean bingkai; true =
    // ada bingkai di staging siap dibaca. Pemanggil memegang _lock.
    private static bool EnsureFrameLocked()
    {
        if (_dup == null)
        {
            if (Environment.TickCount64 < _retryAfterTick) return false;
            if (!Init())
            {
                _retryAfterTick = Environment.TickCount64 + 30000;
                return false;
            }
        }
        // Kuras bingkai yang mengantre; simpan yang terbaru.
        while (DrainOnce(0)) { }
        if (!_haveFrame)
        {
            // Bingkai pertama sesudah init kadang butuh
            // waktu — beri satu kesempatan menunggu.
            DrainOnce(300);
        }
        return _haveFrame;
    }

    // Salin staging ke bitmap yang SUDAH dikecilkan: tiap piksel
    // tujuan = rata-rata kotak wilayah sumbernya.
    private static Bitmap ReadStagingScaled(int tw, int th)
    {
        var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bmp = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(
                new Rectangle(0, 0, tw, th),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int sw = _width, sh = _height;
                int pitch = (int)map.RowPitch;
                IntPtr basePtr = map.DataPointer;
                IntPtr dstBase = bd.Scan0;
                int dstStride = bd.Stride;
                Parallel.For(0, th, oy =>
                {
                    int sy0 = oy * sh / th;
                    int sy1 = Math.Max(sy0 + 1, (oy + 1) * sh / th);
                    var accB = new int[tw];
                    var accG = new int[tw];
                    var accR = new int[tw];
                    var rowBuf = new byte[sw * 4];
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        Marshal.Copy(basePtr + sy * pitch, rowBuf, 0, sw * 4);
                        for (int ox = 0; ox < tw; ox++)
                        {
                            int sx0 = ox * sw / tw;
                            int sx1 = Math.Max(sx0 + 1, (ox + 1) * sw / tw);
                            for (int sx = sx0; sx < sx1; sx++)
                            {
                                int o = sx * 4;
                                accB[ox] += rowBuf[o];
                                accG[ox] += rowBuf[o + 1];
                                accR[ox] += rowBuf[o + 2];
                            }
                        }
                    }
                    var outRow = new byte[tw * 4];
                    int rows = sy1 - sy0;
                    for (int ox = 0; ox < tw; ox++)
                    {
                        int sx0 = ox * sw / tw;
                        int sx1 = Math.Max(sx0 + 1, (ox + 1) * sw / tw);
                        int cnt = rows * (sx1 - sx0);
                        int o = ox * 4;
                        outRow[o] = (byte)(accB[ox] / cnt);
                        outRow[o + 1] = (byte)(accG[ox] / cnt);
                        outRow[o + 2] = (byte)(accR[ox] / cnt);
                        outRow[o + 3] = 255;
                    }
                    Marshal.Copy(outRow, 0, dstBase + oy * dstStride, tw * 4);
                });
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
        finally { _context.Unmap(_staging, 0); }
    }

    private static bool Init()
    {
        try
        {
            var hr = D3D11.D3D11CreateDevice(
                (IDXGIAdapter)null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0 },
                out _device, out _context);
            if (hr.Failure || _device == null) { DisposeLocked(); return false; }

            _dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            if (_dxgiDevice.GetAdapter(out _adapter).Failure) { DisposeLocked(); return false; }
            if (_adapter.EnumOutputs(0, out _output).Failure) { DisposeLocked(); return false; }
            _output1 = _output.QueryInterface<IDXGIOutput1>();
            _dup = _output1.DuplicateOutput(_device);
            if (_dup == null) { DisposeLocked(); return false; }
            _haveFrame = false;
            return true;
        }
        catch
        {
            DisposeLocked();
            return false;
        }
    }

    /// <summary>
    /// Ambil satu bingkai dari antrean duplikasi ke staging.
    /// true = ada bingkai baru tersalin. Timeout (layar diam)
    /// bukan galat — false biasa. Galat sungguhan dilempar.
    /// </summary>
    private static bool DrainOnce(int timeoutMs)
    {
        var hr = _dup.AcquireNextFrame((uint)timeoutMs, out _, out var resource);
        if (hr.Code == WaitTimeout) return false;
        if (hr.Failure || resource == null)
            throw new InvalidOperationException("AcquireNextFrame gagal: 0x" + hr.Code.ToString("X8"));
        try
        {
            using (var tex = resource.QueryInterface<ID3D11Texture2D>())
            {
                var desc = tex.Description;
                if (_staging == null || desc.Width != (uint)_width || desc.Height != (uint)_height)
                {
                    _staging?.Dispose();
                    _width = (int)desc.Width;
                    _height = (int)desc.Height;
                    _staging = _device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = desc.Width,
                        Height = desc.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None,
                    });
                    _rowBuf = new byte[_width * 4];
                }
                _context.CopyResource(_staging, tex);
            }
        }
        finally
        {
            resource.Dispose();
            _dup.ReleaseFrame();
        }
        _haveFrame = true;
        return true;
    }

    private static Bitmap ReadStaging()
    {
        var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bmp = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(
                new Rectangle(0, 0, _width, _height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = _width * 4;
                for (int y = 0; y < _height; y++)
                {
                    IntPtr src = map.DataPointer + y * (int)map.RowPitch;
                    IntPtr dst = bd.Scan0 + y * bd.Stride;
                    Marshal.Copy(src, _rowBuf, 0, rowBytes);
                    Marshal.Copy(_rowBuf, 0, dst, rowBytes);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
        finally { _context.Unmap(_staging, 0); }
    }

    // Bangun (atau bangun ulang bila ukuran berubah) pipa
    // VideoProcessor untuk pasangan ukuran masuk/keluar ini.
    // False = tidak bisa sekarang (jeda dingin 30 detik berlaku).
    private static bool EnsureVpLocked(int inW, int inH, int outW, int outH)
    {
        if (_vpProc != null && _vpInW == inW && _vpInH == inH && _vpOutW == outW && _vpOutH == outH)
            return true;
        DisposeVpLocked();
        if (Environment.TickCount64 < _vpRetryAfterTick) return false;
        try
        {
            if (_vpDevice == null) _vpDevice = _device.QueryInterface<ID3D11VideoDevice>();
            if (_vpContext == null) _vpContext = _context.QueryInterface<ID3D11VideoContext>();
            var desc = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational { Numerator = 60, Denominator = 1 },
                InputWidth = (uint)inW,
                InputHeight = (uint)inH,
                OutputFrameRate = new Rational { Numerator = 60, Denominator = 1 },
                OutputWidth = (uint)outW,
                OutputHeight = (uint)outH,
                Usage = VideoUsage.PlaybackNormal,
            };
            if (_vpDevice.CreateVideoProcessorEnumerator(ref desc, out _vpEnum).Failure)
                throw new InvalidOperationException("CreateVideoProcessorEnumerator gagal");
            if (_vpDevice.CreateVideoProcessor(_vpEnum, 0, out _vpProc).Failure)
                throw new InvalidOperationException("CreateVideoProcessor gagal");
            _vpOutTex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outW,
                Height = (uint)outH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
            var ovd = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            };
            if (_vpDevice.CreateVideoProcessorOutputView(_vpOutTex, _vpEnum, ovd, out _vpOutView).Failure)
                throw new InvalidOperationException("CreateVideoProcessorOutputView gagal");
            _vpStaging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outW,
                Height = (uint)outH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
            _vpInW = inW; _vpInH = inH; _vpOutW = outW; _vpOutH = outH;
            return true;
        }
        catch
        {
            DisposeVpLocked();
            _vpRetryAfterTick = Environment.TickCount64 + 30000;
            return false;
        }
    }

    // Coba hasilkan bitmap berskala lewat GPU. Null = tidak ada
    // bingkai baru / tidak perlu diskala / VP tidak tersedia —
    // pemanggil melanjutkan ke jalur staging seperti biasa.
    // Semua kegagalan VP tertangani di sini (bongkar VP + jeda
    // dingin), tidak pernah melempar ke pemanggil.
    private static Bitmap TryGpuScaledLocked(int targetWidth)
    {
        if (_dup == null) return null;
        if (Environment.TickCount64 < _vpRetryAfterTick) return null;
        ID3D11Texture2D tex = null;
        bool acquired = false;
        try
        {
            var hr = _dup.AcquireNextFrame(0, out _, out var res);
            if (hr.Code == WaitTimeout) { res?.Dispose(); return null; }
            if (hr.Failure || res == null) { res?.Dispose(); return null; }
            acquired = true;
            tex = res.QueryInterface<ID3D11Texture2D>();
            res.Dispose();
            var td = tex.Description;
            int inW = (int)td.Width, inH = (int)td.Height;
            if (targetWidth <= 0 || targetWidth >= inW) return null;
            int outH = Math.Max(1, (int)(inH * (targetWidth / (double)inW)));
            if (!EnsureVpLocked(inW, inH, targetWidth, outH)) return null;
            ID3D11VideoProcessorInputView inView = null;
            try
            {
                var ivd = new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                };
                if (_vpDevice.CreateVideoProcessorInputView(tex, _vpEnum, ivd, out inView).Failure)
                    return null;
                var stream = new VideoProcessorStream { Enable = true, InputSurface = inView };
                // VideoProcessorBlt di Vortice 3.6.x bertipe void —
                // kegagalan muncul sebagai pengecualian SharpGen
                // dan tertangkap penangan VP di luar.
                _vpContext.VideoProcessorBlt(_vpProc, _vpOutView, 0, 1, new[] { stream });
            }
            finally { inView?.Dispose(); }
            _context.CopyResource(_vpStaging, _vpOutTex);
            var bmp = ReadStagingFrom(_vpStaging, targetWidth, outH);
            if (bmp != null) _gpuOk = true;
            return bmp;
        }
        catch
        {
            DisposeVpLocked();
            _vpRetryAfterTick = Environment.TickCount64 + 30000;
            return null;
        }
        finally
        {
            tex?.Dispose();
            if (acquired) { try { _dup.ReleaseFrame(); } catch { } }
        }
    }

    // Baca tekstur staging mana pun ke bitmap (pasangan umum
    // dari ReadStaging yang khusus staging duplikasi).
    private static Bitmap ReadStagingFrom(ID3D11Texture2D tex, int w, int h)
    {
        var map = _context.Map(tex, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = w * 4;
                var buf = new byte[rowBytes];
                for (int y = 0; y < h; y++)
                {
                    IntPtr src = map.DataPointer + y * (int)map.RowPitch;
                    IntPtr dst = bd.Scan0 + y * bd.Stride;
                    Marshal.Copy(src, buf, 0, rowBytes);
                    Marshal.Copy(buf, 0, dst, rowBytes);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
        finally { _context.Unmap(tex, 0); }
    }

    private static void DisposeVpLocked()
    {
        try { _vpOutView?.Dispose(); } catch { }
        try { _vpProc?.Dispose(); } catch { }
        try { _vpEnum?.Dispose(); } catch { }
        try { _vpStaging?.Dispose(); } catch { }
        try { _vpOutTex?.Dispose(); } catch { }
        try { _vpContext?.Dispose(); } catch { }
        try { _vpDevice?.Dispose(); } catch { }
        _vpOutView = null; _vpProc = null; _vpEnum = null;
        _vpStaging = null; _vpOutTex = null; _vpContext = null; _vpDevice = null;
        _vpInW = 0; _vpInH = 0; _vpOutW = 0; _vpOutH = 0;
        _gpuOk = false;
    }

    private static void DisposeLocked()
    {
        DisposeVpLocked();
        try { _staging?.Dispose(); } catch { }
        try { _dup?.Dispose(); } catch { }
        try { _output1?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _adapter?.Dispose(); } catch { }
        try { _dxgiDevice?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _staging = null; _dup = null; _output1 = null; _output = null;
        _adapter = null; _dxgiDevice = null; _context = null; _device = null;
        _width = 0; _height = 0; _haveFrame = false; _rowBuf = null; _scaledOk = false;
    }
}
