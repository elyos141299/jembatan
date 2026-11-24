using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Jembatan;

/// <summary>
/// Jendela Pengaturan MuseLink (v2.5.0, tata letak atas permintaan
/// user): semua saklar yang dulu berjejer di kartu kepala jendela
/// utama pindah ke sini — Jalan otomatis, Mode Administrator,
/// Jangan tidur — ditambah pilihan Tema (Terang / Gelap / Ikuti
/// perangkat) dan tombol besar PUTUSKAN / Sambung lagi yang dulu
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
    private readonly MainForm _owner;

    private RoundedPanel _cardUmum;
    private RoundedPanel _cardTema;
    private RoundedPanel _cardKoneksi;
    private readonly List<Label> _labelsInk = new List<Label>();
    private readonly List<Label> _labelsDim = new List<Label>();
    private ToggleSwitch _autoStartCheck;
    private ToggleSwitch _adminCheck;
    private ToggleSwitch _awakeCheck;
    private RoundedButton _pillTerang;
    private RoundedButton _pillGelap;
    private RoundedButton _pillPerangkat;
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
        ClientSize = new Size(400, 560);
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

        RefreshPills();
        RefreshServerButton();
        ApplyTheme();
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
            Location = new Point(66, y + 3),
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
            Size = new Size(360, 148),
            CornerRadius = 16,
            Padding = new Padding(12),
        };
        Controls.Add(_cardUmum);
        _autoStartCheck = MakeToggleRow(_cardUmum, "Jalan otomatis", 14);
        _adminCheck = MakeToggleRow(_cardUmum, "Mode Administrator", 60);
        _awakeCheck = MakeToggleRow(_cardUmum, "Jangan tidur", 106);

        MakeLabel("TAMPILAN", 20, 242, 9f, true, true);
        _cardTema = new RoundedPanel
        {
            Location = new Point(20, 264),
            Size = new Size(360, 92),
            CornerRadius = 16,
            Padding = new Padding(12),
        };
        Controls.Add(_cardTema);
        var temaLabel = new Label
        {
            Text = "Tema",
            Font = new Font("Segoe UI", 10f),
            AutoSize = true,
            Location = new Point(14, 12),
        };
        _labelsInk.Add(temaLabel);
        _cardTema.Controls.Add(temaLabel);
        _pillTerang = MakePill("Terang", 14, () => _owner.SetTheme("light"));
        _pillGelap = MakePill("Gelap", 126, () => _owner.SetTheme("dark"));
        _pillPerangkat = MakePill("Perangkat", 238, () => _owner.SetTheme("device"));
        _cardTema.Controls.Add(_pillTerang);
        _cardTema.Controls.Add(_pillGelap);
        _cardTema.Controls.Add(_pillPerangkat);

        MakeLabel("KONEKSI", 20, 372, 9f, true, true);
        _cardKoneksi = new RoundedPanel
        {
            Location = new Point(20, 394),
            Size = new Size(360, 106),
            CornerRadius = 16,
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
            CornerRadius = 12,
            Location = new Point(14, 48),
            Size = new Size(332, 44),
        };
        _serverButton.Click += (s, e) => { _owner.ToggleServer(); RefreshServerButton(); };
        _cardKoneksi.Controls.Add(_serverButton);

        MakeLabel("MuseLink v2.5.0", 20, 518, 8.5f, false, true);
    }

    private RoundedButton MakePill(string text, int x, Action onClick)
    {
        var b = new RoundedButton
        {
            Text = text,
            Font = new Font("Segoe UI", 9.5f),
            CornerRadius = 12,
            Location = new Point(x, 42),
            Size = new Size(104, 36),
        };
        b.Click += (s, e) => { onClick(); RefreshPills(); };
        return b;
    }

    /// <summary>Warna pil tema mengikuti pilihan tersimpan saat ini.</summary>
    public void RefreshPills()
    {
        string cur = Settings.Theme;
        PaintPill(_pillTerang, cur == "light");
        PaintPill(_pillGelap, cur == "dark");
        PaintPill(_pillPerangkat, cur == "device");
    }

    private static void PaintPill(RoundedButton b, bool selected)
    {
        if (b == null) return;
        b.BackColor = selected ? AppTheme.Accent : AppTheme.LightFill;
        b.ForeColor = selected ? Color.White : AppTheme.Ink;
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
        foreach (var card in new[] { _cardUmum, _cardTema, _cardKoneksi })
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
        foreach (Control c in _cardTema.Controls) if (c is Label l2) l2.BackColor = AppTheme.Card;
        foreach (Control c in _cardKoneksi.Controls) if (c is Label l3) l3.BackColor = AppTheme.Card;
        RefreshPills();
        RefreshServerButton();
        Invalidate(true);
    }
}
