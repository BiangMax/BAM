using System.Security.Principal;

namespace BAM.Desktop;

// Only the UI thread acquires/releases the mutex. Kernel objects disappear after a crash.
internal sealed class SingleInstance : IDisposable
{
    private readonly string _signalName;
    private static readonly NamedWaitHandleOptions SignalScope = new() { CurrentUserOnly = true, CurrentSessionOnly = true };
    private readonly EventWaitHandle? _activation;
    private readonly Mutex _mutex;
    private RegisteredWaitHandle? _registration;

    internal SingleInstance()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("无法确认当前 Windows 用户。");
        _signalName = $"Biang.BAM.{user.Value}.Activate";
        _mutex = new Mutex($"Biang.BAM.{user.Value}.Primary",
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        if (IsPrimary)
        {
            try { _activation = new EventWaitHandle(false, EventResetMode.AutoReset, _signalName, SignalScope); }
            catch
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                throw;
            }
        }
    }

    internal bool IsPrimary { get; }

    internal bool RequestActivation()
    {
        // Only the primary creates this event. Allow for concurrent cold starts.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting(_signalName, SignalScope, out var signal))
            {
                using (signal) return signal.Set();
            }
            Thread.Sleep(25);
        }
        return false;
    }

    internal void Listen(Action activate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(_activation!,
            (_, _) => activate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activation?.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
