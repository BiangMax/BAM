using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class BamSmokeWindow
{
    [DllImport("user32")] public static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    private delegate bool WindowCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, IntPtr data);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int capacity);
    [DllImport("user32")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint owner);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo information);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rectangle);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo information);

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rectangle, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, WorkArea; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRectangle;
    }

    public static bool IsOwned(IntPtr hwnd, int pid)
        => hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out var owner) != 0 && owner == (uint)pid;

    public static void Resize(IntPtr hwnd, int pid, int width, int height)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            AssertWindow(hwnd, pid);
            var scale = GetDpiForWindow(hwnd) / 96.0;
            if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, (int)Math.Round(width * scale),
                (int)Math.Round(height * scale), 0x16))
                throw new InvalidOperationException("BAM could not be resized.");
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    public static int MonitorCount() => GetMonitors().Count;

    public static bool IsForeground(IntPtr hwnd) => GetForegroundWindow() == hwnd;
    public static IntPtr FindDialog(IntPtr owner, int pid, string title)
    {
        AssertWindow(owner, pid);
        var found = IntPtr.Zero;
        EnumThreadWindows(GetWindowThreadProcessId(owner, out _), (hwnd, _) =>
        {
            if (hwnd == owner || !IsWindowVisible(hwnd)) return true;
            var name = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, name, name.Capacity);
            if (name.ToString() != title) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static void PressKeyInOwnedWindow(IntPtr hwnd, int pid, int virtualKey)
    {
        var info = GetOwnedThreadInfo(hwnd, pid);
        if (info.Focus != hwnd) throw new InvalidOperationException("The owned BAM window does not have keyboard focus.");
        PostKey(hwnd, virtualKey);
    }

    public static bool TryPressForegroundKey(IntPtr hwnd, int pid, int virtualKey)
    {
        var info = GetOwnedThreadInfo(hwnd, pid);
        if (!IsForeground(hwnd) || info.Focus != hwnd) return false;
        PostKey(hwnd, virtualKey);
        return true;
    }

    private static void PostKey(IntPtr hwnd, int virtualKey)
    {
        var key = new IntPtr(virtualKey);
        if (!PostMessage(hwnd, 0x0100, key, new IntPtr(1)) ||
            !PostMessage(hwnd, 0x0101, key, new IntPtr(unchecked((int)0xC0000001))))
            throw new InvalidOperationException("Could not send a key to the owned BAM window.");
    }

    public static void OpenSystemMenu(IntPtr hwnd, int pid)
    {
        AssertWindow(hwnd, pid);
        if (!PostMessage(hwnd, 0x0112, new IntPtr(0xF100), new IntPtr(0x20)))
            throw new InvalidOperationException("Could not request the owned window's System menu.");
    }

    public static bool IsSystemMenuOpen(IntPtr hwnd, int pid)
    {
        var info = GetOwnedThreadInfo(hwnd, pid);
        return info.MenuOwner == hwnd && (info.Flags & 0x1C) != 0;
    }

    public static void DismissSystemMenu(IntPtr hwnd, int pid)
    {
        AssertWindow(hwnd, pid);
        if (!PostMessage(hwnd, 0x001F, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Could not dismiss the owned window's System menu.");
    }

    private static GuiThreadInfo GetOwnedThreadInfo(IntPtr hwnd, int pid)
    {
        var thread = GetWindowThreadProcessId(hwnd, out var owner);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || owner != (uint)pid || !GetGUIThreadInfo(thread, ref info))
            throw new InvalidOperationException("Could not read the owned BAM window's GUI thread.");
        return info;
    }

    public static void MoveToMonitor(IntPtr hwnd, int pid, int monitorIndex)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            AssertWindow(hwnd, pid);
            var monitor = GetMonitors()[monitorIndex];
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(hwnd, out var window))
                throw new InvalidOperationException("Could not read the owned window or monitor geometry.");
            var area = info.WorkArea;
            var width = Math.Min(window.Right - window.Left, area.Right - area.Left);
            var height = Math.Min(window.Bottom - window.Top, area.Bottom - area.Top);
            if (!SetWindowPos(hwnd, IntPtr.Zero, area.Left, area.Top, width, height, 0x14) ||
                MonitorFromWindow(hwnd, 0) != monitor)
                throw new InvalidOperationException("BAM did not move to the requested monitor.");
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private static List<IntPtr> GetMonitors()
    {
        var monitors = new List<IntPtr>();
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) => { monitors.Add(monitor); return true; }, IntPtr.Zero))
            throw new InvalidOperationException("Windows did not enumerate the monitors.");
        return monitors;
    }

    private static void AssertWindow(IntPtr hwnd, int pid)
    {
        if (!IsOwned(hwnd, pid)) throw new InvalidOperationException("The window is no longer owned by the BAM fixture.");
    }
}
