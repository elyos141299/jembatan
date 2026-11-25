using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Jembatan;

/// <summary>
/// Kerangka Control Room v4.0.0: sidebar persisten 232px
/// (9 navigasi + kartu status bridge + tombol utilitas), bar
/// atas (judul halaman + aktivitas terakhir + EMERGENCY STOP),
/// strip banner HALT, dan host halaman. Dipasang MainForm
/// menggantikan kontainer tengah lama; seluruh perilaku lama
/// (tray, flyout, pengaturan, watchdog) tidak berubah.
/// </summary>
public sealed class RoomShell : Panel
{
    private readonly MainForm _owner;
    private readonly AgentServer _server;
    private readonly Panel _host;
    private readonly Label _pageTitle;
    private readonly Label _activityLabel;
    private readonly Panel _haltBanner;
    private readonly Button _stopBtn;
    private readonly Button _accessBtn;
    private readonly RoundDot _bridgeDot;
    private readonly Label _bridgeText;
    private readonly Dictionary<string, Control> _pages = new Dictionary<string, Control>();
    private readonly Dictionary<string, (Panel Row, Panel Bar, Label Text)> _nav =
        new Dictionary<string, (Panel, Panel, Label)>();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly OverviewPage _overview;
    private readonly ActivityPage _activity;
    private readonly CommandPage _command;
    private readonly DesktopPage _desktop;
    private readonly LogsPage _logs;
    private readonly AutomationPage _automation;
    private readonly ConnectionsPage _connections;
    private readonly SecurityPage _security;
    private string _currentPage = "";
    private DateTime _lastActivity = DateTime.MinValue;

