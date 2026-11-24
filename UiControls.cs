using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jembatan;

// Kontrol tampilan gaya iCloud (v2.3.3): panel kartu bersudut bundar,
// saklar ala iOS, tombol pil bersudut bundar, dan titik status bulat.
// Murni tampilan — tidak ada logika aplikasi di sini.

/// <summary>
/// Panel kartu: isi warna rata + garis tepi 1px, sudut dibulatkan
/// (bawaan 16px). Sejak v2.3.5 sudutnya TIDAK lagi dipotong Region —
/// Region Windows tidak mengenal anti-alias dan tepinya bergerigi
/// tangga piksel. Sebagai gantinya area di luar lengkung dicat warna
/// latar (BackColor panel = warna latar form) lalu bentuk bundarnya
/// digambar dengan anti-alias penuh, jadi tepinya mulus.
/// </summary>
public class RoundedPanel : Panel
{
    public int CornerRadius { get; set; } = 16;
    public Color FillColor { get; set; } = Color.White;
    public Color BorderColor { get; set; } = Color.FromArgb(227, 227, 232);

    public RoundedPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        // Warna latar disamakan dengan latar form (#F5F5F7) — bagian di
        // luar sudut bundar sebenarnya terpotong Region, ini pengaman.
        BackColor = Color.FromArgb(245, 245, 247);
    }

    /// <summary>v2.9.1: warna permukaan EFEKTIF di belakang sebuah
    /// kontrol — induk RoundedPanel berarti FillColor-nya, selain itu
    /// BackColor induk. Dipakai mewarnai sudut di luar lengkung.
    /// Sebelumnya RoundedPanel memakai BackColor-nya sendiri yang
    /// bawaan #F5F5F7: tak terlihat di tema terang, tetapi di tema
    /// gelap tampil sebagai segitiga putih di keempat sudut kartu
    /// (dilaporkan user 25 Nov 04:21).</summary>
    internal static Color SurfaceOf(Control parent)
    {
        if (parent == null) return Color.FromArgb(245, 245, 247);
        if (parent is RoundedPanel rp) return rp.FillColor;
        return parent.BackColor;
    }

    internal static GraphicsPath RoundRectPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Sudut di luar lengkung: cat warna permukaan induk dulu,
        // supaya lengkung anti-alias menyatu mulus dengan latar —
        // pengganti Region yang bergerigi (v2.3.5). v2.9.1: warna
        // permukaan EFEKTIF induk (SurfaceOf), bukan BackColor
        // sendiri — sumber segitiga putih di tema gelap.
        using (var bg = new SolidBrush(SurfaceOf(Parent)))
            g.FillRectangle(bg, ClientRectangle);
        using (var path = RoundRectPath(ClientRectangle, CornerRadius))
        using (var fill = new SolidBrush(FillColor))
            g.FillPath(fill, path);
        var inner = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        using (var path = RoundRectPath(inner, CornerRadius))
        using (var pen = new Pen(BorderColor, 1f))
            g.DrawPath(pen, path);
    }
}

/// <summary>
/// Saklar ala iOS pengganti CheckBox: lintasan pil 44×26 (hijau saat
/// menyala, abu muda saat mati) dengan kenop putih 22px. Klik (atau
/// spasi saat fokus) membalik keadaan; semantik Checked +
/// CheckedChanged sama seperti CheckBox.
/// </summary>
public class ToggleSwitch : Control
{
    private bool _checked;

