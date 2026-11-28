using System.IO;
using BAM.Core;
using BAM.Desktop.Data;
using BAM.Desktop.Settings;
using BAM.Desktop.ViewModels;
using Wpf.Ui.Controls;
using Xunit;

namespace BAM.Desktop.Tests;

public sealed class ScriptOrderTests : IDisposable
{
    private static readonly Guid MaaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MaaEndId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ChannelId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("BamOrderTests-");
    private string OrderFile => Path.Combine(_directory.FullName, "script-order.json");
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void SavedOrderDrivesCardSequence()
    {
        var model = NewModel(new[] { MaaEndId, ChannelId, MaaId }, true);

        Assert.Equal(new[] { "MaaEnd", "BBchannel", "MAA" }, model.Cards.Select(card => card.Script.Name));
    }

    [Fact]
    public void InstallationsMissingFromSavedOrderFollowKindPriority()
    {
        // 只记录 BBchannel 时，其余安装按默认类型顺序排在后面。
        var model = NewModel(new[] { ChannelId }, true);

        Assert.Equal(new[] { "BBchannel", "MAA", "MaaEnd" }, model.Cards.Select(card => card.Script.Name));
    }

    [Fact]
    public void WithoutSavedOrderTheKindPriorityApplies()
    {
        var model = NewModel(null, true);

        Assert.Equal(new[] { "MAA", "MaaEnd", "BBchannel" }, model.Cards.Select(card => card.Script.Name));
    }

    [Fact]
    public async Task MovingDownSwapsNeighboursAndPersists()
    {
        var store = new ScriptOrderStore(OrderFile);
        var model = NewModel(null, false, store);
        var first = model.Cards[0].Script;

        Assert.True(model.CanMove(first, 1));
        Assert.True(model.Move(first, 1));

        Assert.Equal(new[] { "MaaEnd", "MAA", "BBchannel" }, model.Cards.Select(card => card.Script.Name));
        await store.FlushAsync();
        Assert.Equal(new[] { MaaEndId, MaaId, ChannelId }, await new ScriptOrderStore(OrderFile).LoadAsync(Cancellation));
    }

    [Fact]
    public async Task MovingUpSwapsBackToTheOriginalSequence()
    {
        var store = new ScriptOrderStore(OrderFile);
        var model = NewModel(null, false, store);
        var first = model.Cards[0].Script;
        model.Move(first, 1);

        Assert.True(model.Move(model.Cards[1].Script, -1));

        Assert.Equal(new[] { "MAA", "MaaEnd", "BBchannel" }, model.Cards.Select(card => card.Script.Name));
        await store.FlushAsync();
        Assert.Equal(new[] { MaaId, MaaEndId, ChannelId }, await new ScriptOrderStore(OrderFile).LoadAsync(Cancellation));
    }

    [Fact]
    public void CannotMovePastEitherEnd()
    {
        var model = NewModel(null, true);
        var first = model.Cards[0].Script;
        var last = model.Cards[^1].Script;

        Assert.False(model.CanMove(first, -1));
        Assert.False(model.Move(first, -1));
        Assert.False(model.CanMove(last, 1));
        Assert.False(model.Move(last, 1));
        Assert.Equal(new[] { "MAA", "MaaEnd", "BBchannel" }, model.Cards.Select(card => card.Script.Name));
    }

    [Fact]
    public void DemoSizedCardWithoutInstallationCannotBeMoved()
    {
        var model = new DashboardViewModel(new OrderData(
            new ScriptItem("演示", "游戏", "版本未知", SymbolRegular.Apps24, Tone.Neutral, ProcessState.Stopped,
                GameTaskState.Unsupported, "", "", "—", "")));

        Assert.False(model.CanMove(model.Cards[0].Script, 1));
    }

    [Fact]
    public async Task StoreRoundTripsSavedIdentifiers()
    {
        var store = new ScriptOrderStore(OrderFile);
        await store.QueueSave(new[] { ChannelId, MaaId });
        await store.FlushAsync();

        Assert.Equal(new[] { ChannelId, MaaId }, await new ScriptOrderStore(OrderFile).LoadAsync(Cancellation));
    }

    [Fact]
    public async Task CorruptFileFallsBackToEmptyOrder()
    {
        await File.WriteAllTextAsync(OrderFile, "{ not json", Cancellation);

        Assert.Empty(await new ScriptOrderStore(OrderFile).LoadAsync(Cancellation));
    }

    [Fact]
    public async Task SessionOnlyStoreNeverWrites()
    {
        var store = new ScriptOrderStore(OrderFile, sessionOnly: true);
        await store.QueueSave(new[] { MaaId });
        await store.FlushAsync();

        Assert.False(File.Exists(OrderFile));
    }

    private DashboardViewModel NewModel(IReadOnlyList<Guid>? saved, bool sessionOnly,
        ScriptOrderStore? store = null)
    {
        store ??= new ScriptOrderStore(OrderFile, sessionOnly);
        var order = new ScriptOrder(store, saved ?? Array.Empty<Guid>());
        return new DashboardViewModel(new OrderData(
            Script(MaaId, "MAA", ProgramKind.Maa),
            Script(MaaEndId, "MaaEnd", ProgramKind.MaaEnd),
            Script(ChannelId, "BBchannel", ProgramKind.BBchannel)), order);
    }

    private static ScriptItem Script(Guid id, string name, ProgramKind kind)
    {
        var folder = Path.Combine(Path.GetTempPath(), name);
        var target = Path.Combine(folder, $"{name}.exe");
        var program = new ProgramInstallation(id, name, kind,
            new LaunchEntry(Path.Combine(folder, $"{name}.lnk"), target, folder, ""), folder, [target]);
        return new ScriptItem(name, "游戏", "版本未知", SymbolRegular.Apps24, Tone.Success, ProcessState.Stopped,
            GameTaskState.Unsupported, folder, folder, "—", "") { Installation = program };
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private sealed class OrderData(params ScriptItem[] scripts) : IDesktopData
    {
        public bool IsDemo => true;
        public bool HasResourceSource => false;
        public IReadOnlyList<ScriptItem> Scripts { get; } = scripts;
        public IReadOnlyList<LogEntry> Logs => [];
        public string? GetLogSourceNotice(string source) => null;
        public ResourceFrame? ReadResources(DateTimeOffset time) => null;
    }
}
