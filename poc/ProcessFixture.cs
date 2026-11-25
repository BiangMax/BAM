// Compile with the inbox .NET Framework compiler, no SDK or NuGet required.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class ProcessFixture
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int capacity);
    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, string device, IntPtr mode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string windowClass, string title,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    [MTAThread]
    private static int Main(string[] args)
    {
        SetErrorMode(0x0001 | 0x0002); // Process-local; suppress OS crash dialogs.
        try { return Run(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine("Fixture failed: " + error.GetType().Name + " " + error.Message);
            return 1;
        }
    }
    private static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] == "window-test") return TestWindow();
        if (args.Length == 1 && args[0] == "worker")
        {
            using (var form = new Form())
            using (var timeout = new System.Windows.Forms.Timer())
            {
                form.Text = "BAM Phase 0 fixture";
                form.FormClosed += delegate { Application.ExitThread(); };
                timeout.Interval = 15000;
                timeout.Tick += delegate { Application.ExitThread(); };
                timeout.Start();
                Console.WriteLine(form.Handle.ToInt64());
                Console.Out.Flush();
                Application.Run(); // Dedicated hidden top-level window.
            }
            return 0;
        }
        if (args.Length == 1 && args[0] == "unexpected") return 23;
        if (args.Length != 1) return 2;
        string own = Application.ExecutablePath;
        using (Process primary = Start(own, "worker"))
        using (Process other = Start(args[0], "worker"))
        {
            try
            {
                IntPtr window = ReadWindow(primary);
                ReadWindow(other);
                Check(primary.ProcessName == other.ProcessName && primary.Id != other.Id &&
                    primary.MainModule.FileName != other.MainModule.FileName, "same name, distinct installation and PID");
                int matches = 0;
                foreach (var discovered in Process.GetProcessesByName(primary.ProcessName))
                {
                    using (discovered)
                    {
                        if (discovered.Id == primary.Id && discovered.StartTime == primary.StartTime &&
                            discovered.MainModule.FileName == own) matches++;
                    }
                }
                Check(matches == 1, "reconcile external instance");
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                var title = new System.Text.StringBuilder(80);
                GetWindowText(window, title, title.Capacity);
                Check(owner == primary.Id && title.ToString() == "BAM Phase 0 fixture" && !IsHungAppWindow(window), "HWND, title, response");
                var watch = Stopwatch.StartNew();
                TimeSpan before = primary.TotalProcessorTime;
                Thread.Sleep(1000);
                primary.Refresh();
                double cpu = 100 * (primary.TotalProcessorTime - before).TotalSeconds / watch.Elapsed.TotalSeconds / Environment.ProcessorCount;
                Check(cpu >= 0 && cpu <= 100 && primary.WorkingSet64 > 0 && primary.PrivateMemorySize64 > 0 && primary.StartTime < DateTime.Now, "CPU, memory, uptime");
                Check(PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero) && primary.WaitForExit(2500) && primary.ExitCode == 0, "WM_CLOSE, confirmed exit");
                Check(!other.HasExited, "other installation unaffected");
                using (var restarted = Start(own, "worker"))
                {
                    try
                    {
                        ReadWindow(restarted);
                        Check(restarted.StartTime > primary.StartTime, "restart, new identity");
                        restarted.Kill();
                        Check(restarted.WaitForExit(2500), "explicit force on owned fixture");
                    }
                    finally { EndFixture(restarted); }
                }
                using (var unexpected = Start(own, "unexpected"))
                    Check(unexpected.WaitForExit(2500) && unexpected.ExitCode == 23, "unrequested exit, known nonzero code");
            }
            finally { EndFixture(primary); EndFixture(other); }
        }
        return 0;
    }
    private static int TestWindow()
    {
        // No desktop switch on the user's screen. Plain Win32 avoids WinForms/OLE initialization.
        IntPtr original = GetThreadDesktop(GetCurrentThreadId());
        IntPtr desktop = CreateDesktop("BamFixture" + Guid.NewGuid().ToString("N"), null,
            IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
        if (desktop == IntPtr.Zero) return NativeFailure("CreateDesktop");
        try
        {
            if (!SetThreadDesktop(desktop)) return NativeFailure("SetThreadDesktop");
            IntPtr window = CreateWindowEx(0, "STATIC", "Fixture", 0x00cf0000, 0, 0, 100, 100,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (window == IntPtr.Zero) return NativeFailure("CreateWindowEx");
            try
            {
                ShowWindow(window, 7);
                Check(IsIconic(window), "minimized private fixture window");
                ShowWindow(window, 9);
                Check(!IsIconic(window), "restored private fixture window");
                bool requested = SetForegroundWindow(window);
                Console.WriteLine("Private desktop foreground request={0}, observed={1}",
                    requested, GetForegroundWindow() == window);
            }
            finally { DestroyWindow(window); }
        }
        finally { SetThreadDesktop(original); CloseDesktop(desktop); }
        return 0;
    }
    private static int NativeFailure(string operation)
    {
        int error = Marshal.GetLastWin32Error();
        Console.Error.WriteLine("{0} failed, Win32 error {1}", operation, error);
        return 3;
    }
    private static Process Start(string file, string argument)
    {
        return Process.Start(new ProcessStartInfo(file, argument) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
    }
    private static IntPtr ReadWindow(Process process)
    {
        var line = process.StandardOutput.ReadLineAsync();
        if (!line.Wait(5000)) throw new TimeoutException("Fixture did not create a window");
        return new IntPtr(long.Parse(line.Result));
    }
    private static void EndFixture(Process process)
    {
        if (!process.HasExited) { process.Kill(); process.WaitForExit(2500); }
    }
    private static void Check(bool ok, string test)
    {
        if (!ok) throw new InvalidOperationException(test);
        Console.WriteLine("PASS " + test);
    }
}
