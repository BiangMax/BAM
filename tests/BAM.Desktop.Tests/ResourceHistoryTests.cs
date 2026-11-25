using BAM.Desktop.Data;
using Xunit;

namespace BAM.Desktop.Tests;

public sealed class ResourceHistoryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void WrapRetainsInclusiveWindowInOrder()
    {
        var history = new ResourceHistory();
        for (var second = 0; second < 240; second++)
            history.Add(new(Start.AddSeconds(second), second, null));

        Assert.Equal(121, history.Count);
        Assert.Equal(Start.AddSeconds(119), history[0].Time);
        Assert.Equal(Start.AddSeconds(239), history.End);
        Assert.Equal(239, history[120].Cpu);
        Assert.Throws<ArgumentOutOfRangeException>(() => history[121]);
    }

    [Fact]
    public void IgnoresDuplicatesAndOutOfOrderPoints()
    {
        var history = new ResourceHistory();
        history.Add(new(Start.AddSeconds(2), 10, 307));
        history.Add(new(Start.AddSeconds(2), 15, 399));
        history.Add(new(Start.AddSeconds(1), 5, 250));

        Assert.Equal(new(Start.AddSeconds(2), 10, 307), history[0]);
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void RetainsMissingValuesAndPrunesAfterPause()
    {
        var history = new ResourceHistory();
        history.Add(new(Start, 0, 120));
        history.Add(new(Start.AddSeconds(1), null, null));
        history.Add(new(Start.AddSeconds(121), 9, null));

        Assert.Equal(2, history.Count);
        Assert.Null(history[0].Cpu);
        Assert.Null(history[0].MemoryMiB);
        Assert.Equal(9, history[1].Cpu);
    }
}
