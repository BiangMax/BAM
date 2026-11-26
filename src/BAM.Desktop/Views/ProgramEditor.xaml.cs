using System.Globalization;
using System.IO;
using System.Windows;
using BAM.Core;
using BAM.Desktop.Data;
using Microsoft.Win32;

namespace BAM.Desktop.Views;

public partial class ProgramEditor : Window
{
    private readonly LiveDesktopData _data;
    private readonly Guid? _installationId;
    private ProgramInstallation? _program;
    private LaunchEntry? _emulator;
    private bool _working;

    internal ProgramEditor(LiveDesktopData data, ProgramInstallation? program, bool focusEmulator = false)
    {
        _data = data;
        _installationId = program?.Id;
        InitializeComponent();
        EmulatorWait.Text = (program?.Emulator?.WaitSeconds ?? EmulatorStartup.DefaultWaitSeconds).ToString();
        ShowEmulatorEntry(program?.Emulator?.Entry);
        MonitorImage.Text = program?.EmulatorMonitor?.ImagePath ?? "";
        MonitorWorker.Text = program?.EmulatorMonitor?.WorkerImagePath ?? "";
        MonitorInstance.Text = program?.EmulatorMonitor?.InstanceNumber?.ToString(CultureInfo.InvariantCulture) ?? "";
        UpdateMonitorFields();
        if (program is not null) ShowFields(program);
        if (focusEmulator) Loaded += (_, _) =>
        {
            ChooseEmulatorButton.BringIntoView();
            ChooseEmulatorButton.Focus();
        };
    }

    private void ShowFields(ProgramInstallation program)
    {
        _program = program;
        EntryPath.Text = program.Entry.OriginalPath;
        ProgramName.Text = program.Name;
        WorkDirectory.Text = program.Entry.WorkingDirectory;
        LaunchArguments.Text = program.Entry.Arguments;
        WorkerPath.Text = program.WorkerPaths.Length == 1 ? program.WorkerPaths[0] : "";
        EmulatorSettings.Visibility = program.SupportsEmulatorStartup ? Visibility.Visible : Visibility.Collapsed;
        EditorNotice.Text = "";
        SaveButton.IsEnabled = true;
    }

    private async void ChooseEntry(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        var picker = new OpenFileDialog { Filter = "启动入口|*.exe;*.lnk;*.cmd;*.bat", CheckFileExists = true, DereferenceLinks = false };
        if (picker.ShowDialog(this) != true) return;
        _working = true;
        SaveButton.IsEnabled = false;
        try
        {
            var program = await _data.ImportAsync(picker.FileName);
            if (!IsLoaded) return;
            ShowFields(_installationId is { } id ? program with { Id = id, Name = _program!.Name } : program);
        }
        catch (Exception error) when (LiveDesktopData.IsExpected(error))
        {
            EditorNotice.Text = "无法读取所选启动入口，请检查文件类型和权限。";
            SaveButton.IsEnabled = _program is not null;
        }
        finally { _working = false; }
    }

    private void ChooseDirectory(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog();
        if (picker.ShowDialog(this) == true) WorkDirectory.Text = picker.FolderName;
    }

    private void ChooseWorker(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "可执行程序|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) WorkerPath.Text = picker.FileName;
    }

