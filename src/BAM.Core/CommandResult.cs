namespace BAM.Core;

public enum CommandStatus { Unknown, Accepted, Completed, Unsupported, Denied, IdentityChanged, TimedOut, Failed }
public sealed record CommandResult(CommandStatus Status, string? Reason = null);

public static class CommandPolicy
{
    public static string? GetWindowControlRestriction(bool? allowed) => allowed switch
    {
        true => null,
        false => "目标窗口的完整性级别高于 BAM，当前权限无法控制。",
        null => "无法读取 BAM 或目标进程令牌的完整性级别，窗口控制暂不可用。"
    };

    // Authorization decision only. A future executor must retain/revalidate the same process handle.
    public static CommandResult CheckTarget(ScriptDefinition script, CapabilityKind requested,
        Capability capability, ProcessIdentity? selected, ProcessInspection current)
    {
        if (capability.Kind != requested)
            return new(CommandStatus.Denied, "能力与请求不符。");
        if (capability.Status == CapabilityStatus.Unsupported)
            return new(CommandStatus.Unsupported, capability.Reason);
        if (capability.Status != CapabilityStatus.Available)
            return new(CommandStatus.Denied, capability.Reason ?? "能力尚不可用。");
        if (selected is null || current.Identity is null || !script.WorkerPaths.Contains(selected.ImagePath))
            return new(CommandStatus.Denied, "无法确认目标属于此安装。");
        if (!selected.Equals(current.Identity))
            return new(CommandStatus.IdentityChanged, "实例已改变。");

        return new(CommandStatus.Accepted);
    }
}
