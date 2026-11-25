namespace BAM.Desktop.Data;

internal readonly record struct ResourcePoint(DateTimeOffset Time, double? Cpu, double? MemoryMiB);

internal sealed class ResourceHistory
{
    public const int WindowSeconds = 120;
    private readonly ResourcePoint[] _points = new ResourcePoint[WindowSeconds + 1];
    private int _start;
    public int Count { get; private set; }
    public DateTimeOffset End { get; private set; }

    public ResourcePoint this[int offset] => offset >= 0 && offset < Count
        ? _points[(_start + offset) % _points.Length]
        : throw new ArgumentOutOfRangeException(nameof(offset));

    public void Add(ResourcePoint point)
    {
        if (Count > 0 && point.Time <= End) return;
        End = point.Time;

        while (Count > 0 && End - this[0].Time > TimeSpan.FromSeconds(WindowSeconds))
            DropOldest();
        if (Count == _points.Length) DropOldest();

        _points[(_start + Count) % _points.Length] = point;
        Count++;
    }

    private void DropOldest()
    {
        _start = (_start + 1) % _points.Length;
        Count--;
    }
}
