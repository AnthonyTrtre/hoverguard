using System;
using System.Runtime.InteropServices;

namespace Hoverguard;

/// <summary>Appels Win32 utilisés par Hoverguard. Rien d'autre ne touche directement au système.</summary>
internal static class Natif
{
    // ── Crochets bas niveau ─────────────────────────────────────────────────────────────────────────────────────────
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204,
        WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A,
        WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_MOUSEHWHEEL = 0x20E;
    public const uint LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80, LLMHF_INJECTED = 0x01;
    public const int WM_QUIT = 0x12, WM_APP = 0x8000;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ── Entrées injectées ───────────────────────────────────────────────────────────────────────────────────────────
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x2, MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static INPUT Touche(ushort vk, bool relachee, UIntPtr marque = default) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = relachee ? KEYEVENTF_KEYUP : 0, dwExtraInfo = marque } },
    };

    public static INPUT Souris(int dx, int dy, uint drapeaux, UIntPtr marque = default) => new()
    {
        type = INPUT_MOUSE,
        u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = drapeaux, dwExtraInfo = marque } },
    };

    public static uint Envoyer(params INPUT[] entrees) => SendInput((uint)entrees.Length, entrees, Marshal.SizeOf<INPUT>());

    // ── Fenêtres ────────────────────────────────────────────────────────────────────────────────────────────────────
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_LAYERED = 0x80000,
        WS_EX_NOACTIVATE = 0x8000000, WS_EX_APPWINDOW = 0x40000;
    public const uint LWA_ALPHA = 0x2, LWA_COLORKEY = 0x1;
    public const uint WDA_NONE = 0, WDA_MONITOR = 1, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint crKey, out byte bAlpha, out uint dwFlags);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public static long StyleEtendu(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
    public static void AjouterStyleEtendu(IntPtr hwnd, long style) => SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(StyleEtendu(hwnd) | style));
    public static void RetirerStyleEtendu(IntPtr hwnd, long style) => SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(StyleEtendu(hwnd) & ~style));

    /// <summary>Met une fenêtre au premier plan malgré les restrictions de Windows sur le vol du focus : on s'attache
    /// le temps d'un instant à la file d'entrée de la fenêtre active.</summary>
    public static void Activer(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        var devant = GetForegroundWindow();
        uint fil = GetCurrentThreadId(), filDevant = devant == IntPtr.Zero ? 0 : GetWindowThreadProcessId(devant, out _);
        bool attache = filDevant != 0 && filDevant != fil && AttachThreadInput(fil, filDevant, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attache) AttachThreadInput(fil, filDevant, false);
        }
    }

    // ── Alimentation et batterie ────────────────────────────────────────────────────────────────────────────────────
    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint esFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    // ── Mot de passe ────────────────────────────────────────────────────────────────────────────────────────────────
    public const int LOGON32_LOGON_INTERACTIVE = 2, LOGON32_PROVIDER_DEFAULT = 0;
    public const int ERROR_LOGON_FAILURE = 1326;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool LogonUser(string lpszUsername, string? lpszDomain, IntPtr lpszPassword, int dwLogonType,
        int dwLogonProvider, out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetUserNameEx(int nameFormat, System.Text.StringBuilder lpNameBuffer, ref uint lpnSize);
    public const int NameUserPrincipal = 8, NameDisplay = 3;
}