    public event EventHandler CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        Size = new Size(40, 20);
        MinimumSize = new Size(40, 20);
        MaximumSize = new Size(40, 20);
        TabStop = true;
        Cursor = Cursors.Hand;
        BackColor = Color.White;
    }

    public bool Checked
    {
        get { return _checked; }
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var bg = new SolidBrush(RoundedPanel.SurfaceOf(Parent)))
            g.FillRectangle(bg, ClientRectangle);

        var trackRect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color track = _checked
            ? AppTheme.Accent
            : (AppTheme.IsDark ? Color.FromArgb(58, 61, 92) : Color.FromArgb(232, 232, 237));
        using (var path = RoundedPanel.RoundRectPath(trackRect, Height / 2))
        {
            using (var brush = new SolidBrush(track))
                g.FillPath(brush, path);
            // Garis tepi sangat tipis semi-transparan: memberi batas
            // tegas tanpa kesan keras (v2.3.5).
            using (var pen = new Pen(Color.FromArgb(26, 0, 0, 0), 1f))
                g.DrawPath(pen, path);
        }

        int knob = Math.Max(10, Height - 4);
        int kx = _checked ? Width - knob - 2 : 2;
        // Bayangan lembut kenop (elips semi-transparan bergeser 1px
        // ke bawah) — kesan kedalaman ala iOS (v2.3.5).
        using (var shadow = new SolidBrush(Color.FromArgb(34, 0, 0, 0)))
            g.FillEllipse(shadow, kx, 3, knob, knob);
        var knobRect = new Rectangle(kx, 2, knob, knob);
        using (var path = RoundedPanel.RoundRectPath(knobRect, knob / 2))
        {
            using (var brush = new SolidBrush(Color.White))
                g.FillPath(brush, path);
            using (var pen = new Pen(Color.FromArgb(217, 217, 222), 1f))
                g.DrawPath(pen, path);
        }

        if (Focused)
        {
            using (var pen = new Pen(AppTheme.Accent, 1f))
            {
                pen.DashStyle = DashStyle.Dot;
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }
}

/// <summary>
/// Tombol datar tanpa tepi dengan sudut bundar (isi + teks digambar
/// sendiri; sudut di luar lengkung dicat warna latar induk agar
/// tepinya mulus anti-alias — tanpa Region, v2.3.5). BackColor =
/// warna isi, ForeColor = warna teks, sama seperti Button biasa.
/// </summary>
public class RoundedButton : Button
{
    public int CornerRadius { get; set; } = 12;

    /// <summary>v2.9.0: bila diisi, isi tombol digambar sebagai
    /// gradasi diagonal dari BackColor ke warna ini.</summary>
    public Color? GradientEnd { get; set; }

    private bool _hover;
    private bool _pressed;

    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Sengaja kosong: seluruh permukaan digambar di OnPaint,
        // termasuk sudut di luar lengkung (warna latar induk).
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Sudut di luar lengkung dicat warna latar induk langsung,
        // supaya lengkung anti-alias menyatu mulus — pengganti
        // pemotongan Region yang bergerigi (v2.3.5).
        Color surround = RoundedPanel.SurfaceOf(Parent);
        using (var bg = new SolidBrush(surround))
            g.FillRectangle(bg, ClientRectangle);

        Color fill = BackColor;
        Color fillEnd = GradientEnd ?? BackColor;
        if (!Enabled) { fill = ControlPaint.LightLight(fill); fillEnd = ControlPaint.LightLight(fillEnd); }
        else if (_pressed) { fill = ControlPaint.Dark(fill, 0.08f); fillEnd = ControlPaint.Dark(fillEnd, 0.08f); }
        else if (_hover) { fill = ControlPaint.Dark(fill, 0.04f); fillEnd = ControlPaint.Dark(fillEnd, 0.04f); }

        using (var path = RoundedPanel.RoundRectPath(ClientRectangle, CornerRadius))
        {
            if (GradientEnd.HasValue)
            {
                using (var brush = new LinearGradientBrush(ClientRectangle, fill, fillEnd, LinearGradientMode.ForwardDiagonal))
                    g.FillPath(brush, path);
            }
            else
            {
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
            }
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.WordEllipsis);
    }
}

/// <summary>Titik status bulat kecil (pengganti kotak status lama).</summary>
public class RoundDot : Control
{
    private Color _dotColor = Color.Gray;

    public RoundDot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(10, 10);
        BackColor = Color.White;
    }

    public Color DotColor
    {
        get { return _dotColor; }
        set
        {
            _dotColor = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var bg = new SolidBrush(BackColor))
            g.FillRectangle(bg, ClientRectangle);
        var r = new RectangleF(0.5f, 0.5f, Math.Max(0f, Width - 1f), Math.Max(0f, Height - 1f));
        using (var brush = new SolidBrush(_dotColor))
            g.FillEllipse(brush, r);
    }

}

