using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Jembatan;

/// <summary>
/// Jendela Pengaturan MuseLink (v2.5.0, tata letak atas permintaan
/// user): semua saklar yang dulu berjejer di kartu kepala jendela
/// utama pindah ke sini — Jalan otomatis, Mode Administrator,
/// Jangan tidur — dan tombol besar PUTUSKAN / Sambung lagi yang
/// dulu
/// memenuhi kiri bawah jendela utama.
///
/// Jendela ini hanya WAJAH: keadaan &amp; logika tetap milik
/// MainForm (ApplyAutoStart / ApplyAdminMode / ApplyKeepAwake /
/// SetTheme / ToggleServer), jendela utama yang menampung
/// servernya. Dibuka dari tombol roda gigi di pojok kanan atas
/// kartu kepala; satu instans saja, non-modal.
/// </summary>
public sealed class SettingsForm : Form
{
    // v2.9.1: bar judul jendela dipaksa mengikuti tema (DWM
    // immersive dark mode) agar tidak belang putih di tema gelap.
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

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

    public SettingsForm(MainForm owner)
    {
        _owner = owner;
        Text = "Pengaturan — MuseLink";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(400, 452);
        Font = new Font("Segoe UI", 10f);
        if (owner.Icon != null) Icon = owner.Icon;

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

        // v3.0.1: muncul dengan pudar masuk singkat — tidak polos
        // muncul tiba-tiba (permintaan user, sejalan flyout log).
        Opacity = 0;
        Shown += (s, e) =>
        {
            var fade = new System.Windows.Forms.Timer { Interval = 15 };
            fade.Tick += (s2, e2) =>
            {
                Opacity = Math.Min(1.0, Opacity + 0.16);
                if (Opacity >= 1.0) fade.Stop();
            };
            fade.Start();
        };
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

        MakeLabel("MuseLink v3.0.3", 20, 372, 8.5f, false, true);
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

    /// <summary>Cat ulang seluruh jendela mengikuti tema efektif.</summary>
    public void ApplyTheme()
    {
        BackColor = AppTheme.Bg;
        foreach (var card in new[] { _cardUmum, _cardKoneksi })
        {
            if (card == null) continue;
            card.FillColor = AppTheme.Card;
            card.BorderColor = AppTheme.Hairline;
            card.BackColor = AppTheme.Bg;
        }
        foreach (var l in _labelsInk) { l.ForeColor = AppTheme.Ink; l.BackColor = AppTheme.Bg; }
        foreach (var l in _labelsDim) { l.ForeColor = AppTheme.InkDim; l.BackColor = AppTheme.Bg; }
        foreach (var t in new[] { _autoStartCheck, _adminCheck, _awakeCheck })
            if (t != null) t.BackColor = AppTheme.Card;
        // Label di dalam kartu berlatar kartu, bukan latar form.
        foreach (Control c in _cardUmum.Controls) if (c is Label l1) l1.BackColor = AppTheme.Card;
        foreach (Control c in _cardKoneksi.Controls) if (c is Label l3) l3.BackColor = AppTheme.Card;
        RefreshServerButton();
        try
        {
            int dark = AppTheme.IsDark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        }
        catch { /* OS lama tanpa DWM: abaikan */ }
        Invalidate(true);
    }
}
