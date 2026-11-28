using System.IO;
using BAM.Desktop.Settings;
using Xunit;

namespace BAM.Desktop.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("BamSettingsTests-");
    private string SettingsFile => Path.Combine(_directory.FullName, "settings.json");
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SavesInOrderAndKeepsPreviousValidDocument()
    {
        var store = new SettingsStore(false, SettingsFile);
        var first = new DesktopPreferences(Theme: ThemeChoice.Light);
        var last = new DesktopPreferences(Theme: ThemeChoice.System, CloseToTray: true);
        var firstSave = store.QueueSave(first);
        var lastSave = store.QueueSave(last);

        await store.FlushAsync();
        Assert.Null(await firstSave);
        Assert.Null(await lastSave);
        Assert.Equal(last, (await store.LoadAsync(Cancellation)).Preferences);

        var backup = new SettingsStore(false, SettingsFile + ".bak");
        Assert.Equal(first, (await backup.LoadAsync(Cancellation)).Preferences);
    }

    [Fact]
    public async Task DemoDoesNotReadOrPersistUserSettings()
    {
        await File.WriteAllTextAsync(SettingsFile, "invalid production settings", Cancellation);
        var store = new SettingsStore(true, SettingsFile);

        Assert.Equal(new DesktopPreferences(), (await store.LoadAsync(Cancellation)).Preferences);
        Assert.Null(await store.QueueSave(new DesktopPreferences(Theme: ThemeChoice.Light)));
        Assert.Equal("invalid production settings", await File.ReadAllTextAsync(SettingsFile, Cancellation));
    }

    [Fact]
    public async Task NewerSchemaRemainsIntact()
    {
        const string future = """{"SchemaVersion":2,"Theme":0}""";
        await File.WriteAllTextAsync(SettingsFile, future, Cancellation);
        var store = new SettingsStore(false, SettingsFile);

        Assert.NotNull((await store.LoadAsync(Cancellation)).Notice);
        Assert.Null(await store.QueueSave(new DesktopPreferences(CloseToTray: true)));
        Assert.Equal(future, await File.ReadAllTextAsync(SettingsFile, Cancellation));
    }

    [Fact]
    public async Task BatchIntervalRoundTripsAndIsClampedOnRead()
    {
        var store = new SettingsStore(false, SettingsFile);
        Assert.Null(await store.QueueSave(new DesktopPreferences(BatchIntervalSeconds: 120)));
        Assert.Equal(120, (await store.LoadAsync(Cancellation)).Preferences.BatchIntervalSeconds);

        await File.WriteAllTextAsync(SettingsFile, """{"SchemaVersion":1,"BatchIntervalSeconds":99999}""", Cancellation);
        Assert.Equal(SettingsStore.MaxBatchIntervalSeconds, (await store.LoadAsync(Cancellation)).Preferences.BatchIntervalSeconds);

        await File.WriteAllTextAsync(SettingsFile, """{"SchemaVersion":1,"BatchIntervalSeconds":-30}""", Cancellation);
        Assert.Equal(0, (await store.LoadAsync(Cancellation)).Preferences.BatchIntervalSeconds);
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
