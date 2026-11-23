using System;
using System.Collections.Generic;
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

    public static void Move(int x, int y)
    {
        SetCursorPos(x, y);
    }

    public static void Click(int x, int y, string button)
    {
        SetCursorPos(x, y);
        uint down, up;
        switch ((button ?? "left").Trim().ToLowerInvariant())
        {
            case "right":
                down = RightDown; up = RightUp; break;
            case "middle":
                down = MiddleDown; up = MiddleUp; break;
            default:
                down = LeftDown; up = LeftUp; break;
        }
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(45);
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
