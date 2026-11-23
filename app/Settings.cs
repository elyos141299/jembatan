using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Jembatan;

/// <summary>
/// Pengaturan Jembatan yang disimpan per-user di
/// HKCU\Software\Jembatan, plus pembantu soal hak administrator.
///
/// Mode Administrator (v2.3): bila AKTIF, Jembatan berjalan dengan
/// hak administrator — saat dibuka manual ia menaikkan haknya sendiri
/// (satu prompt UAC), dan jalan otomatisnya memakai Tugas Terjadwal
/// "Muse Windows" berhak tertinggi sehingga saat login Windows ia
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
    /// "Muse Windows Watchdog" memeriksanya sebelum menyalakan ulang
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
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
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
