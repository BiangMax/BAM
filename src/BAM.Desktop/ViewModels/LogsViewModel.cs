using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using BAM.Desktop.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BAM.Desktop.ViewModels;

internal sealed partial class LogsViewModel : ObservableObject
{
    private const int Capacity = 2000;
    private const string AllSources = "全部来源";
    private const string AllLevels = "全部级别";
    private const string ErrorLevel = "错误";
    private readonly IDesktopData _data;

    [ObservableProperty] private string _source = AllSources;
    [ObservableProperty] private string _level = AllLevels;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _follow = true;
    [ObservableProperty] private LogEntry? _selected;
    [ObservableProperty] private string? _copyNotice;

    public LogsViewModel(IDesktopData data)
    {
        _data = data;
        var entries = data.Logs.Take(Capacity).ToArray();
        Sources = [AllSources, "BAM", .. data.Scripts.Select(script => script.Name)];
        Entries = new ListCollectionView(entries) { Filter = item => Matches((LogEntry)item) };
        Selected = (LogEntry?)Entries.CurrentItem;
        ErrorCount = entries.Count(entry => entry.Level == LogLevel.Error);
        LimitLabel = $"最多保留 {Capacity:N0} 条" + (data.Logs.Count > Capacity ? $" · 已省略 {data.Logs.Count - Capacity:N0} 条" : "");
    }

    public IReadOnlyList<string> Sources { get; }
    public IReadOnlyList<string> Levels { get; } = [AllLevels, "信息", "警告", ErrorLevel];
    public ICollectionView Entries { get; }
    public string LimitLabel { get; }
    public int ErrorCount { get; }
    public bool HasResults => !Entries.IsEmpty;
    public string CountLabel => $"{((ListCollectionView)Entries).Count} 条记录";
    public string EmptyLabel => _data.IsDemo ? "没有符合当前筛选条件的记录" : "尚无运行日志";
    public string EmptyHint => _data.IsDemo ? "可调整来源、级别或搜索词。" : "第三方日志采集尚未接入。";
    public string? SourceNotice => _data.GetLogSourceNotice(Source);
    public bool HasSourceNotice => SourceNotice is not null;
    public bool HasSelection => Selected is not null;

    public void ShowErrors()
    {
        Source = AllSources;
        Query = "";
        Level = ErrorLevel;
    }

    partial void OnSourceChanged(string value) => Refresh();
    partial void OnLevelChanged(string value) => Refresh();
    partial void OnQueryChanged(string value) => Refresh();
    partial void OnSelectedChanged(LogEntry? value)
    {
        CopyNotice = null;
        OnPropertyChanged(nameof(HasSelection));
        CopyCommand.NotifyCanExecuteChanged();
    }

    private bool Matches(LogEntry entry)
    {
        var query = Query.Trim();
        return (Source == AllSources || Source == entry.Source)
            && (Level == AllLevels || Level == entry.LevelLabel)
            && (query.Length == 0 || entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Source.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void Refresh()
    {
        var selected = Selected;
        Entries.Refresh();
        Selected = selected is not null && Entries.Contains(selected) ? selected : (LogEntry?)Entries.CurrentItem;
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(SourceNotice));
        OnPropertyChanged(nameof(HasSourceNotice));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        if (Selected is not { } entry) return;
        try
        {
            Clipboard.SetText(entry.ClipboardText);
            CopyNotice = "已复制";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            CopyNotice = "剪贴板暂不可用，请重试";
        }
    }
}
