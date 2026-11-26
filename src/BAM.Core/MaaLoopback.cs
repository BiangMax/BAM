namespace BAM.Core;

public static class MaaLoopbackRoutes
{
    public const string GetTask = "/getTask";
    public const string ReportStatus = "/reportStatus";
}

public enum MaaHeartbeatReport { Accepted, Unknown, Expired, Conflict }

public sealed record MaaHeartbeatSnapshot(DateTimeOffset? LastPoll, RemoteCommandState State,
    bool? ReportSucceeded = null, bool? ReportContainsRemoteTask = null);

public interface IMaaHeartbeatSession
{
    bool IsPaired(string user, string device);
    Guid? Poll();
    MaaHeartbeatReport Report(Guid id, bool succeeded, string payload);
}

public interface IMaaLoopbackHost
{
    Task<int> StartAsync(int port, IMaaHeartbeatSession session, CancellationToken cancellation);
    Task StopAsync(CancellationToken cancellation);
}