/// <summary>
/// Tombol bar judul gambar-sendiri (v2.8.0 — bar judul terpadu):
/// persegi datar 46x36, glif digambar garis (min / maks / pulihkan /
/// tutup), hover memakai isian terang tema — khusus Tutup memakai
/// merah Windows #E81123 dengan glif putih. Tanpa tepi, tanpa teks.
/// </summary>
public class CaptionButton : Control
{
    public enum GlyphKind { Minimize, Maximize, Close }

    public GlyphKind Kind { get; set; } = GlyphKind.Close;
    /// <summary>True saat jendela pemilik termaksimalkan (glif pulihkan).</summary>
    public bool Maximized { get; set; }

    private bool _hover;
    private bool _pressed;

    public CaptionButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(46, 36);
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        bool closeHover = Kind == GlyphKind.Close && (_hover || _pressed);
        Color bg = closeHover ? Color.FromArgb(232, 17, 35)
            : _pressed ? ControlPaint.Dark(AppTheme.LightFill, 0.1f)
            : _hover ? AppTheme.LightFill
            : (Parent != null ? Parent.BackColor : AppTheme.Panel);
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
        Color ink = closeHover ? Color.White : AppTheme.Ink;
        using (var pen = new Pen(ink, 1f))
        {
            int cx = Width / 2, cy = Height / 2;
            switch (Kind)
            {
                case GlyphKind.Minimize:
                    g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                    break;
                case GlyphKind.Maximize:
                    if (!Maximized)
                        g.DrawRectangle(pen, cx - 5, cy - 5, 10, 10);
                    else
                    {
                        g.DrawRectangle(pen, cx - 6, cy - 2, 8, 8);
                        g.DrawRectangle(pen, cx - 2, cy - 6, 8, 8);
                    }
                    break;
                default:
                    g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                    g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                    break;
            }
        }
    }
}

/// <summary>
/// Tombol panah lipat kecil (v2.8.0) untuk strip status: menggambar
/// chevron; Down=true berarti panel log sedang terbuka.
/// </summary>
public class ChevronButton : Control
{
    public bool Down { get; set; } = true;
    private bool _hover;

    public ChevronButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(24, 24);
        TabStop = false;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var b = new SolidBrush(Parent != null ? Parent.BackColor : AppTheme.Panel))
            g.FillRectangle(b, ClientRectangle);
        Color ink = _hover ? AppTheme.Ink : AppTheme.InkDim;
        using (var pen = new Pen(ink, 1.6f))
        {
            int cx = Width / 2, cy = Height / 2;
            if (Down)
            {
                g.DrawLine(pen, cx - 5, cy - 3, cx, cy + 2);
                g.DrawLine(pen, cx, cy + 2, cx + 5, cy - 3);
            }
            else
            {
                g.DrawLine(pen, cx - 5, cy + 3, cx, cy - 2);
                g.DrawLine(pen, cx, cy - 2, cx + 5, cy + 3);
            }
        }
    }
}


/// <summary>
/// v2.9.1: garis aksen gradasi horizontal (Blurple -> fuchsia,
/// tanda tangan Midnight Rich) untuk tepi atas kartu kepala.
/// Sudutnya dibulatkan penuh dan sudut luarnya dicat warna
/// permukaan induk seperti kontrol bundar lainnya.
/// </summary>
public class GradientBar : Control
{
    public Color StartColor { get; set; } = Color.FromArgb(88, 101, 242);
    public Color EndColor { get; set; } = Color.FromArgb(235, 69, 158);

    public GradientBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var bg = new SolidBrush(RoundedPanel.SurfaceOf(Parent)))
            g.FillRectangle(bg, ClientRectangle);
        using (var path = RoundedPanel.RoundRectPath(ClientRectangle, Math.Max(1, Height / 2)))
        using (var brush = new LinearGradientBrush(ClientRectangle, StartColor, EndColor, LinearGradientMode.Horizontal))
            g.FillPath(brush, path);
    }
}
