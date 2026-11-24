using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Jembatan;

/// <summary>
/// Tombol roda gigi MELAYANG di bar judul jendela (v2.5.1,
/// permintaan user: "di sebelah kiri minimize kanan atas").
///
/// Bar judul MuseLink adalah bar bawaan Windows — tidak ada
/// tempat resmi untuk menyisipkan tombol di dalamnya. Jalan yang
/// dipakai di sini: satu jendela kecil tanpa bingkai MILIK
/// jendela utama (owned window) yang menempel persis di sebelah
/// kiri tombol minimize dan mengikuti setiap gerak / ubah ukuran
/// / sembunyi jendela utama. Seluruh chrome bawaan (seret,
/// snap, minimize/maximize/close) tetap 100% asli Windows —
/// tidak ada bar judul custom yang berisiko.
///
/// Ukuran mengikuti tombol caption Windows 11 pada skala 100%:
/// 46px per tombol; minimize adalah tombol caption paling kiri,
/// jadi roda gigi menempati kotak [Right-184, Right-138].
/// Gaya tombol mengikuti bar judul (terang), bukan tema aplikasi.
/// </summary>
public sealed class CaptionGearForm : Form
{
    private readonly Button _btn;

    public CaptionGearForm(MainForm owner)
    {
        // Ukuran harus persis seperti yang ditulis (penskalakan
        // font dimatikan; tapi lihat WndProc di bawah — pengekang
        // ukuran yang sebenarnya adalah ukuran jendela minimum
        // bawaan Windows, 136x39).
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(46, 32);
        BackColor = Color.FromArgb(255, 255, 255);

        _btn = new Button
        {
            Dock = DockStyle.Fill,
            Text = "\u2699",
            Font = new Font("Segoe UI Symbol", 12f),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(255, 255, 255),
            ForeColor = Color.FromArgb(29, 29, 31),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _btn.FlatAppearance.BorderSize = 0;
        _btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 232, 237);
        _btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(210, 210, 215);
        _btn.Click += (s, e) => owner.OpenSettings();
        Controls.Add(_btn);
    }

    // Jangan rebut fokus dari jendela utama saat tampil / diklik.
    protected override bool ShowWithoutActivation => true;

    // FIX SEBENARNYA (v2.5.4): Windows mengejepit SEMUA jendela
    // ke ukuran minimum bawaan 136x39 px (SM_CXMIN x SM_CYMIN) —
    // jendela 46x32 ini dipaksa jadi 136x39 dan menutupi tombol
    // minimize/maximize (terukur persis 136x39 di v2.5.2/v2.5.3;
    // dugaan awal "skala font" terbukti meleset). Satu-satunya
    // jalan: jawab WM_GETMINMAXINFO dengan ukuran minimum kita
    // sendiri.
    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            mmi.ptMinTrackSize.x = 46;
            mmi.ptMinTrackSize.y = 32;
            Marshal.StructureToPtr(mmi, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Tempelkan ke pojok kanan atas jendela utama.</summary>
    public void SyncTo(Rectangle ownerBounds, bool ownerVisible)
    {
        if (!ownerVisible)
        {
            if (Visible) Hide();
            return;
        }
        Location = new Point(ownerBounds.Right - 184, ownerBounds.Top);
        if (!Visible) Show();
        BringToFront();
    }
}
