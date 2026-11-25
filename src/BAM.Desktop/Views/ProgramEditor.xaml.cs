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
    private bool _working;

    internal ProgramEditor(LiveDesktopData data, ProgramInstallation? program)
    {
        _data = data;
        _installationId = program?.Id;
        InitializeComponent();
        if (program is not null) ShowFields(program);
    }

    private void ShowFields(ProgramInstallation program)
    {
        _program = program;
        EntryPath.Text = program.Entry.OriginalPath;
        ProgramName.Text = program.Name;
        WorkDirectory.Text = program.Entry.WorkingDirectory;
        LaunchArguments.Text = program.Entry.Arguments;
        WorkerPath.Text = program.WorkerPaths.Length == 1 ? program.WorkerPaths[0] : "";
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

    private async void Save(object sender, RoutedEventArgs e)
    {
        if (_working || _program is not { } program) return;
        _working = true;
        SaveButton.IsEnabled = false;
        try
        {
            await _data.SaveAsync(program, ProgramName.Text, WorkDirectory.Text, LaunchArguments.Text, WorkerPath.Text);
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
