using System;
using System.Runtime.InteropServices;
using static IslandDock.Native;

namespace IslandDock;

/// <summary>
/// Takes Alt+Tab and the Windows key (pressed on its own) for the island. The callbacks run on the hook's own thread,
/// inside Windows' handling of each key press, so they only decide and queue the real work on the UI thread.
/// </summary>
internal static class KeyboardHook
{
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    // The mouse member is only there so the union is its real size; SendInput refuses a wrong one.
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hmod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    const uint LLKHF_EXTENDED = 0x1, LLKHF_INJECTED = 0x10, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, INPUT_KEYBOARD = 1;
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    const int VK_TAB = 0x09, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
        VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    /// <summary>A key no keyboard has. Pressed just before Windows or Alt is let go, it makes that a key combination,
    /// so Start doesn't open and the app in front doesn't jump to its menu bar.</summary>
    const ushort VK_MASK = 0xE8;

    static HookProc? proc;
    static IntPtr hook;
    static bool winDown, winUsed, altTabbing;
    static ushort winVk;

    /// <summary>Alt+Tab, or Alt+Shift+Tab (true). Returns false to leave it to Windows.</summary>
    public static Func<bool, bool>? AltTab;
    /// <summary>The Windows key pressed and let go on its own. Returns false to leave it to Windows (Start).</summary>
    public static Func<bool>? WinKey;

    static long lastKeyAt;
    /// <summary>When a key was last pressed, for telling typing from a window merely having the keyboard. Only ever when, not which.</summary>
    public static long LastKeyAt => System.Threading.Volatile.Read(ref lastKeyAt);

    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    const uint WM_QUIT = 0x12;
    static uint hookThread;

    /// <summary>
    /// The hook gets a thread of its own. Windows only waits a moment for a hook before passing the key on anyway, so
    /// on the UI thread, busy drawing the island, Start and Windows' Alt+Tab still came up alongside the island.
    /// </summary>
    public static void Start()
    {
        var thread = new System.Threading.Thread(() =>
        {
            hookThread = GetCurrentThreadId();
            proc = Proc;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } // the hook is called from in here
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "Keyboard hook", Priority = System.Threading.ThreadPriority.Highest };
        thread.Start();
    }

    public static void Stop()
    {
        if (hookThread != 0) PostThreadMessage(hookThread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        hookThread = 0;
    }

    static IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool down = (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
            if (down) System.Threading.Volatile.Write(ref lastKeyAt, Environment.TickCount64);
            // Keys this sends itself go straight through.
            try
            {
                if ((k.flags & LLKHF_INJECTED) == 0 && Handle((int)k.vkCode, (ushort)k.scanCode, (k.flags & LLKHF_EXTENDED) != 0, down))
                    return 1;
            }
            catch { }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>True to swallow the key.</summary>
    static bool Handle(int vk, ushort scan, bool extended, bool down)
    {
        // Windows never sees the Windows key on its own: it's held back until another key goes with it (Win+E,
        // Win+Shift+S, Win+arrows), then passed on ahead of that key. Let go alone, it's the island's, or Start's
        // if the island doesn't want it. Letting it through and hiding the let-go didn't stop Start on Windows 11.
        if (vk is VK_LWIN or VK_RWIN)
        {
            if (down)
            {
                if (!winDown) { winDown = true; winUsed = false; winVk = (ushort)vk; }
                return !winUsed; // held down repeats; once passed on, they go through too
            }
            if (!winDown || vk != winVk) return false;
            winDown = false;
            if (winUsed) return false; // Windows had the key-down, so it has the let-go too
            if (WinKey?.Invoke() == true) return true;
            Send(new[] { Key(winVk, 0, false, false), Key(winVk, 0, false, true) }); // Start, as normal
            return true;
        }
        if (down && winDown && !winUsed)
        {
            winUsed = true;
            // Win+L locks the PC only when it's really pressed, so do it here.
            if (vk == 'L') { LockWorkStation(); return true; }
            Send(new[] { Key(winVk, 0, false, false), Key((ushort)vk, scan, extended, false) });
            return true;
        }

        if (vk == VK_TAB)
        {
            if (!down) return altTabbing;
            if (!altTabbing && (!Held(VK_MENU) || Held(VK_CONTROL) || winDown)) return false;
            if (AltTab?.Invoke(Held(VK_SHIFT)) != true) return altTabbing;
            altTabbing = true;
            return true;
        }
        if (vk is VK_MENU or VK_LMENU or VK_RMENU && !down && altTabbing)
        {
            altTabbing = false;
            Send(VK_MASK, false, VK_MASK, true, (ushort)vk, true);
            return true;
        }
        return false;
    }

    static void Send(ushort vk1, bool up1, ushort vk2, bool up2, ushort vk3, bool up3) =>
        Send(new[] { Key(vk1, 0, false, up1), Key(vk2, 0, false, up2), Key(vk3, 0, false, up3) });

    static INPUT Key(ushort vk, ushort scan, bool extended, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT
        {
            wVk = vk, wScan = scan,
            dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0)
        } }
    };

    /// <summary>Presses and lets go of a key; the hook lets it through, as it does everything it sends.</summary>
    public static void Tap(ushort vk) => Send(new[] { Key(vk, 0, false, false), Key(vk, 0, false, true) });

    static void Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
