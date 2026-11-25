using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BAM.Core;

namespace BAM.Adapters;

public sealed partial class MaaEndTaskObserver(ITaskLogSource logs, TimeProvider clock)
{
    private const string Source = "MaaFramework";
    private const string Scope = "只读观测单个 MaaFramework 任务。";
    private static readonly TimeSpan RunningFreshness = TimeSpan.FromMinutes(1);
    private readonly Dictionary<Guid, Session> _sessions = [];
    private DateTimeOffset? _lastPoll;

    // Invoked by one desktop monitor loop, outside the process-control gate.
    public async ValueTask<ProgramFrame> ObserveAsync(ProgramFrame frame, CancellationToken cancellation)
    {
        var now = clock.GetUtcNow();
        if (now < _lastPoll) _sessions.Clear();
        _lastPoll = now;
        var programs = frame.Programs.ToArray();
        HashSet<Guid> observed = [];
        for (int index = 0; index < programs.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var row = programs[index];
            if (row.Installation.Kind != ProgramKind.MaaEnd) continue;
            programs[index] = await ObserveProgramAsync(row, cancellation).ConfigureAwait(false);
            observed.Add(row.Installation.Id);
        }
        foreach (var id in _sessions.Keys)
            if (!observed.Contains(id)) _sessions.Remove(id);
        return frame with { Programs = programs };
    }

    private async ValueTask<ProgramStatus> ObserveProgramAsync(ProgramStatus row, CancellationToken cancellation)
    {
        var program = row.Installation;
        var id = program.Id;
        if (row.Metadata is not { WorkerBindingValid: true, ProductVersion: not null, ResourceVersion: not null })
        {
            _sessions.Remove(id);
            return WithState(row, GameTaskState.Unknown, "无法确认 MaaEnd 安装与版本。");
        }
        if (row.Metadata is not { ProductVersion: "2.11.2", ResourceVersion: "v3.3.0-beta.2" })
        {
            _sessions.Remove(id);
            return row with { TaskHint = "当前版本组合没有可用的 MaaEnd 任务观测规则。" };
        }
        if (row is not { Process: ProcessState.Running, Worker.Identity: { } worker })
        {
            _sessions.Remove(id);
            return WithState(row, GameTaskState.Unknown, "等待确认当前 MaaEnd 进程。");
        }
        try
        {
            if (!_sessions.TryGetValue(id, out var session) || !ReferenceEquals(session.Installation, program) ||
                !worker.Equals(session.Worker))
            {
                var path = Path.Combine(Path.GetDirectoryName(worker.ImagePath)!, "debug", "maafw.log");
                session = new(program, worker, logs.Follow(path), clock);
                _sessions[id] = session;
            }

            var batch = await session.Tail.ReadAsync(cancellation).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            if (batch.Discontinuity || now < _lastPoll)
            {
                session.Reset(clock);
                return WithState(row, GameTaskState.Unknown, "从当前日志末尾等待新的任务证据。");
            }

            foreach (var line in batch.Lines)
            {
                cancellation.ThrowIfCancellationRequested();
                var match = EventLine().Match(line);
                if (!match.Success || !int.TryParse(match.Groups["pid"].ValueSpan, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int pid) || pid != worker.Pid ||
                    !ReadTime(match.Groups["time"].ValueSpan, clock.LocalTimeZone, out var time) ||
                    time < session.State.OpenedAt || time <= worker.CreatedAtUtc || time > now)
                    continue;

                if (!TryReadEvent(match.Groups["kind"].Value, match.Groups["details"].Value, time, out var entry))
                {
                    session.Reset(clock);
                    return WithState(row, GameTaskState.Unknown, "任务事件字段无法确认，等待新的起点。");
                }
                if (entry.Key.Entry != "MaaTaskerPostStop") session.Apply(entry);
            }

            if (!batch.CaughtUp)
                return WithState(row, GameTaskState.Unknown, "正在追读新日志，等待完整观测。");
            var observation = session.State.GameTask.Latest;
            if (observation is { Value: GameTaskState.Running } && now - observation.ObservedAt > RunningFreshness)
                return WithState(row, GameTaskState.Unknown, "近期没有可确认的任务事件，结果未知。");
            return WithState(row, session.State.GameTask.Value, "等待可关联的任务起止事件。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _sessions.Remove(id);
            return WithState(row, GameTaskState.Unknown, "暂时无法安全读取 MaaEnd 任务日志。");
        }
    }

    private static ProgramStatus WithState(ProgramStatus row, GameTaskState state, string unknownHint) =>
        row with { Task = state, TaskHint = state == GameTaskState.Unknown ? $"{Scope} {unknownHint}" : Scope };

    private static bool ReadTime(ReadOnlySpan<char> field, TimeZoneInfo zone, out DateTimeOffset time)
    {
        time = default;
        if (!DateTime.TryParseExact(field, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local) || zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local))
            return false;
        time = new(local, zone.GetUtcOffset(local));
        return true;
    }

