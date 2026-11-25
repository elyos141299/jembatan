using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Security.Principal;
using Microsoft.Win32;

namespace Jembatan;

/// <summary>
/// Pengaturan Jembatan yang disimpan per-user di
/// HKCU\Software\Jembatan, plus pembantu soal hak administrator.
///
/// Mode Administrator (v2.3): bila AKTIF, Jembatan berjalan dengan
/// hak administrator — saat dibuka manual ia menaikkan haknya sendiri
/// (satu prompt UAC), dan jalan otomatisnya memakai Tugas Terjadwal
/// "MuseLink" berhak tertinggi sehingga saat login Windows ia
/// langsung admin TANPA prompt. Bila nilai registry belum ada,
/// bawaannya AKTIF — memang itu yang diperintahkan user secara
/// eksplisit.
///
/// UserStopped (v2.3.2): bendera berhenti-sengaja untuk penjaga
/// anti-crash (watchdog). Dipasang HANYA saat user keluar lewat menu
/// tray → Keluar; penjaga membaca bendera ini dan tidak menyalakan
/// ulang aplikasi. Setiap jalan yang sehat membersihkannya.
/// </summary>
internal static class Settings
{
    private const string KeyPath = @"Software\Jembatan";
    private const string AdminModeValueName = "AdminMode";
    private const string UserStoppedValueName = "UserStopped";

