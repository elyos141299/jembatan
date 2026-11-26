using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Jembatan;

/// <summary>
/// Integrations v5.3.0 (pengganti Connections, persis frame
/// Figma): grid 6 kartu aplikasi + panel Integration Details.
/// Status "Terhubung" SUNGGUH diuji dari keadaan PC: proses
/// aplikasi berjalan (Chrome/OBS/Figma/Spotify/WhatsApp) atau
/// gh CLI ditemukan di PATH (GitHub). Test Connection menguji
/// ulang; Buka menjalankan aplikasi bila jalurnya ditemukan;
/// Configure masih contoh desain (fitur menyusul). Klik kartu
/// memilih aplikasi untuk panel detail.
/// </summary>
public sealed class IntegrationsPage : UserControl, IRoomPage
{
    private sealed class AppInfo
    {
        public string Name = "";
        public string Desc = "";
        public string Letter = "";
        public string ProcessName = "";   // nama proses Windows ("" = khusus)
        public string ExeHint = "";       // nama exe untuk Buka
        public bool Connected;
        public Border Badge;
        public string DetailLine = "";
    }

    private readonly List<AppInfo> _apps = new List<AppInfo>();
    private readonly TextBlock _summary, _detTitle, _detLine1, _detLine2;
    private readonly Button _openBtn;
    private AppInfo _selected;

    public IntegrationsPage()
    {
        _apps.Add(new AppInfo { Name = "Chrome", Desc = "Browser & otomasi web", Letter = "C", ProcessName = "chrome", ExeHint = "chrome.exe" });
        _apps.Add(new AppInfo { Name = "OBS Studio", Desc = "Streaming & overlay", Letter = "O", ProcessName = "obs64", ExeHint = "obs64.exe" });
        _apps.Add(new AppInfo { Name = "Figma", Desc = "Desain UI/UX langsung di PC", Letter = "F", ProcessName = "Figma", ExeHint = "Figma.exe" });
        _apps.Add(new AppInfo { Name = "GitHub", Desc = "Repo, Actions & rilis", Letter = "G", ProcessName = "", ExeHint = "gh.exe" });
        _apps.Add(new AppInfo { Name = "Spotify", Desc = "Musik & playlist", Letter = "S", ProcessName = "Spotify", ExeHint = "Spotify.exe" });
        _apps.Add(new AppInfo { Name = "WhatsApp", Desc = "Pesan & notifikasi", Letter = "W", ProcessName = "WhatsApp", ExeHint = "WhatsApp.exe" });

        var root = new StackPanel();

        // ---- Header ----
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = UiKit.SectionLabel("APPLICATION INTEGRATIONS");
        label.FontSize = 10.5;
        label.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(label);
        _summary = new TextBlock
        {
            Text = "6 aplikasi • … terhubung",
            FontSize = 11.5,
            Foreground = UiKit.Res("CrMuted"),
        };
        Grid.SetColumn(_summary, 1);
        header.Children.Add(_summary);
        root.Children.Add(header);

        // ---- Grid kartu ----
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var app in _apps) grid.Children.Add(BuildCard(app));
        root.Children.Add(grid);

