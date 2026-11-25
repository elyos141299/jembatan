using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace Jembatan;

/// <summary>
/// Sensor sistem untuk baris rincian panel (v3.0.1 — permintaan
/// user: RAM, suhu CPU, suhu GPU di kartu rincian).
/// RAM dibaca native (GlobalMemoryStatusEx — selalu tersedia).
/// GPU lewat nvidia-smi milik driver NVIDIA (terbukti di PC user:
/// 37°C saat diam). Suhu CPU tidak punya jalur bawaan Windows di
/// motherboard desktop (WMI ACPI ThermalZone = "Not supported",
/// terverifikasi di PC user), jadi memakai LibreHardwareMonitor
/// yang membaca sensor lewat driver ring-0 — bila Windows
/// memblokir driver itu, Read() mengembalikan null dan baris CPU
/// menampilkan "—" (degradasi anggun, tidak pernah crash).
/// Semua pembacaan boleh gagal dengan aman.
/// </summary>
internal static class SysSensors
{
    // ---------- RAM (native) ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    /// <summary>"4,1/15,8 GB" — pemakaian RAM fisik saat ini.</summary>
    public static string RamText()
    {
        try
        {
            var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (!GlobalMemoryStatusEx(m) || m.ullTotalPhys == 0) return "—";
            double total = m.ullTotalPhys / 1073741824.0;
            double used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            return Fmt(used) + "/" + Fmt(total) + " GB";
        }
        catch { return "—"; }
    }

    private static string Fmt(double v)
        => v.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');

    // ---------- GPU (nvidia-smi) ----------

    private static string FindNvidiaSmi()
    {
        try
        {
            string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (File.Exists(sys)) return sys;
        }
        catch { /* abaikan — jatuh ke pencarian PATH */ }
        return "nvidia-smi";
    }

    /// <summary>
    /// "37°C · 12%" (suhu · beban GPU) atau null bila gagal —
    /// pemanggil mempertahankan nilai terakhir yang bagus.
    /// MENGGANTUNG ±100–300ms: panggil dari utas latar saja.
    /// </summary>
    public static string GpuText()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FindNvidiaSmi(),
                Arguments = "--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) return null;
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(4000))
                {
                    try { p.Kill(); } catch { /* abaikan */ }
                    return null;
                }
                var parts = output.Trim().Split(',');
                if (parts.Length >= 2
                    && int.TryParse(parts[0].Trim(), out int temp)
                    && int.TryParse(parts[1].Trim(), out int util))
                    return temp + "°C · " + util + "%";
            }
        }
        catch { /* nvidia-smi tidak ada / gagal — nilai lama dipertahankan */ }
        return null;
    }
}

/// <summary>
/// Suhu CPU lewat LibreHardwareMonitor. Open() memasang driver
/// sensor (butuh admin — aplikasi sudah berjalan sebagai admin)
/// dan bisa makan waktu beberapa detik: panggil dari utas latar.
/// Bila driver diblokir Windows (daftar blokir driver rentan),
/// Failed = true dan Read() selalu null.
/// </summary>
internal sealed class CpuTempSensor : IDisposable
{
    private Computer _computer;

    /// <summary>True bila inisialisasi sensor dipastikan gagal.</summary>
    public bool Failed { get; private set; }

    public bool TryInit()
    {
        try
        {
            _computer = new Computer { IsCpuEnabled = true };
            _computer.Open();
            return true;
        }
        catch
        {
            Failed = true;
            _computer = null;
            return false;
        }
    }

    /// <summary>Suhu CPU (°C, dibulatkan) atau null bila tak terbaca.</summary>
    public int? Read()
    {
        if (_computer == null) return null;
        try
        {
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType != HardwareType.Cpu) continue;
                hw.Update();
                float? package = null;
                float best = float.MinValue;
                foreach (var sensor in hw.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue)
                        continue;
                    if (sensor.Name == "CPU Package") package = sensor.Value.Value;
                    else if (sensor.Value.Value > best) best = sensor.Value.Value;
                }
                if (package.HasValue) return (int)Math.Round(package.Value);
                if (best != float.MinValue) return (int)Math.Round(best);
            }
        }
        catch { /* sensor macet sesaat — coba lagi di putaran berikut */ }
        return null;
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { /* abaikan */ }
        _computer = null;
    }
}