    private static bool TryReadEvent(string kind, string details, DateTimeOffset time, out LogEvent entry)
    {
        entry = default;
        try
        {
            using var document = JsonDocument.Parse(details);
            var body = document.RootElement;
            if (!body.TryGetProperty("task_id", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt64(out long taskId) || taskId <= 0 ||
                ReadString(body, "entry") is not { Length: > 0 and <= 256 } taskEntry ||
                ReadString(body, "uuid") is not { Length: > 0 and <= 256 } scope)
                return false;
            if (body.TryGetProperty("reason", out var reason) && reason.ValueKind != JsonValueKind.String)
                return false;
            var state = (kind, ReadString(body, "reason")) switch
            {
                ("Starting", null) => GameTaskState.Running,
                ("Succeeded", "completed") => GameTaskState.Completed,
                ("Succeeded" or "Failed", "cancelled") => GameTaskState.Cancelled,
                ("Failed", "node_not_found" or "action_failed" or "error_handling_failed" or
                    "no_enabled_candidate" or "recognition_timeout") => GameTaskState.Failed,
                _ => GameTaskState.Unknown
            };
            entry = new(new(taskId, taskEntry, scope), time, state);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string? ReadString(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex("""^\[(?<time>[0-9 :.-]{23})\]\[[A-Z]{3}\]\[Px(?<pid>[0-9]{1,10})\]\[Tx[0-9]+\]\[[^\]]*EventDispatcher\.hpp\]\[L[0-9]+\]\[[^\]]*EventDispatcher::notify\] !!!OnEventNotify!!! \[handle=true\] \[msg=Tasker\.Task\.(?<kind>Starting|Succeeded|Failed)\] \[details=(?<details>\{.*\})\]\s*$""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EventLine();

    // Task identifiers and payloads stay in memory. They are never sent to BAM's persisted event store.
    private readonly record struct TaskKey(long Id, string Entry, string Scope);
    private readonly record struct LogEvent(TaskKey Key, DateTimeOffset Time, GameTaskState State);

    private sealed class Session(ProgramInstallation installation, ProcessIdentity worker, ITaskLogTail tail, TimeProvider clock)
    {
        private const int Capacity = 64;
        private readonly HashSet<TaskKey> _active = [];
        private readonly Queue<LogEvent> _recent = new(Capacity);
        private bool _overlapping;
        public ProgramInstallation Installation { get; } = installation;
        public ProcessIdentity Worker { get; } = worker;
        public ITaskLogTail Tail { get; } = tail;
        public SessionState State { get; private set; } = new(clock);

        public void Reset(TimeProvider clock)
        {
            _active.Clear();
            _recent.Clear();
            _overlapping = false;
            State = new(clock);
        }

        public void Apply(LogEvent entry)
        {
            if (entry.Time < State.GameTask.Latest?.ObservedAt || _recent.Contains(entry)) return;
            if (_recent.Count == Capacity) _recent.Dequeue();
            _recent.Enqueue(entry);
            var state = entry.State;
            if (state == GameTaskState.Running)
            {
                if (!_active.Add(entry.Key)) return;
                if (_active.Count > 1) _overlapping = true;
                if (_active.Count > Capacity) _active.Clear();
            }
            else if (!_active.Remove(entry.Key))
            {
                state = GameTaskState.Unknown;
            }

            if (_overlapping) state = GameTaskState.Unknown;
            if (_active.Count == 0) _overlapping = false;
            State.GameTask.Apply(new(State.Id, entry.Time, Source, state));
        }
    }
}
