using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jembatan;

/// <summary>
/// Pengaturan MuseLink. v3.2.0 (koreksi user atas v3.1.0): bukan
/// lagi jendela terpisah — kini FLYOUT tinta yang menempel pada
/// tombol roda gigi di rel dan muncul dengan geser + pudar, persis
/// seperti Log Aktivitas (tema tinta yang sama: latar LogBg, kartu
/// InkCard, teks InkOnDark/InkFaint). Isi tetap sama: saklar Jalan
/// otomatis / Mode Administrator / Jangan tidur, dan kartu KONEKSI
/// dengan tombol PUTUSKAN — sejak v3.2.0 tombol putus/sambung
/// akses HANYA ada di sini (grup AKSES di panel dihapus user
/// karena duplikat).
///
/// Jendela ini hanya WAJAH: keadaan &amp; logika tetap milik
/// MainForm (ApplyAutoStart / ApplyAdminMode / ApplyKeepAwake /
/// ToggleServer). Satu instans, non-modal; Esc / klik roda gigi
/// lagi / tombol ✕ menutupnya dengan animasi.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly MainForm _owner;

    private RoundedPanel _cardUmum;
    private RoundedPanel _cardKoneksi;
    private readonly List<Label> _labelsInk = new List<Label>();
    private readonly List<Label> _labelsDim = new List<Label>();
    private ToggleSwitch _autoStartCheck;
    private ToggleSwitch _adminCheck;
    private ToggleSwitch _awakeCheck;
    private RoundedButton _serverButton;
    private Label _serverNote;

    // Mekanik flyout (pola yang sama dengan LogFlyoutForm).
    private readonly System.Windows.Forms.Timer _anim;
    private int _animTargetX;
    private int _animStartX;
    private long _animStartTick;
    private bool _animHide;

    public SettingsForm(MainForm owner)
    {
        _owner = owner;
        Text = "Pengaturan — MuseLink";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(400, 452);
        Font = new Font("Segoe UI", 10f);
        KeyPreview = true;
        if (owner.Icon != null) Icon = owner.Icon;

        _anim = new System.Windows.Forms.Timer { Interval = 15 };
        _anim.Tick += AnimTick;

        BuildUi();

        // Isi keadaan awal DULU, pasang handler SESUDAHNYA — pola
        // yang sama seperti InitStartOptions lama, agar pengisian
        // awal tidak memicu penulisan registry / log.
        _autoStartCheck.Checked = owner.AutoStartEnabled;
        _adminCheck.Checked = Settings.AdminMode;
        _awakeCheck.Checked = Settings.KeepAwake;
        _autoStartCheck.CheckedChanged += (s, e) => _owner.ApplyAutoStart(_autoStartCheck.Checked);
        _adminCheck.CheckedChanged += (s, e) => _owner.ApplyAdminMode(_adminCheck.Checked);
        _awakeCheck.CheckedChanged += (s, e) => _owner.ApplyKeepAwake(_awakeCheck.Checked);

        RefreshServerButton();
        ApplyTheme();
    }

    // Bayangan jatuh kelas jendela (pola LogFlyoutForm).
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    // Sudut bundar 16px + bingkai RailHover — persis flyout log.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width < 2 || Height < 2) return;
        using (var path = new GraphicsPath())
        {
            int r = 16, w = Width, h = Height;
            path.AddArc(0, 0, r, r, 180, 90);
            path.AddArc(w - r, 0, r, r, 270, 90);
            path.AddArc(w - r, h - r, r, r, 0, 90);
            path.AddArc(0, h - r, r, r, 90, 90);
            path.CloseFigure();
            Region = new Region(path);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(AppTheme.RailHover, 1f))
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) HideAnimated();
    }

    /// <summary>
    /// Tampilkan sebagai flyout menempel di tepi kanan tombol
    /// jangkar (roda gigi rel), bergeser masuk + memudar — pola
    /// ShowAnchored yang sama dengan flyout log.
    /// </summary>
    public void ShowAnchored(Rectangle anchorScreen, Form owner)
    {
        var wa = Screen.FromRectangle(anchorScreen).WorkingArea;
        int targetX = anchorScreen.Right + 10;
        int y = anchorScreen.Top - 8;
        if (y + Height > wa.Bottom - 8) y = wa.Bottom - Height - 8;
        if (y < wa.Top + 8) y = wa.Top + 8;
        if (targetX + Width > wa.Right - 8) targetX = wa.Right - Width - 8;
        _anim.Stop();
        _animHide = false;
        _animTargetX = targetX;
        _animStartX = targetX - 14;
        Location = new Point(_animStartX, y);
        Opacity = 0;
        Show(owner);
        _animStartTick = Environment.TickCount64;
        _anim.Start();
    }

    /// <summary>Tutup dengan geser keluar + pudar, lalu sembunyi.</summary>
    public void HideAnimated()
    {
        if (!Visible) return;
        _anim.Stop();
        _animHide = true;
        _animStartX = Left;
        _animTargetX = Left - 14;
        _animStartTick = Environment.TickCount64;
        _anim.Start();
    }

    public void HideImmediate()
    {
        _anim.Stop();
        Hide();
    }

    private void AnimTick(object sender, EventArgs e)
    {
        double t = Math.Min(1.0, (Environment.TickCount64 - _animStartTick) / 135.0);
        double ease = 1 - Math.Pow(1 - t, 3);
        int x = _animStartX + (int)((_animTargetX - _animStartX) * ease);
        Location = new Point(x, Top);
        Opacity = _animHide ? 1.0 - ease : ease;
        if (t >= 1)
        {
            _anim.Stop();
            if (_animHide) { Hide(); Opacity = 1.0; }
        }
    }

    private Label MakeLabel(string text, int x, int y, float size, bool bold, bool dim)
    {
        var l = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            AutoSize = true,
            Location = new Point(x, y),
        };
        (dim ? _labelsDim : _labelsInk).Add(l);
        Controls.Add(l);
        return l;
    }

    private ToggleSwitch MakeToggleRow(Control parent, string label, int y)
    {
        var t = new ToggleSwitch { Location = new Point(14, y) };
        parent.Controls.Add(t);
        var l = new Label
        {
            Text = label,
            Font = new Font("Segoe UI", 10f),
            AutoSize = true,
            Location = new Point(62, y + 2),
        };
        _labelsInk.Add(l);
        parent.Controls.Add(l);
        return t;
    }

    private void BuildUi()
    {
        MakeLabel("Pengaturan", 20, 14, 15f, true, false);
        var close = new Label
        {
            Text = "\uE711",
            Font = new Font("Segoe MDL2 Assets", 11f),
            ForeColor = AppTheme.InkOnDark,
            BackColor = AppTheme.LogBg,
            Size = new Size(38, 32),
            Location = new Point(354, 8),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
        };
        close.Click += (s, e) => HideAnimated();
        Controls.Add(close);

        MakeLabel("UMUM", 20, 56, 9f, true, true);
        _cardUmum = new RoundedPanel
        {
            Location = new Point(20, 78),
            Size = new Size(360, 142),
            CornerRadius = 8,
            Padding = new Padding(12),
        };
        Controls.Add(_cardUmum);
        _autoStartCheck = MakeToggleRow(_cardUmum, "Jalan otomatis", 14);
        _adminCheck = MakeToggleRow(_cardUmum, "Mode Administrator", 60);
        _awakeCheck = MakeToggleRow(_cardUmum, "Jangan tidur", 106);

        MakeLabel("KONEKSI", 20, 242, 9f, true, true);
        _cardKoneksi = new RoundedPanel
        {
            Location = new Point(20, 264),
            Size = new Size(360, 100),
            CornerRadius = 8,
            Padding = new Padding(12),
        };
        Controls.Add(_cardKoneksi);
        _serverNote = new Label
        {
            Text = "Putuskan menghentikan akses Muse ke PC ini sepenuhnya sampai disambung lagi.",
            Font = new Font("Segoe UI", 9f),
            AutoSize = false,
            Size = new Size(332, 30),
            Location = new Point(14, 10),
        };
        _labelsDim.Add(_serverNote);
        _cardKoneksi.Controls.Add(_serverNote);
        _serverButton = new RoundedButton
        {
            Text = "PUTUSKAN",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.White,
            CornerRadius = 8,
            Location = new Point(14, 48),
            Size = new Size(332, 44),
        };
        _serverButton.Click += (s, e) => { _owner.ToggleServer(); RefreshServerButton(); };
        _cardKoneksi.Controls.Add(_serverButton);

        MakeLabel("MuseLink v3.5.0", 20, 372, 8.5f, false, true);
    }

    /// <summary>Teks &amp; warna tombol server mengikuti keadaan listener.</summary>
    public void RefreshServerButton()
    {
        if (_serverButton == null) return;
        if (_owner.ServerRunning)
        {
            _serverButton.Text = "PUTUSKAN";
            _serverButton.BackColor = AppTheme.Red;
        }
        else
        {
            _serverButton.Text = "Sambung lagi";
            _serverButton.BackColor = AppTheme.Accent;
        }
    }

    /// <summary>
    /// Cat ulang mengikuti tema tinta Log Aktivitas (v3.2.0) —
    /// latar LogBg, kartu InkCard bertepi RailHover, teks terang.
    /// </summary>
    public void ApplyTheme()
    {
        BackColor = AppTheme.LogBg;
        foreach (var card in new[] { _cardUmum, _cardKoneksi })
        {
            if (card == null) continue;
            card.FillColor = AppTheme.InkCard;
            card.BorderColor = AppTheme.RailHover;
            card.BackColor = AppTheme.LogBg;
        }
        foreach (var l in _labelsInk) { l.ForeColor = AppTheme.InkOnDark; l.BackColor = AppTheme.LogBg; }
        foreach (var l in _labelsDim) { l.ForeColor = AppTheme.InkFaint; l.BackColor = AppTheme.LogBg; }
        foreach (var t in new[] { _autoStartCheck, _adminCheck, _awakeCheck })
            if (t != null) t.BackColor = AppTheme.InkCard;
        // Label di dalam kartu berlatar kartu, bukan latar form.
        foreach (Control c in _cardUmum.Controls) if (c is Label l1) l1.BackColor = AppTheme.InkCard;
        foreach (Control c in _cardKoneksi.Controls) if (c is Label l3) l3.BackColor = AppTheme.InkCard;
        RefreshServerButton();
        Invalidate(true);
    }
}