    public RoomShell(MainForm owner, AgentServer server)
    {
        _owner = owner;
        _server = server;
        Dock = DockStyle.Fill;
        BackColor = RoomTheme.Bg;

        // ---------- Host halaman (ditambah pertama = terproses terakhir) ----------
        _host = new Panel { Dock = DockStyle.Fill, BackColor = RoomTheme.Bg };

        // ---------- Banner HALT ----------
        _haltBanner = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = RoomTheme.RedBanner,
            Visible = false,
        };
        var haltText = new Label
        {
            Dock = DockStyle.Fill,
            Text = "EMERGENCY STOP AKTIF — semua input Muse ditahan. Pelepasan hanya dari aplikasi ini.",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0),
            BackColor = RoomTheme.RedBanner,
        };
        _haltBanner.Controls.Add(haltText);

        // ---------- Bar atas ----------
        var topbar = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = RoomTheme.Bg };
        var topHair = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = RoomTheme.Hairline };
        _pageTitle = new Label
        {
            Text = "Overview",
            ForeColor = RoomTheme.Text,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 15),
            BackColor = RoomTheme.Bg,
        };
        _activityLabel = new Label
        {
            Text = "Belum ada aktivitas",
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 9f),
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = RoomTheme.Bg,
        };
        _stopBtn = new Button
        {
            Text = "■  EMERGENCY STOP",
            Size = new Size(178, 34),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = RoomTheme.RedFill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _stopBtn.FlatAppearance.BorderSize = 0;
        _stopBtn.Click += (s, e) => OnStopClicked();
        topbar.Controls.Add(_pageTitle);
        topbar.Controls.Add(_activityLabel);
        topbar.Controls.Add(_stopBtn);
        topbar.Controls.Add(topHair);
        topbar.Resize += (s, e) =>
        {
            _stopBtn.Location = new Point(topbar.Width - _stopBtn.Width - 16, 10);
            _activityLabel.Location = new Point(_stopBtn.Left - _activityLabel.Width - 14, 19);
        };

        // ---------- Sidebar ----------
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 232, BackColor = RoomTheme.Sidebar };
        var sideHair = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = RoomTheme.Hairline };
        sidebar.Controls.Add(sideHair);

        // Navigasi (Dock Fill; ditambah sebelum footer & kepala
        // agar terproses dok paling akhir di sidebar).
        var navPanel = new Panel { Dock = DockStyle.Fill, BackColor = RoomTheme.Sidebar };
        // Urutan tambah terbalik dari urutan tampil (aturan dok).
        navPanel.Controls.Add(NavRow("settings", "Settings"));
        navPanel.Controls.Add(NavRow("logs", "System Logs"));
        navPanel.Controls.Add(NavRow("connections", "Connections"));
        navPanel.Controls.Add(NavRow("security", "Permissions & Security"));
        navPanel.Controls.Add(NavRow("automation", "Automation"));
        navPanel.Controls.Add(NavRow("desktop", "Desktop Control"));
        navPanel.Controls.Add(NavRow("activity", "Activity Monitor"));
        navPanel.Controls.Add(NavRow("command", "Command Center"));
        navPanel.Controls.Add(NavRow("overview", "Overview"));
        sidebar.Controls.Add(navPanel);

        // Kaki sidebar: kartu status + utilitas + putuskan.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 158, BackColor = RoomTheme.Sidebar, Padding = new Padding(12, 8, 12, 12) };
        var statusCard = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Height = 56,
            CornerRadius = 10,
            FillColor = RoomTheme.Card,
            BorderColor = RoomTheme.CardEdge,
            BackColor = RoomTheme.Sidebar,
        };
        _bridgeDot = new RoundDot { Size = new Size(10, 10), Location = new Point(12, 12), DotColor = AppTheme.DotWaiting, BackColor = RoomTheme.Card };
        _bridgeText = new Label
        {
            Text = "Bridge: …",
            ForeColor = RoomTheme.Text,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(28, 8),
            BackColor = RoomTheme.Card,
        };
        var bridgeSub = new Label
        {
            Text = "Port 8765 • Tailscale",
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 8.5f),
            AutoSize = true,
            Location = new Point(28, 28),
            BackColor = RoomTheme.Card,
        };
        statusCard.Controls.Add(_bridgeDot);
        statusCard.Controls.Add(_bridgeText);
        statusCard.Controls.Add(bridgeSub);
        footer.Controls.Add(statusCard);

        var utilRow = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = RoomTheme.Sidebar, Padding = new Padding(0, 8, 0, 0) };
        var chatBtn = UtilButton("Chat");
        chatBtn.Dock = DockStyle.Left; chatBtn.Width = 62;
        chatBtn.Click += (s, e) => _owner.ToggleChatPanel();
        var logBtn = UtilButton("Log");
        logBtn.Dock = DockStyle.Left; logBtn.Width = 62;
        logBtn.Click += (s, e) => _owner.ToggleLogPanel();
        var setBtn = UtilButton("Pengaturan");
        setBtn.Dock = DockStyle.Fill;
        setBtn.Click += (s, e) => _owner.OpenSettings();
        utilRow.Controls.Add(setBtn);
        utilRow.Controls.Add(logBtn);
        utilRow.Controls.Add(chatBtn);
        footer.Controls.Add(utilRow);

        _accessBtn = new Button
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Text = "Putuskan Akses",
            FlatStyle = FlatStyle.Flat,
            BackColor = RoomTheme.Panel,
            ForeColor = RoomTheme.Text,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _accessBtn.FlatAppearance.BorderColor = RoomTheme.CardEdge;
        _accessBtn.Click += (s, e) => { _owner.ToggleServer(); RefreshShell(); };
        footer.Controls.Add(_accessBtn);
        sidebar.Controls.Add(footer);

        // Kepala sidebar: logo + nama + versi.
        var head = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = RoomTheme.Sidebar };
        var logo = new PictureBox
        {
            Size = new Size(30, 30),
            Location = new Point(14, 16),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        try
        {
            using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                if (ic != null) logo.Image = ic.ToBitmap();
        }
        catch { /* tanpa logo pun rapi */ }
        var nameLbl = new Label
        {
            Text = "MuseLink",
            ForeColor = RoomTheme.Text,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(52, 14),
            BackColor = RoomTheme.Sidebar,
        };
        var verLbl = new Label
        {
            Text = "Control Room • v" + MainForm.DisplayVersion,
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 8.5f),
            AutoSize = true,
            Location = new Point(53, 38),
            BackColor = RoomTheme.Sidebar,
        };
        head.Controls.Add(logo);
        head.Controls.Add(nameLbl);
        head.Controls.Add(verLbl);
        sidebar.Controls.Add(head);

        // ---------- Rakit kerangka (urutan dok: sidebar dulu) ----------
        Controls.Add(_host);
        Controls.Add(_haltBanner);
        Controls.Add(topbar);
        Controls.Add(sidebar);

        // ---------- Halaman ----------
        _overview = new OverviewPage(owner, server);
        _activity = new ActivityPage(server);
        _command = new CommandPage(server);
        _desktop = new DesktopPage();
        _logs = new LogsPage(owner, server);
        _automation = new AutomationPage();
        _connections = new ConnectionsPage(owner, server);
        _security = new SecurityPage(server);
        RegisterPage("overview", _overview);
        RegisterPage("activity", _activity);
        RegisterPage("command", _command);
        RegisterPage("desktop", _desktop);
        RegisterPage("automation", _automation);
        RegisterPage("security", _security);
        RegisterPage("connections", _connections);
        RegisterPage("logs", _logs);
        ShowPage("overview");

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (s, e) => RefreshShell();
        _timer.Start();
        RefreshShell();
    }

    private static Button UtilButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = RoomTheme.Panel,
            ForeColor = RoomTheme.Dim,
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = RoomTheme.CardEdge;
        return b;
    }

    private Panel NavRow(string key, string text)
    {
        var row = new Panel
        {
            Dock = DockStyle.Top,
            Height = 38,
            BackColor = RoomTheme.Sidebar,
            Cursor = Cursors.Hand,
        };
        var bar = new Panel { Dock = DockStyle.Left, Width = 3, BackColor = RoomTheme.Accent, Visible = false };
        var lbl = new Label
        {
            Dock = DockStyle.Fill,
            Text = "   " + text,
            ForeColor = RoomTheme.Dim,
            Font = new Font("Segoe UI", 10f),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand,
            BackColor = RoomTheme.Sidebar,
        };
        row.Controls.Add(lbl);
        row.Controls.Add(bar);
        EventHandler click = (s, e) =>
        {
            if (key == "settings") { _owner.OpenSettings(); return; }
            ShowPage(key);
        };
        row.Click += click;
        lbl.Click += click;
        row.MouseEnter += (s, e) => { if (_currentPage != key) { row.BackColor = RoomTheme.Hover; lbl.BackColor = RoomTheme.Hover; } };
        row.MouseLeave += (s, e) => { if (_currentPage != key) { row.BackColor = RoomTheme.Sidebar; lbl.BackColor = RoomTheme.Sidebar; } };
        lbl.MouseEnter += (s, e) => { if (_currentPage != key) { row.BackColor = RoomTheme.Hover; lbl.BackColor = RoomTheme.Hover; } };
        lbl.MouseLeave += (s, e) => { if (_currentPage != key) { row.BackColor = RoomTheme.Sidebar; lbl.BackColor = RoomTheme.Sidebar; } };
        _nav[key] = (row, bar, lbl);
        return row;
    }

    private void RegisterPage(string key, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _host.Controls.Add(page);
        _pages[key] = page;
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        foreach (var kv in _pages) kv.Value.Visible = kv.Key == key;
        foreach (var kv in _nav)
        {
            bool aktif = kv.Key == key;
            kv.Value.Bar.Visible = aktif;
            kv.Value.Row.BackColor = aktif ? RoomTheme.Hover : RoomTheme.Sidebar;
            kv.Value.Text.BackColor = kv.Value.Row.BackColor;
            kv.Value.Text.ForeColor = aktif ? RoomTheme.Text : RoomTheme.Dim;
            kv.Value.Text.Font = new Font("Segoe UI", 10f, aktif ? FontStyle.Bold : FontStyle.Regular);
        }
        _currentPage = key;
        _pageTitle.Text = key switch
        {
            "overview" => "Overview",
            "command" => "Command Center",
            "activity" => "Activity Monitor",
            "desktop" => "Desktop Control",
            "automation" => "Automation",
            "security" => "Permissions & Security",
            "connections" => "Connections",
            "logs" => "System Logs",
            _ => "Overview",
        };
        if (key == "overview") _overview.RefreshAsync();
        if (key == "activity") _activity.RefreshList();
        if (key == "desktop") _desktop.RefreshAll();
        if (key == "logs") _logs.RefreshList();
        if (key == "automation") _automation.RefreshList();
        if (key == "connections") _connections.RefreshData();
        if (key == "security") _security.RefreshData();
    }

    private void OnStopClicked()
    {
        if (!_server.InputHalted)
        {
            var jawab = MessageBox.Show(
                "Hentikan SEMUA input Muse sekarang?\n\nOperasi yang antre dibatalkan, shell dihentikan, dan semua kendali ditolak sampai kamu melepas Stop dari aplikasi ini.",
                "Emergency Stop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (jawab != DialogResult.Yes) return;
            _server.SetHalt(true, "aplikasi (tombol Emergency Stop)");
        }
        else
        {
            _server.SetHalt(false, "aplikasi (tombol Lepas Stop)");
        }
        RefreshShell();
    }

    /// <summary>Dipanggil MainForm.OnActivity (utas UI).</summary>
    public void NotifyActivity()
    {
        _lastActivity = DateTime.Now;
    }

    private void RefreshShell()
    {
        try
        {
            bool halted = _server.InputHalted;
            _haltBanner.Visible = halted;
            _stopBtn.Text = halted ? "▶  LEPAS STOP" : "■  EMERGENCY STOP";
            _stopBtn.BackColor = halted ? Color.FromArgb(0x3E, 0x6B, 0x34) : RoomTheme.RedFill;
            bool jalan = _server.IsRunning;
            _bridgeDot.DotColor = halted ? RoomTheme.Red
                : jalan ? RoomTheme.Green : AppTheme.DotStopped;
            _bridgeText.Text = halted ? "Bridge: STOP DARURAT"
                : jalan ? "Bridge: Mendengarkan" : "Bridge: Berhenti";
            _accessBtn.Text = jalan ? "Putuskan Akses" : "Sambungkan";
            _activityLabel.Text = _lastActivity == DateTime.MinValue
                ? "Belum ada aktivitas"
                : "Aktivitas terakhir " + _lastActivity.ToString("HH:mm:ss");
            _activityLabel.Location = new Point(_stopBtn.Left - _activityLabel.Width - 14, 19);
            if (_currentPage == "overview" && _overview.Visible) _overview.RefreshAsync();
            if (_currentPage == "activity" && _activity.Visible) _activity.RefreshIfAuto();
            if (_currentPage == "desktop" && _desktop.Visible) _desktop.RefreshIfVisible();
            if (_currentPage == "logs" && _logs.Visible) _logs.RefreshIfAuto();
            if (_currentPage == "connections" && _connections.Visible) _connections.RefreshIfVisible();
            if (_currentPage == "security" && _security.Visible) _security.RefreshIfVisible();
        }
        catch { /* penyegaran kerangka tidak boleh menjatuhkan aplikasi */ }
    }
}