    private async void ChooseEmulator(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        var picker = new OpenFileDialog { Filter = "模拟器启动入口|*.lnk;*.exe", CheckFileExists = true, DereferenceLinks = false };
        if (picker.ShowDialog(this) != true) return;
        _working = true;
        SaveButton.IsEnabled = false;
        try
        {
            var entry = await _data.ImportEmulatorAsync(picker.FileName);
            if (!IsLoaded) return;
            ShowEmulatorEntry(entry);
            EditorNotice.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (LiveDesktopData.IsExpected(error))
        {
            EditorNotice.Text = "无法读取所选入口，请选择指向模拟器程序的实例快捷方式或 EXE。";
        }
        finally
        {
            _working = false;
            SaveButton.IsEnabled = _program is not null;
        }
    }

    private void ClearEmulator(object sender, RoutedEventArgs e)
    {
        if (!_working) ShowEmulatorEntry(null);
    }

    private async void ChooseEmulatorMonitor(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        var picker = new OpenFileDialog { Filter = "可执行程序|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        _working = true;
        SaveButton.IsEnabled = false;
        try
        {
            var path = await _data.ImportEmulatorImageAsync(picker.FileName);
            if (!IsLoaded) return;
            if (ReferenceEquals(sender, ChooseMonitorWorkerButton)) MonitorWorker.Text = path;
            else if (!string.Equals(MonitorImage.Text, path, StringComparison.OrdinalIgnoreCase))
            {
                MonitorImage.Text = path;
                MonitorWorker.Clear();
                MonitorInstance.Clear();
            }
            UpdateMonitorFields();
            EditorNotice.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (LiveDesktopData.IsExpected(error))
        {
            EditorNotice.Text = error is ArgumentException ? error.Message : "无法读取所选程序。";
        }
        finally
        {
            _working = false;
            SaveButton.IsEnabled = _program is not null;
        }
    }

    private void ClearEmulatorMonitor(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        MonitorWorker.Clear();
        if (!ReferenceEquals(sender, ClearMonitorWorkerButton))
        {
            MonitorImage.Clear();
            MonitorInstance.Clear();
        }
        UpdateMonitorFields();
    }

    private void UpdateMonitorFields()
    {
        var selected = MonitorImage.Text.Length > 0;
        ChooseMonitorWorkerButton.IsEnabled = selected;
        ClearMonitorWorkerButton.IsEnabled = selected && MonitorWorker.Text.Length > 0;
        MonitorInstance.IsEnabled = selected;
    }

    private void ShowEmulatorEntry(LaunchEntry? entry)
    {
        _emulator = entry;
        EmulatorPath.Text = entry?.OriginalPath ?? "";
        EmulatorArguments.Text = entry?.Arguments ?? "";
        EmulatorArguments.IsEnabled = entry is not null;
        EmulatorArguments.IsReadOnly = string.Equals(Path.GetExtension(entry?.OriginalPath), ".lnk", StringComparison.OrdinalIgnoreCase);
        EmulatorWait.IsEnabled = entry is not null;
        ClearEmulatorButton.IsEnabled = entry is not null;
    }

    private async void Save(object sender, RoutedEventArgs e)
    {
        if (_working || _program is not { } program) return;
        EmulatorMonitoring? monitoring = null;
        if (MonitorImage.Text.Length > 0)
        {
            int? number = null;
            if (MonitorInstance.Text.Trim() is { Length: > 0 } text)
            {
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    EditorNotice.Text = "实例编号须为非负整数，单实例可以留空。";
                    return;
                }
                number = parsed;
            }
            monitoring = new(MonitorImage.Text, number, MonitorWorker.Text.Length > 0 ? MonitorWorker.Text : null);
        }
        EmulatorStartup? setup = null;
        if (program.SupportsEmulatorStartup && _emulator is { } entry)
        {
            if (!int.TryParse(EmulatorWait.Text, out var seconds) || seconds is < 0 or > EmulatorStartup.MaximumWaitSeconds)
            {
                EditorNotice.Text = $"等待时间须为 0 至 {EmulatorStartup.MaximumWaitSeconds} 秒的整数。";
                return;
            }
            setup = new(entry with { Arguments = EmulatorArguments.IsReadOnly ? entry.Arguments : EmulatorArguments.Text }, seconds);
        }
        _working = true;
        SaveButton.IsEnabled = false;
        try
        {
            await _data.SaveAsync(program, ProgramName.Text, WorkDirectory.Text, LaunchArguments.Text, WorkerPath.Text, setup, monitoring);
            if (IsLoaded) DialogResult = true;
        }
        catch (Exception error) when (LiveDesktopData.IsExpected(error))
        {
            EditorNotice.Text = error is IOException or UnauthorizedAccessException
                ? "无法保存本地配置，请检查配置目录权限。" : error.Message;
        }
        finally
        {
            SaveButton.IsEnabled = true;
            _working = false;
        }
    }
}
