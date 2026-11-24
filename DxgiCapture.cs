using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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

    // HRESULT DXGI yang perlu dibedakan dari kegagalan umum.
    private const int WaitTimeout = unchecked((int)0x887A0027);

    /// <summary>"dxgi" bila mesin duplikasi sedang hidup, "gdi" bila tidak.</summary>
    public static string Mode
    {
        get { lock (_lock) { return _dup != null ? "dxgi" : "gdi"; } }
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
            if (_dup == null)
            {
                if (Environment.TickCount64 < _retryAfterTick) return null;
                if (!Init())
                {
                    _retryAfterTick = Environment.TickCount64 + 30000;
                    return null;
                }
            }
            try
            {
                // Kuras bingkai yang mengantre; simpan yang terbaru.
                while (DrainOnce(0)) { }
                if (!_haveFrame)
                {
                    // Bingkai pertama sesudah init kadang butuh
                    // waktu — beri satu kesempatan menunggu.
                    DrainOnce(300);
                }
                if (!_haveFrame) return null;
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

    private static bool Init()
    {
        try
        {
            var hr = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                null,
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
        var hr = _dup.AcquireNextFrame(timeoutMs, out _, out var resource);
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
        var map = _context.Map(_staging, 0, MapMode.Read, MapFlags.None);
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

    private static void DisposeLocked()
    {
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
        _width = 0; _height = 0; _haveFrame = false; _rowBuf = null;
    }
}
