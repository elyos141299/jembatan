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
/// (bawaan 16px) dan seluruh kontrol (termasuk anak-anaknya) dipotong
/// mengikuti bentuk bundar itu lewat Region.
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

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width > 0 && Height > 0)
        {
            using (var path = RoundRectPath(ClientRectangle, CornerRadius))
                Region = new Region(path);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
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
        Size = new Size(44, 26);
        MinimumSize = new Size(44, 26);
        MaximumSize = new Size(44, 26);
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
        using (var bg = new SolidBrush(BackColor))
            g.FillRectangle(bg, ClientRectangle);

        var trackRect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color track = _checked
            ? Color.FromArgb(52, 199, 89)
            : Color.FromArgb(232, 232, 237);
        using (var path = RoundedPanel.RoundRectPath(trackRect, Height / 2))
        using (var brush = new SolidBrush(track))
            g.FillPath(brush, path);

        const int knob = 22;
        int kx = _checked ? Width - knob - 2 : 2;
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
            using (var pen = new Pen(Color.FromArgb(0, 113, 227), 1f))
            {
                pen.DashStyle = DashStyle.Dot;
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }
}

/// <summary>
/// Tombol datar tanpa tepi dengan sudut bundar (Region dipotong
/// mengikuti jalur bundar; isi + teks digambar sendiri). BackColor =
/// warna isi, ForeColor = warna teks, sama seperti Button biasa.
/// </summary>
public class RoundedButton : Button
{
    public int CornerRadius { get; set; } = 12;

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

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width > 0 && Height > 0)
        {
            using (var path = RoundedPanel.RoundRectPath(ClientRectangle, CornerRadius))
                Region = new Region(path);
        }
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
        // Sengaja kosong: Region sudah memotong bentuknya dan isi
        // digambar di OnPaint — latar di luar sudut ditangani induk.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill = BackColor;
        if (!Enabled) fill = ControlPaint.LightLight(fill);
        else if (_pressed) fill = ControlPaint.Dark(fill, 0.08f);
        else if (_hover) fill = ControlPaint.Dark(fill, 0.04f);

        using (var path = RoundedPanel.RoundRectPath(ClientRectangle, CornerRadius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

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
        using (var bg = new SolidBrush(BackColor))
            g.FillRectangle(bg, ClientRectangle);
        var r = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        using (var brush = new SolidBrush(_dotColor))
            g.FillEllipse(brush, r);
    }
}
