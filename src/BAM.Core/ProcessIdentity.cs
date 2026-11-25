namespace BAM.Core;

public sealed class ProcessIdentity : IEquatable<ProcessIdentity>
{
    public ProcessIdentity(string imagePath, int pid, DateTimeOffset createdAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        ImagePath = NormalizePath(imagePath);
        Pid = pid;
        CreatedAtUtc = createdAt.ToUniversalTime();
    }

    public string ImagePath { get; }
    public int Pid { get; }
    public DateTimeOffset CreatedAtUtc { get; }

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Expected a fully qualified path.", nameof(path));

        return Path.GetFullPath(path);
    }

    public bool Equals(ProcessIdentity? other) =>
        other is not null && Pid == other.Pid && CreatedAtUtc == other.CreatedAtUtc &&
        StringComparer.OrdinalIgnoreCase.Equals(ImagePath, other.ImagePath);

    public override bool Equals(object? obj) => Equals(obj as ProcessIdentity);
    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(ImagePath), Pid, CreatedAtUtc);
}