        // ---- Panel detail ----
        var detStack = new StackPanel();
        _detLine1 = new TextBlock
        {
            Text = "—",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        detStack.Children.Add(_detLine1);
        _detLine2 = UiKit.Body("");
        _detLine2.Margin = new Thickness(0, 6, 0, 0);
        detStack.Children.Add(_detLine2);
        _openBtn = UiKit.PrimaryBtn("Buka Figma", Open_Click);
        _openBtn.Margin = new Thickness(0, 12, 0, 0);
        _openBtn.HorizontalAlignment = HorizontalAlignment.Left;
        detStack.Children.Add(_openBtn);
        _detTitle = UiKit.CardTitle("INTEGRATION DETAILS — FIGMA");
        var detInner = new StackPanel();
        detInner.Children.Add(_detTitle);
        detInner.Children.Add(detStack);
        var detCard = new Border
        {
            Style = (Style)Application.Current.FindResource("CrCardStyle"),
            Child = detInner,
            Margin = new Thickness(0, 16, 0, 0),
        };
        root.Children.Add(detCard);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public void OnShown()
    {
        RefreshStatuses();
        SelectApp(_apps[2]); // Figma — sama seperti contoh desain
    }

    public void OnHidden() { }

    private Border BuildCard(AppInfo app)
    {
        var stack = new StackPanel { Margin = new Thickness(16) };
        var icon = new Border
        {
            Background = UiKit.Res("CrElevated"),
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Width = 44,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = app.Letter,
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = UiKit.Res("CrAccent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = app.Name,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 12, 0, 0),
        });
        var d = UiKit.Muted(app.Desc);
        d.FontSize = 11;
        d.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(d);
        app.Badge = UiKit.Badge("…", "CrFaint");
        app.Badge.HorizontalAlignment = HorizontalAlignment.Left;
        app.Badge.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(app.Badge);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        btns.Children.Add(UiKit.Btn("Configure", null));
        var test = new Button
        {
            Content = "Test Connection",
            Style = (Style)Application.Current.FindResource("CrButton"),
            Foreground = UiKit.Res("CrAccent"),
            BorderBrush = UiKit.Res("CrAccent"),
            Background = UiKit.Res("CrBg"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        test.Click += (s, e) =>
        {
            app.Connected = CheckConnected(app);
            UpdateBadge(app);
            UpdateSummary();
            SelectApp(app);
        };
        btns.Children.Add(test);
        stack.Children.Add(btns);

        var card = new Border
        {
            Style = (Style)Application.Current.FindResource("CrCardStyle"),
            Child = stack,
            Margin = new Thickness(0, 6, 12, 6),
            Cursor = Cursors.Hand,
            Tag = app,
        };
        card.MouseLeftButtonDown += (s, e) => SelectApp(app);
        return card;
    }

    private void RefreshStatuses()
    {
        foreach (var app in _apps)
        {
            app.Connected = CheckConnected(app);
            UpdateBadge(app);
        }
        UpdateSummary();
    }

    private static bool CheckConnected(AppInfo app)
    {
        try
        {
            if (app.Name == "GitHub")
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(';'))
                {
                    if (dir.Length == 0) continue;
                    if (File.Exists(Path.Combine(dir.Trim(), "gh.exe"))) return true;
                }
                return false;
            }
            return Process.GetProcessesByName(app.ProcessName).Length > 0;
        }
        catch { return false; }
    }

    private void UpdateBadge(AppInfo app)
    {
        var parent = app.Badge.Parent as StackPanel;
        if (parent == null) return;
        int idx = parent.Children.IndexOf(app.Badge);
        var nb = UiKit.Badge(app.Connected ? "Terhubung" : "Belum", app.Connected ? "CrGreen" : "CrFaint");
        nb.HorizontalAlignment = HorizontalAlignment.Left;
        nb.Margin = new Thickness(0, 10, 0, 0);
        parent.Children.RemoveAt(idx);
        parent.Children.Insert(idx, nb);
        app.Badge = nb;
    }

    private void UpdateSummary()
    {
        int n = 0;
        foreach (var a in _apps) if (a.Connected) n++;
        _summary.Text = _apps.Count + " aplikasi • " + n + " terhubung";
    }

    private void SelectApp(AppInfo app)
    {
        _selected = app;
        _detTitle.Text = "INTEGRATION DETAILS — " + app.Name.ToUpperInvariant();
        _openBtn.Content = "Buka " + app.Name;
        if (app.Name == "GitHub")
        {
            _detLine1.Text = app.Connected
                ? "GitHub CLI (gh) ditemukan di PATH — repo, Actions & rilis bisa dijalankan dari PC ini."
                : "GitHub CLI (gh) tidak ditemukan di PATH.";
            _detLine2.Text = "Jalur: gh CLI + git di PC pengguna • tombol Buka membuka github.com di browser.";
        }
        else
        {
            _detLine1.Text = app.Connected
                ? app.Name + " sedang berjalan di PC ini (proses " + app.ProcessName + " terdeteksi)."
                : app.Name + " tidak terdeteksi berjalan saat ini.";
            _detLine2.Text = "Jalur: MuseLink bridge (mouse, keyboard, clipboard) • latensi aksi ±0,5 dtk via relay Tailscale.";
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var app = _selected;
        if (app == null) return;
        try
        {
            if (app.Name == "GitHub")
            {
                Process.Start(new ProcessStartInfo("https://github.com") { UseShellExecute = true });
                return;
            }
            string exe = FindExe(app);
            if (exe != null)
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                _detLine1.Text = app.Name + " dibuka.";
            }
            else
            {
                _detLine1.Text = "Jalur " + app.Name + " tidak ditemukan di PC ini.";
            }
        }
        catch (Exception ex)
        {
            _detLine1.Text = "Gagal membuka " + app.Name + ": " + ex.Message;
        }
    }

    private static string FindExe(AppInfo app)
    {
        // Jalur lazim per aplikasi; null bila tidak ketemu.
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidates = new List<string>();
            switch (app.Name)
            {
                case "Chrome":
                    candidates.Add(Path.Combine(pf, "Google\\Chrome\\Application\\chrome.exe"));
                    candidates.Add(Path.Combine(local, "Google\\Chrome\\Application\\chrome.exe"));
                    break;
                case "OBS Studio":
                    candidates.Add(Path.Combine(pf, "obs-studio\\bin\\64bit\\obs64.exe"));
                    break;
                case "Figma":
                    candidates.Add(Path.Combine(local, "Figma\\Figma.exe"));
                    break;
                case "Spotify":
                    candidates.Add(Path.Combine(local, "Microsoft\\WindowsApps\\Spotify.exe"));
                    break;
                case "WhatsApp":
                    candidates.Add(Path.Combine(local, "Microsoft\\WindowsApps\\WhatsApp.exe"));
                    break;
            }
            foreach (var c in candidates) if (File.Exists(c)) return c;
            // Terakhir: andalkan pencarian App Paths Windows.
            return app.ExeHint;
        }
        catch { return null; }
    }
}
