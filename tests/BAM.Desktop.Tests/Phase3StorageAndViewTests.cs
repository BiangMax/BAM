using System.IO;
using BAM.Adapters;
using BAM.Core;
using BAM.Desktop.Data;
using BAM.Desktop.ViewModels;
using BAM.Infrastructure;
using Wpf.Ui.Controls;
using Xunit;

namespace BAM.Desktop.Tests;

public sealed class Phase3StorageAndViewTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("BamInstallStore-");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleInstanceOnlySignalsTheMatchingElevation(bool elevated)
    {
        var name = $"Biang.BAM.Test.{Guid.NewGuid():N}";
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new ManualResetEventSlim();
        var primary = Task.Factory.StartNew(() =>
        {
            try
            {
                using var instance = new SingleInstance(elevated, name);
                Assert.True(instance.IsPrimary);
                instance.Listen(() => activated.TrySetResult());
                ready.SetResult();
                Assert.True(stop.Wait(TimeSpan.FromSeconds(15)));
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            using (var different = new SingleInstance(!elevated, name))
            {
                Assert.False(different.IsPrimary);
                Assert.False(different.RequestActivation());
            }
            using (var same = new SingleInstance(elevated, name))
            {
                Assert.False(same.IsPrimary);
                Assert.True(same.RequestActivation());
            }
            await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        finally
        {
            stop.Set();
            await primary;
        }
    }

    [Fact]
    public void LaunchNoticeRequiresNewObservationOfTheSelectedInstallation()
    {
        var program = Program("fixture");
        var original = new ProcessIdentity(program.WorkerPaths[0], 17, DateTimeOffset.UnixEpoch);
        var replacement = new ProcessIdentity(original.ImagePath, original.Pid, original.CreatedAtUtc.AddSeconds(1));
        var status = new ProgramStatus(program, new("", "", null, true, null), ProcessState.Running,
            new(original, null, null, null, null, false), null, null, null, null, new HashSet<ProgramAction>());
        var oldFrame = new ProgramFrame(DateTimeOffset.UnixEpoch, TimeSpan.Zero, [status], null, null, 0, 0, 1, 0);
        Assert.Null(LiveDesktopData.ResolveLaunchNotice(oldFrame, program, null, oldFrame));
        Assert.Null(LiveDesktopData.ResolveLaunchNotice(oldFrame with { }, program, original, oldFrame));
        Assert.Null(LiveDesktopData.ResolveLaunchNotice(oldFrame with
        {
            Programs = [status with { Installation = program with { } }]
        }, program, null, oldFrame));
        var newFrame = oldFrame with { Programs = [status with { Worker = status.Worker! with { Identity = replacement } }] };
        Assert.Equal("fixture · 已确认软件运行。", LiveDesktopData.ResolveLaunchNotice(newFrame, program, original, oldFrame));
        Assert.Equal("fixture · 启动后未能确认工作进程。请在外部核对。", LiveDesktopData.ResolveLaunchNotice(oldFrame with
        {
            Programs = [status with { Worker = null, Process = ProcessState.Unknown, LaunchConfirmationTimedOut = true }]
        }, program, null, oldFrame));
    }

    [Theory]
    [InlineData("empty", "尚未添加脚本")]
    [InlineData("found", "尚未添加脚本")]
    [InlineData("unreadable", "未能确认脚本列表")]
    [InlineData("failed", "未能确认脚本列表")]
    public async Task FirstDetectionWaitsForInventoryBeforeShowingItsOutcome(string outcome, string expectedTitle)
    {
        var fixture = new DetectionFixture();
        var catalog = new InstallationCatalog(fixture);
        using var monitor = new ProgramMonitor(catalog, fixture, fixture, fixture, TimeProvider.System);
        using var live = new LiveDesktopData(monitor, catalog);
        var dashboard = new DashboardViewModel(live);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            live.Start(action =>
            {
                action();
                updated.TrySetResult();
                return Task.CompletedTask;
            });
            await fixture.Loading.Task.WaitAsync(Token);
            Assert.True(dashboard.IsDetecting);
            Assert.Equal("正在检测脚本", dashboard.DetectionTitle);
            Assert.Equal("— / —", dashboard.ProcessCount);
            Assert.Empty(dashboard.Cards);
            Assert.False(updated.Task.IsCompleted);

            if (outcome == "failed") fixture.Loaded.SetException(new IOException("injected load failure"));
            else fixture.Loaded.SetResult(outcome switch
            {
                "found" => new([Program("fixture")], [], null),
                "unreadable" => new([], [], "无法读取安装配置。", Readable: false),
                _ => new([], [], null)
            });
            await updated.Task.WaitAsync(Token);
            Assert.False(dashboard.IsDetecting);
            if (outcome == "found") Assert.Equal("fixture", Assert.Single(dashboard.Cards).Script.Name);
            else
            {
                Assert.Empty(dashboard.Cards);
                Assert.Equal(expectedTitle, dashboard.DetectionTitle);
                Assert.Equal(outcome is "failed" or "unreadable", dashboard.DetectionIssue is not null);
                Assert.Equal(outcome == "empty" ? "0 / 0" : "— / —", dashboard.ProcessCount);
            }
        }
        finally { await live.StopAsync(); }
    }

    private sealed class DetectionFixture : IProgramStore, IProgramEventStore, IProgramFiles, IProgramPlatform
    {
        public TaskCompletionSource Loading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProgramStoreLoad> Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProgramStoreLoad> LoadAsync(CancellationToken cancellation)
        {
            Loading.TrySetResult();
            return Loaded.Task.WaitAsync(cancellation);
        }

        public Task SaveAsync(IReadOnlyList<ProgramInstallation> programs, IReadOnlyList<string> suppressedPaths,
            CancellationToken cancellation) => throw new NotSupportedException();
        Task<IReadOnlyList<ProgramEvent>> IProgramEventStore.LoadAsync(CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<ProgramEvent>>([]);
        public Task AppendAsync(ProgramEvent entry, CancellationToken cancellation) => Task.CompletedTask;
        public IEnumerable<string> FindShortcuts(CancellationToken cancellation) => [];
        public LaunchEntry ReadEntry(string path) => throw new NotSupportedException();
        public bool FileExists(string path) => true;
        public bool DirectoryExists(string path) => true;
        public string? ReadSmallText(string path) => null;
        public string? ReadProductVersion(string path) => null;
        public byte[]? ReadIcon(string path, int index) => null;
        public ProcessScan Scan(IReadOnlySet<string> paths, bool readResources, CancellationToken cancellation) =>
            new(true, new HashSet<string>(), [], new Dictionary<ProcessIdentity, uint?>());
        public CommandResult Launch(LaunchEntry entry) => throw new NotSupportedException();
        public CommandResult Control(ScriptDefinition script, ProgramAction action, ProcessIdentity identity, ProgramWindow? window) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task TemporarilyLockedConfigIsNeverReplacedAfterLockClears()
    {
        var original = Program("original");
        var store = new LocalProgramStore(_directory.FullName);
        await store.SaveAsync([original], [], Token);
        var path = Path.Combine(_directory.FullName, "installations.json");
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.NotNull((await store.LoadAsync(Token)).Notice);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync([], [], Token));
        Assert.Equal(original.Id, Assert.Single((await new LocalProgramStore(_directory.FullName).LoadAsync(Token)).Programs).Id);
    }

    [Fact]
    public async Task SessionOnlyProfileReadsButNeverReplacesAppendsOrPrunes()
    {
        var original = Program("original");
        await new LocalProgramStore(_directory.FullName).SaveAsync([original], [], Token);
        var store = new LocalProgramStore(_directory.FullName, sessionOnly: true);
        Assert.Equal(original.Id, Assert.Single((await store.LoadAsync(Token)).Programs).Id);
        await store.SaveAsync([], [], Token);
        Assert.Equal(original.Id, Assert.Single((await store.LoadAsync(Token)).Programs).Id);

        var log = Path.Combine(_directory.FullName, "bam.log");
        await File.WriteAllTextAsync(log, "old", Token);
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddDays(-16));
        var logs = new LocalProgramEventStore(_directory.FullName, sessionOnly: true);
        Assert.Empty(await logs.LoadAsync(Token));
        await logs.AppendAsync(new(DateTimeOffset.UnixEpoch, null, "BAM", EventSeverity.Information, "test", ""), Token);
        Assert.Equal("old", await File.ReadAllTextAsync(log, Token));

        var settingsFile = Path.Combine(_directory.FullName, "settings.json");
        var settings = new BAM.Desktop.Settings.SettingsStore(false, settingsFile, sessionOnly: true);
        await settings.QueueSave(new(CloseToTray: true));
        Assert.False(File.Exists(settingsFile));
    }

    [Fact]
    public async Task DamagedConfigurationRecoversBackupAndPreservesBothOldFiles()
    {
        var store = new LocalProgramStore(_directory.FullName);
        var original = Program("first");
        await store.SaveAsync([original], [], Token);
        await store.SaveAsync([original with { Name = "second" }], [], Token);
        var path = Path.Combine(_directory.FullName, "installations.json");
        await File.WriteAllTextAsync(path, "{corrupt", Token);
        store = new(_directory.FullName);
        var recovered = await store.LoadAsync(Token);
        Assert.Equal(original.Id, Assert.Single(recovered.Programs).Id);
        Assert.Equal("first", recovered.Programs[0].Name);
        Assert.NotNull(recovered.Notice);
        await store.SaveAsync([original with { Name = "recovered" }], [], Token);
        Assert.Equal("{corrupt", await File.ReadAllTextAsync(path + ".corrupt", Token));
        Assert.Equal("recovered", (await store.LoadAsync(Token)).Programs[0].Name);
        Assert.Contains("first", await File.ReadAllTextAsync(path + ".bak", Token));
    }

    [Fact]
    public async Task LegacyShortcutKeepsItsSavedFieldsUntilInheritanceIsExplicitlyEnabled()
    {
        var original = Program("legacy");
        original = original with
        {
            Entry = original.Entry with { OriginalPath = Path.ChangeExtension(original.Entry.OriginalPath, ".lnk"), Arguments = "--saved" }
        };
        var path = Path.Combine(_directory.FullName, "installations.json");
        var program = System.Text.Json.JsonSerializer.SerializeToNode(original)!.AsObject();
        program.Remove(nameof(ProgramInstallation.InheritShortcutArguments));
        program.Remove(nameof(ProgramInstallation.InheritShortcutDirectory));
        await File.WriteAllTextAsync(path, $$"""{"SchemaVersion":1,"Programs":[{{program.ToJsonString()}}]}""", Token);
        var store = new LocalProgramStore(_directory.FullName);
        var restored = Assert.Single((await store.LoadAsync(Token)).Programs);
        Assert.False(restored.InheritShortcutArguments);
        Assert.False(restored.InheritShortcutDirectory);
        Assert.Equal(original.Entry, restored.Entry);
        await store.SaveAsync([restored with { InheritShortcutArguments = true }], [], Token);
        var updated = Assert.Single((await store.LoadAsync(Token)).Programs);
        Assert.True(updated.InheritShortcutArguments);
        Assert.False(updated.InheritShortcutDirectory);
    }

    [Theory]
    [InlineData("{\"Programs\":[]}", true)]
    [InlineData("{\"SchemaVersion\":19,\"Programs\":[]}", false)]
    [InlineData("{\"SchemaVersion\":19,\"Programs\":{\"kind\":\"future\"}}", false)]
    public async Task MissingVersionMigratesAndFutureVersionStaysIntact(string document, bool writeAllowed)
    {
        var path = Path.Combine(_directory.FullName, "installations.json");
        await File.WriteAllTextAsync(path, document, Token);
        var store = new LocalProgramStore(_directory.FullName);
        Assert.Empty((await store.LoadAsync(Token)).Programs);
        if (writeAllowed)
        {
            await store.SaveAsync([Program("migrated")], [], Token);
            Assert.Contains("\"SchemaVersion\":1", await File.ReadAllTextAsync(path, Token));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync([Program("new")], [], Token));
            Assert.Equal(document, await File.ReadAllTextAsync(path, Token));
        }
    }

    [Fact]
    public void MonotonicHistorySurvivesClockRollbackAndKeepsTheNewWallTimestamp()
    {
        var history = new ResourceHistory();
        var wall = DateTimeOffset.UnixEpoch;
        history.Add(new(wall, 12, 123, TimeSpan.FromSeconds(2)));
        history.Add(new(wall.AddMinutes(-1), 14, null, TimeSpan.FromSeconds(3)));
        Assert.Equal(2, history.Count);
        Assert.Equal(wall.AddMinutes(-1), history.End);
        Assert.Equal(TimeSpan.FromSeconds(3), history.EndPosition);
        history.Add(new(wall, 4, 1, TimeSpan.FromSeconds(125)));
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void ScriptLogShortcutClearsOldLevelAndSearch()
    {
        var script = new ScriptItem("MAA", "fixture", "v1", SymbolRegular.Apps24, Tone.Accent,
            ProcessState.Stopped, GameTaskState.Unsupported, "", "", "", "");
        var model = new LogsViewModel(new LogFixture(script));
        model.ShowErrors();
        model.Query = "unrelated";
        Assert.False(model.HasResults);
        model.ShowScript(script);
        Assert.True(model.HasResults);
        Assert.Equal("MAA", model.Source);
        Assert.Equal("全部级别", model.Level);
        Assert.Empty(model.Query);
    }

    [Fact]
    public async Task ShutdownDoesNotBlockOnAStalledFlush()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await App.WaitForShutdownAsync(stalled.Task, TimeSpan.Zero);
        Assert.False(stalled.Task.IsCompleted);
        stalled.SetResult();
        await App.WaitForShutdownAsync(stalled.Task, TimeSpan.Zero);
    }

    private ProgramInstallation Program(string name)
    {
        var path = Path.Combine(_directory.FullName, "program.exe");
        return new(Guid.NewGuid(), name, ProgramKind.Generic, new(path, path, _directory.FullName, ""),
            _directory.FullName, [path]);
    }

    private sealed class LogFixture(ScriptItem script) : IDesktopData
    {
        public bool IsDemo => true;
        public IReadOnlyList<ScriptItem> Scripts => [script];
        public IReadOnlyList<LogEntry> Logs => [new(DateTimeOffset.UnixEpoch, "MAA", LogLevel.Information, "own log", "")];
        public bool HasResourceSource => false;
        public string? GetLogSourceNotice(string source) => null;
        public ResourceFrame? ReadResources(DateTimeOffset time) => null;
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
