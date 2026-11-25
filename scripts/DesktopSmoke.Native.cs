using System;
using System.Runtime.InteropServices;

public static class BamSmokeWindow
{
    [DllImport("user32")] public static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint owner);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

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

    private static void AssertWindow(IntPtr hwnd, int pid)
    {
        if (!IsOwned(hwnd, pid)) throw new InvalidOperationException("The window is no longer owned by the BAM fixture.");
    }
}
