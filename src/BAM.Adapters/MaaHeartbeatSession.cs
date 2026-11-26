using System.Security.Cryptography;
using System.Text;
using BAM.Core;

namespace BAM.Adapters;

public sealed class MaaHeartbeatSession : IMaaHeartbeatSession
{
    private const int ReportHistorySize = 16;
    private static readonly TimeSpan ProbeLifetime = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly string _user;
    private readonly string _device;
    private readonly TimeProvider _clock;
    private readonly Queue<ReportEntry> _reports = new(ReportHistorySize);
    private DateTimeOffset? _lastPoll;
    private Guid _probeId;
    private long _queuedAt;
    private RemoteCommandState _state = RemoteCommandState.Unknown;
    private bool? _reportSucceeded;
    private bool? _reportContainsRemoteTask;

    public MaaHeartbeatSession(string user, string device, TimeProvider clock)
    {
        if (!IsValidIdentifier(user) || !IsValidIdentifier(device))
            throw new ArgumentException("配对标识须为 1–128 个字符且不含控制字符。");
        (_user, _device, _clock) = (user, device, clock);
    }

    private static bool IsValidIdentifier(string value) =>
        value is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    public bool IsPaired(string user, string device) =>
        string.Equals(user, _user, StringComparison.Ordinal) && string.Equals(device, _device, StringComparison.Ordinal);

    public bool TryQueueHeartbeat()
    {
        lock (_gate)
        {
            Expire();
            if (_state is RemoteCommandState.Queued or RemoteCommandState.Offered) return false;
            _probeId = Guid.NewGuid();
            _queuedAt = _clock.GetTimestamp();
            _state = RemoteCommandState.Queued;
            _reportSucceeded = null;
            _reportContainsRemoteTask = null;
            return true;
        }
    }

    public Guid? Poll()
    {
        lock (_gate)
        {
            Expire();
            _lastPoll = _clock.GetUtcNow();
            if (_state is not (RemoteCommandState.Queued or RemoteCommandState.Offered)) return null;
            _state = RemoteCommandState.Offered;
            return _probeId;
        }
    }

    public MaaHeartbeatReport Report(Guid id, bool succeeded, string payload)
    {
        lock (_gate)
        {
            Expire();
            var previous = _reports.FirstOrDefault(entry => entry.Id == id);
            if (previous is not null)
                return previous.Succeeded == succeeded && previous.Fingerprint.AsSpan().SequenceEqual(Fingerprint(payload))
                    ? MaaHeartbeatReport.Accepted : MaaHeartbeatReport.Conflict;
            if (id == _probeId && _state == RemoteCommandState.TimedOut) return MaaHeartbeatReport.Expired;
            if (id != _probeId || _state != RemoteCommandState.Offered) return MaaHeartbeatReport.Unknown;

            if (_reports.Count == ReportHistorySize) _reports.Dequeue();
            _reports.Enqueue(new(id, succeeded, Fingerprint(payload)));
            _reportSucceeded = succeeded;
            _reportContainsRemoteTask = succeeded ? payload.Length != 0 : null;
            _state = RemoteCommandState.Reported;
            return MaaHeartbeatReport.Accepted;
        }
    }

    public MaaHeartbeatSnapshot Snapshot()
    {
        lock (_gate)
        {
            Expire();
            return new(_lastPoll, _state, _reportSucceeded, _reportContainsRemoteTask);
        }
    }

    private void Expire()
    {
        if (_state is RemoteCommandState.Queued or RemoteCommandState.Offered &&
            _clock.GetElapsedTime(_queuedAt) >= ProbeLifetime)
            _state = RemoteCommandState.TimedOut;
    }

    private static byte[] Fingerprint(string payload) => SHA256.HashData(Encoding.UTF8.GetBytes(payload));
    private sealed record ReportEntry(Guid Id, bool Succeeded, byte[] Fingerprint);
}
