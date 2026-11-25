using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Jembatan;

/// <summary>
/// Jendela chat MELAYANG (v3.4.0 — blueprint user: "chat buat
/// seperti log aktivitas"). Mekanisme meniru persis LogFlyoutForm:
/// jendela tanpa bingkai yang muncul menempel di kanan tombol
/// chat rel, melayang di atas panel kendali, masuk/keluar dengan
/// animasi geser + pudar, tutup lewat tombol yang sama atau Esc.
/// Bedanya: latar KANVAS terang (isi chat = kartu jendela seperti
/// dulu), lebar tetap 460px, dan tinggi mengikuti tinggi area
/// konten jendela utama (diatur pemanggil lewat ShowAnchored).
/// Isi (strip kepala + kartu WebView2) dibangun MainForm lalu
/// dipindah ke panel Host di sini. WebView2 tetap hidup saat
/// flyout tertutup — sesi chat tidak terputus.
/// </summary>
public sealed class ChatFlyoutForm : Form
{
    private readonly System.Windows.Forms.Timer _anim = new System.Windows.Forms.Timer { Interval = 15 };
    private const int AnimSteps = 9;
    private int _animStep;
    private bool _showing;
    private Point _targetPos;

    /// <summary>Lebar flyout chat (permintaan blueprint ±460px).</summary>
    public const int FlyoutWidth = 460;

    /// <summary>Wadah isi chat (mengisi seluruh flyout) — diisi MainForm.</summary>
    public Panel Host { get; }

    public ChatFlyoutForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(FlyoutWidth, 600);
        BackColor = AppTheme.Bg;
        Opacity = 0;
        KeyPreview = true;

        Host = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Bg };
        Controls.Add(Host);

        _anim.Tick += AnimTick;
    }

    // Bayangan jatuh untuk jendela tanpa bingkai (kelas jendela
    // CS_DROPSHADOW) — melayang harus terasa melayang.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000;
            return cp;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width >= 10 && Height >= 10)
        {
            // Region.FromHrgn mengambil alih kepemilikan gagang.
            Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 16, 16));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Garis tepi 1px mengikuti sudut membulat (radius sama
        // dengan region jendela) — tepi laci terasa tegas.
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            const int r = 8;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
            path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
            path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using (var pen = new Pen(AppTheme.RailHover))
                e.Graphics.DrawPath(pen, path);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) HideAnimated();
    }

    /// <summary>
    /// Tampilkan menempel di kanan tombol rel (anchor = persegi
    /// tombol dalam koordinat layar), bergeser masuk dari kiri
    /// sambil memudar masuk. Tinggi = tinggi area konten jendela
    /// utama dan tepi atas diselaraskan ke topY (koordinat layar
    /// tepi atas area konten) — keduanya dijepit ke area kerja
    /// layar agar tidak pernah keluar layar.
    /// </summary>
    public void ShowAnchored(Rectangle anchorScreen, Form owner, int height, int topY)
    {
        var wa = Screen.FromRectangle(anchorScreen).WorkingArea;
        int w = FlyoutWidth;
        int h = Math.Min(height, wa.Height - 16);
        if (h < 200) h = Math.Min(560, wa.Height - 16);
        int x = anchorScreen.Right + 10;
        int y = topY;
        if (x + w > wa.Right - 8) x = wa.Right - w - 8;
        if (y + h > wa.Bottom - 8) y = wa.Bottom - h - 8;
        if (y < wa.Top + 4) y = wa.Top + 4;
        Size = new Size(w, h);
        _targetPos = new Point(x, y);
        Location = new Point(x - 18, y);
        Opacity = 0;
        _showing = true;
        _animStep = 0;
        if (!Visible) Show(owner);
        BringToFront();
        Activate();
        _anim.Start();
    }

    /// <summary>Geser + pudar keluar, lalu sembunyi.</summary>
    public void HideAnimated()
    {
        if (!Visible) return;
        _targetPos = Location;
        _showing = false;
        _animStep = 0;
        _anim.Start();
    }

    /// <summary>Sembunyi seketika (mis. jendela utama terminimalkan).</summary>
    public void HideImmediate()
    {
        _anim.Stop();
        if (Visible) Hide();
        Opacity = 0;
    }

    private void AnimTick(object sender, EventArgs e)
    {
        _animStep++;
        double t = Math.Min(1.0, _animStep / (double)AnimSteps);
        double ease = 1 - Math.Pow(1 - t, 3);
        if (_showing)
        {
            Location = new Point(_targetPos.X - (int)(18 * (1 - ease)), _targetPos.Y);
            Opacity = ease;
            if (t >= 1)
            {
                _anim.Stop();
                Location = _targetPos;
                Opacity = 1;
            }
        }
        else
        {
            Opacity = 1 - ease;
            Location = new Point(_targetPos.X - (int)(14 * ease), _targetPos.Y);
            if (t >= 1)
            {
                _anim.Stop();
                Hide();
                Opacity = 0;
            }
        }
    }
}
