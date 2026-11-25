namespace BAM.Desktop.Data;

internal readonly record struct ResourcePoint(DateTimeOffset Time, double? Cpu, double? MemoryMiB, TimeSpan? Elapsed = null)
{
    public TimeSpan Position => Elapsed ?? TimeSpan.FromTicks(Time.UtcTicks);
}

internal sealed class ResourceHistory
{
    public const int WindowSeconds = 120;
    private readonly ResourcePoint[] _points = new ResourcePoint[WindowSeconds + 1];
    private int _start;
    private int _knownPoints;
    public int Count { get; private set; }
    public bool HasData => _knownPoints != 0;
    public DateTimeOffset End { get; private set; }
    public TimeSpan EndPosition { get; private set; }

    public ResourcePoint this[int offset] => offset >= 0 && offset < Count
        ? _points[(_start + offset) % _points.Length]
        : throw new ArgumentOutOfRangeException(nameof(offset));

    public void Add(ResourcePoint point)
    {
        if (Count > 0 && point.Position <= EndPosition) return;
        End = point.Time;
        EndPosition = point.Position;

        while (Count > 0 && EndPosition - this[0].Position > TimeSpan.FromSeconds(WindowSeconds))
            DropOldest();
        if (Count == _points.Length) DropOldest();

        _points[(_start + Count) % _points.Length] = point;
        Count++;
        if (point.Cpu.HasValue || point.MemoryMiB.HasValue) _knownPoints++;
    }

    private void DropOldest()
    {
        if (_points[_start].Cpu.HasValue || _points[_start].MemoryMiB.HasValue) _knownPoints--;
        _start = (_start + 1) % _points.Length;
        Count--;
    }
}
