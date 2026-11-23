using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Jembatan;

/// <summary>
/// Kendali mouse & keyboard lewat user32 (SetCursorPos + mouse_event)
/// dan SendKeys. Semua fungsi di sini HARUS dipanggil dari thread UI
/// (AgentServer mengurus itu) karena SendKeys butuh message loop.
/// </summary>
public static class NativeInput
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint Wheel = 0x0800;

    private static void ButtonFlags(string button, out uint down, out uint up)
    {
        switch ((button ?? "left").Trim().ToLowerInvariant())
        {
            case "right":
                down = RightDown; up = RightUp; break;
            case "middle":
                down = MiddleDown; up = MiddleUp; break;
            default:
                down = LeftDown; up = LeftUp; break;
        }
    }

    // ---------- Daftar & fokus jendela (v2.1) ----------

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SwRestore = 9;

    /// <summary>Satu jendela tingkat atas yang terlihat dan berjudul.</summary>
    public sealed class WindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public string Process;
    }

    /// <summary>Daftar semua jendela tingkat atas yang terlihat & berjudul non-kosong.</summary>
    public static List<WindowInfo> ListWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;
            string proc = "";
            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                proc = Process.GetProcessById((int)pid).ProcessName;
            }
            catch { /* proses sudah mati dsb. — nama proses dikosongkan */ }
            result.Add(new WindowInfo { Handle = hWnd, Title = title, Process = proc });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Bawa jendela ke depan. Jendela yang diminimalkan dipulihkan dulu
    /// (SW_RESTORE) sebelum SetForegroundWindow.
    /// </summary>
    public static bool FocusWindow(IntPtr hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
        return SetForegroundWindow(hWnd);
    }

    public static void Move(int x, int y)
    {
        SetCursorPos(x, y);
    }

    public static void Click(int x, int y, string button)
    {
        SetCursorPos(x, y);
        ButtonFlags(button, out uint down, out uint up);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(45);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>
    /// Scroll roda mouse di titik (x, y). Delta positif = ke atas,
    /// negatif = ke bawah (kelipatan 120 = satu "klik" roda, standar Windows).
    /// </summary>
    public static void Scroll(int x, int y, int delta)
    {
        SetCursorPos(x, y);
        // dwData untuk wheel bertanda (signed) — kirim sebagai uint.
        mouse_event(Wheel, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }

    /// <summary>
    /// Seret (drag): tekan tombol di titik awal, gerakkan bertahap
    /// ke titik akhir, lalu lepas. Gerakan dibuat bertahap agar
    /// aplikasi tujuan menerima event drag dengan wajar.
    /// </summary>
    public static void Drag(int x1, int y1, int x2, int y2, string button)
    {
        ButtonFlags(button, out uint down, out uint up);
        SetCursorPos(x1, y1);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        const int steps = 16;
        for (int i = 1; i <= steps; i++)
        {
            int x = x1 + (x2 - x1) * i / steps;
            int y = y1 + (y2 - y1) * i / steps;
            SetCursorPos(x, y);
            Thread.Sleep(15);
        }
        Thread.Sleep(60);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Mengetik teks biasa; karakter khusus SendKeys di-escape otomatis.</summary>
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SendKeys.SendWait(Escape(text));
    }

    /// <summary>
    /// Menekan satu tombol khusus ("ENTER", "TAB", "ESC", "F5", ...)
    /// atau kombinasi dalam sintaks SendKeys ("^c" = Ctrl+C, "%{F4}" = Alt+F4).
    /// </summary>
    public static void PressKey(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys)) return;
        string k = keys.Trim();
        if (SpecialKeys.TryGetValue(k, out string token))
            SendKeys.SendWait(token);
        else
            SendKeys.SendWait(k);
    }

    // Nama tombol -> token SendKeys yang harus dikirim.
    private static readonly Dictionary<string, string> SpecialKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ENTER", "{ENTER}" },
            { "TAB", "{TAB}" },
            { "ESC", "{ESC}" },
            { "ESCAPE", "{ESC}" },
            { "BACKSPACE", "{BACKSPACE}" },
            { "BKSP", "{BACKSPACE}" },
            { "DELETE", "{DELETE}" },
            { "DEL", "{DELETE}" },
            { "INSERT", "{INSERT}" },
            { "INS", "{INSERT}" },
            { "UP", "{UP}" },
            { "DOWN", "{DOWN}" },
            { "LEFT", "{LEFT}" },
            { "RIGHT", "{RIGHT}" },
            { "HOME", "{HOME}" },
            { "END", "{END}" },
            { "PGUP", "{PGUP}" },
            { "PAGEUP", "{PGUP}" },
            { "PGDN", "{PGDN}" },
            { "PAGEDOWN", "{PGDN}" },
            { "SPACE", " " },
            { "CAPSLOCK", "{CAPSLOCK}" },
            { "NUMLOCK", "{NUMLOCK}" },
            { "SCROLLLOCK", "{SCROLLLOCK}" },
            { "PRTSC", "{PRTSC}" },
            { "F1", "{F1}" }, { "F2", "{F2}" }, { "F3", "{F3}" }, { "F4", "{F4}" },
            { "F5", "{F5}" }, { "F6", "{F6}" }, { "F7", "{F7}" }, { "F8", "{F8}" },
            { "F9", "{F9}" }, { "F10", "{F10}" }, { "F11", "{F11}" }, { "F12", "{F12}" },
        };

    // Karakter yang punya arti khusus di SendKeys dibungkus kurung kurawal,
    // mis. "+" menjadi "{+}". Enter/tab menjadi tombol sungguhan.
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r':
                    break; // pasangan \r\n cukup satu {ENTER} dari \n
                case '\n':
                    sb.Append("{ENTER}");
                    break;
                case '\t':
                    sb.Append("{TAB}");
                    break;
                case '+':
                case '^':
                case '%':
                case '~':
                case '(':
                case ')':
                case '{':
                case '}':
                case '[':
                case ']':
                    sb.Append('{').Append(c).Append('}');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