    /// <summary>Mode Administrator menurut registry; belum ada nilai = AKTIF.</summary>
    public static bool AdminMode
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key?.GetValue(AdminModeValueName) is int v)
                        return v != 0;
                }
            }
            catch { /* abaikan — pakai bawaan */ }
            return true;
        }
        set
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, true)
                              ?? Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key?.SetValue(AdminModeValueName, value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }

    private const string OnUiTimeoutMsValueName = "OnUiTimeoutMs";

    /// <summary>
    /// Batas tunggu operasi UI (v3.7.0 — BUG-10): berapa lama
    /// server menunggu thread UI sebelum menjawab 504 (operasi
    /// TIDAK dibatalkan — hasil akhirnya tetap tercatat).
    /// Bawaan 10.000 ms; sah 250–120.000; diatur lewat registry
    /// HKCU\Software\Jembatan\OnUiTimeoutMs (DWORD, milidetik).
    /// </summary>
    public static int OnUiTimeoutMs
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key?.GetValue(OnUiTimeoutMsValueName) is int v && v >= 250 && v <= 120000)
                        return v;
                }
            }
            catch { /* abaikan — pakai bawaan */ }
            return 10000;
        }
    }

    private const string ThemeValueName = "Theme";
    private const string KeepAwakeValueName = "KeepAwake";
    private const string LogPanelOpenValueName = "LogPanelOpen";
    private const string LogPanelHeightValueName = "LogPanelHeight";
    private const string PanelOpenValueName = "PanelOpen";

    /// <summary>
    /// Pilihan tema tampilan (v2.5.0): "device" (bawaan, mengikuti
    /// Windows), "light", atau "dark". Disimpan sebagai teks.
    /// </summary>
    public static string Theme
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key?.GetValue(ThemeValueName) is string v &&
                        (v == "light" || v == "dark" || v == "device"))
                        return v;
                }
            }
            catch { /* anggap bawaan */ }
            return "device";
        }
        set
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key?.SetValue(ThemeValueName, value, RegistryValueKind.String);
            }
        }
    }

    /// <summary>
    /// Saklar "Jangan tidur" (v2.5.0): true = PC dicegah tidur
    /// selama MuseLink jalan (SetThreadExecutionState di
    /// NativeInput). Bawaan mati.
    /// </summary>
    public static bool KeepAwake
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key?.GetValue(KeepAwakeValueName) is int v)
                        return v != 0;
                }
            }
            catch { /* anggap mati */ }
            return false;
        }
        set
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key?.SetValue(KeepAwakeValueName, value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }

    /// <summary>Panel log bawah terbuka? (v2.8.0) Bawaan terbuka.</summary>
    public static bool LogPanelOpen
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key?.GetValue(LogPanelOpenValueName) is int v)
                        return v != 0;
                }
            }
            catch { /* anggap terbuka */ }
            return true;
        }
        set
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key?.SetValue(LogPanelOpenValueName, value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }

    private const string ChatZoomValueName = "ChatZoomPercent";

    /// <summary>Zoom panel chat WebView2 dalam persen (v3.1.0). Bawaan 100, rentang 50–200.</summary>
    public static int ChatZoomPercent
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key?.GetValue(ChatZoomValueName) is int v)
                        return Math.Min(200, Math.Max(50, v));
                }
            }
            catch { /* anggap bawaan */ }
            return 100;
        }
        set
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key?.SetValue(ChatZoomValueName,
                    Math.Min(200, Math.Max(50, value)), RegistryValueKind.DWord);
            }
        }
    }

    /// <summary>Panel kendali di samping rel terbuka? (v3.0.0) Bawaan terbuka.</summary>
    public static bool PanelOpen
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key?.GetValue(PanelOpenValueName) is int v)
                        return v != 0;
                }
            }
            catch { /* anggap terbuka */ }
            return true;
        }
        set
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key?.SetValue(PanelOpenValueName, value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }

    /// <summary>Tinggi panel log bawah tersimpan (v2.8.0). Bawaan 176 (blueprint v3.0).</summary>
    public static int LogPanelHeight
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key?.GetValue(LogPanelHeightValueName) is int v && v >= 110 && v <= 600)
                        return v;
                }
            }
            catch { /* bawaan */ }
            return 176;
        }
        set
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key?.SetValue(LogPanelHeightValueName, value, RegistryValueKind.DWord);
            }
        }
    }

    /// <summary>True bila nilai AdminMode memang sudah pernah ditulis.</summary>
    public static bool HasAdminModeValue
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                    return key?.GetValue(AdminModeValueName) != null;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Bendera berhenti-sengaja (DWORD "UserStopped", v2.3.2). True
    /// HANYA bila user keluar lewat menu tray → Keluar; tugas penjaga
    /// "MuseLink Watchdog" memeriksanya sebelum menyalakan ulang
    /// aplikasi. Penutupan oleh Windows shutdown / Task Manager tidak
    /// memasang bendera ini, jadi penjaga tetap menyalakan ulang.
    /// Aplikasi membersihkannya (0) setiap berhasil dimulai.
    /// </summary>
    public static bool UserStopped
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key?.GetValue(UserStoppedValueName) is int v)
                        return v != 0;
                }
            }
            catch { /* abaikan — pakai bawaan */ }
            return false;
        }
        set
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, true)
                              ?? Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key?.SetValue(UserStoppedValueName, value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }

    private const string WindowBoundsValueName = "WindowBounds";
    private const string WindowMaximizedValueName = "WindowMaximized";

    /// <summary>
    /// Ukuran & posisi jendela MuseLink terakhir (v2.4.2), disimpan
    /// sebagai teks "X,Y,Lebar,Tinggi" (DWORD terpisah untuk keadaan
    /// dimaksimalkan). Null bila belum pernah disimpan / rusak —
    /// pemanggil memakai ukuran bawaan.
    /// </summary>
    public static Rectangle? WindowBounds
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key?.GetValue(WindowBoundsValueName) is string v)
                    {
                        var parts = v.Split(',');
                        if (parts.Length == 4
                            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                            && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                            && w >= 400 && h >= 300)
                            return new Rectangle(x, y, w, h);
                    }
                }
            }
            catch { /* abaikan — pakai bawaan */ }
            return null;
        }
        set
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, true)
                              ?? Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (value.HasValue)
                {
                    var r = value.Value;
                    key?.SetValue(WindowBoundsValueName,
                        string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", r.X, r.Y, r.Width, r.Height),
                        RegistryValueKind.String);
                }
            }
        }
    }

    /// <summary>True bila jendela terakhir ditinggalkan dalam keadaan dimaksimalkan.</summary>
    public static bool WindowMaximized
    {
        get
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key?.GetValue(WindowMaximizedValueName) is int v)
                        return v != 0;
                }
            }
            catch { /* abaikan */ }
            return false;
        }
        set
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, true)
                              ?? Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key?.SetValue(WindowMaximizedValueName, value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }

    /// <summary>True bila PROSES INI sedang berjalan dengan hak administrator.</summary>
    public static bool IsElevated()
    {
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// Jalankan ulang exe yang sama dengan Verb "runas" (memunculkan
    /// prompt UAC satu kali). True bila proses baru berhasil dimulai;
    /// false bila gagal — termasuk user menekan "No" di prompt UAC
    /// (Win32Exception ERROR_CANCELLED). Pemanggil memutuskan sendiri
    /// langkah berikutnya; fungsi ini tidak pernah mengulang.
    /// </summary>
    public static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Jembatan.exe"),
                Arguments = JoinArgs(args),
                UseShellExecute = true,
                Verb = "runas",
            };
            return Process.Start(psi) != null;
        }
        catch (Win32Exception)
        {
            return false; // user menolak UAC dsb.
        }
        catch
        {
            return false;
        }
    }

    private static string JoinArgs(string[] args)
    {
        if (args == null || args.Length == 0) return "";
        var parts = new List<string>();
        foreach (var a in args)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                parts.Add(a);
            else
                parts.Add("\"" + a.Replace("\"", "\\\"") + "\"");
        }
        return string.Join(" ", parts);
    }
}
