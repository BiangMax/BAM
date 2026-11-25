using System.IO;
using System.Text.Json;

namespace BAM.Desktop.Settings;

internal enum ThemeChoice { Dark, Light, System }

internal sealed record DesktopPreferences(
    int SchemaVersion = 1, ThemeChoice Theme = ThemeChoice.Dark, bool ReduceMotion = false,
    bool CloseToTray = false, bool TrayIntroSeen = false, bool CollectResources = true);

internal sealed class SettingsStore(bool isDemo, string? file = null, bool sessionOnly = false)
{
    private readonly string _file = file ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BAM", "settings.json");
    private bool _canWrite = true;
    private Task<string?> _pending = Task.FromResult<string?>(null);

    public async Task<(DesktopPreferences Preferences, string? Notice)> LoadAsync(CancellationToken cancellationToken)
    {
        if (isDemo) return (new(), null);
        try
        {
            return (await ReadAsync(_file, cancellationToken), null);
        }
        catch (FileNotFoundException) { return (new(), null); }
        catch (DirectoryNotFoundException) { return (new(), null); }
        catch (NotSupportedException)
        {
            _canWrite = false;
            return (new(), "设置由较新版本创建，本次变更仅用于当前会话。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _canWrite = false;
            try
            {
                return (await ReadAsync(_file + ".bak", cancellationToken),
                    "已从备份读取设置，本次变更仅用于当前会话。");
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return (new(), "无法读取本地设置，本次变更仅用于当前会话。");
            }
        }
    }

    public Task<string?> QueueSave(DesktopPreferences settings)
    {
        if (isDemo || sessionOnly || !_canWrite) return Task.FromResult<string?>(null);
        return _pending = SaveAfterAsync(_pending, settings);
    }

    public Task FlushAsync() => _pending;

    private static async Task<DesktopPreferences> ReadAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > 16384) throw new JsonException();
        var settings = await JsonSerializer.DeserializeAsync<DesktopPreferences>(stream, cancellationToken: cancellationToken);
        if (settings is null || !Enum.IsDefined(settings.Theme)) throw new JsonException();
        if (settings.SchemaVersion != 1) throw new NotSupportedException();
        return settings;
    }

    private async Task<string?> SaveAfterAsync(Task previous, DesktopPreferences settings)
    {
        await previous.ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            var temporary = _file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                    await JsonSerializer.SerializeAsync(stream, settings, cancellationToken: CancellationToken.None).ConfigureAwait(false);

                if (File.Exists(_file)) File.Replace(temporary, _file, _file + ".bak");
                else File.Move(temporary, _file);
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "无法保存本地设置，本次变更已在当前会话生效。";
            }
        }).ConfigureAwait(false);
    }
}
