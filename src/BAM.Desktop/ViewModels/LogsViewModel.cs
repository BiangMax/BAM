using System.ComponentModel;
using System.Collections.ObjectModel;
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
    private readonly ObservableCollection<LogEntry> _entries;
    private readonly Dictionary<string, Guid> _sourceIds = new(StringComparer.Ordinal);
    private IReadOnlyList<LogEntry> _snapshot;

    [ObservableProperty] private string _source = AllSources;
    [ObservableProperty] private string _level = AllLevels;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _follow = true;
    [ObservableProperty] private LogEntry? _selected;
    [ObservableProperty] private string? _copyNotice;

    public LogsViewModel(IDesktopData data)
    {
        _data = data;
        _snapshot = data.Logs;
        _entries = new(data.Logs.Take(Capacity));
        Sources = BuildSources();
        Entries = new ListCollectionView(_entries) { Filter = item => Matches((LogEntry)item) };
        Selected = (LogEntry?)Entries.CurrentItem;
        if (data is LiveDesktopData live) live.Updated += (_, _) => UpdateLive();
    }

    public IReadOnlyList<string> Sources { get; private set; }
    public IReadOnlyList<string> Levels { get; } = [AllLevels, "信息", "警告", ErrorLevel];
    public ICollectionView Entries { get; }
    public string LimitLabel => $"最多保留 {Capacity:N0} 条";
    public int ErrorCount => _entries.Count(entry => entry.Level == LogLevel.Error);
    public bool HasResults => !Entries.IsEmpty;
    public string CountLabel => $"{((ListCollectionView)Entries).Count} 条记录";
    public string EmptyLabel => _entries.Count > 0 ? "没有符合当前筛选条件的记录" : "尚无运行日志";
    public string EmptyHint => "可调整来源、级别或搜索词。";
    public string? SourceNotice => _data.GetLogSourceNotice(Source);
    public bool HasSourceNotice => SourceNotice is not null;
    public bool HasSelection => Selected is not null;

    public void ShowErrors()
    {
        Source = AllSources;
        Query = "";
        Level = ErrorLevel;
    }

    public void ShowScript(ScriptItem script)
    {
        Query = "";
        Level = AllLevels;
        Source = script.Installation is { } program
            ? _sourceIds.FirstOrDefault(pair => pair.Value == program.Id).Key ?? AllSources : script.Name;
    }

    private IReadOnlyList<string> BuildSources()
    {
        _sourceIds.Clear();
        if (_data.IsDemo) return [AllSources, "BAM", .. _data.Scripts.Select(item => item.Name)];
        var installations = _data.Scripts.Where(item => item.Installation is not null)
            .Select(item => (Id: item.Installation!.Id, item.Name))
            .Concat(_data.Logs.Where(item => item.InstallationId.HasValue)
                .Select(item => (Id: item.InstallationId!.Value, Name: item.Source))).DistinctBy(item => item.Id).ToArray();
        foreach (var group in installations.GroupBy(item => item.Name))
        {
            foreach (var (id, name) in group)
            {
                var label = group.Count() > 1 || name is AllSources or "BAM" ? $"{name} · {id.ToString("N")[..8]}" : name;
                if (!_sourceIds.TryAdd(label, id)) _sourceIds.Add($"{name} · {id:N}", id);
            }
        }
        return [AllSources, "BAM", .. _sourceIds.Keys];
    }

    private void UpdateLive()
    {
        if (ReferenceEquals(_snapshot, _data.Logs)) return;
        _snapshot = _data.Logs;
        var previousSource = Source;
        var id = _sourceIds.GetValueOrDefault(previousSource);
        var sources = BuildSources();
        if (!Sources.SequenceEqual(sources))
        {
            Sources = sources;
            OnPropertyChanged(nameof(Sources));
            Source = id != Guid.Empty ? _sourceIds.FirstOrDefault(pair => pair.Value == id).Key ?? AllSources :
                sources.Contains(previousSource) ? previousSource : AllSources;
        }
        var alreadyPresent = _entries.Count > 0 ? Array.IndexOf(_snapshot.ToArray(), _entries[0]) : -1;
        if (alreadyPresent < 0)
        {
            _entries.Clear();
            foreach (var entry in _snapshot.Take(Capacity)) _entries.Add(entry);
        }
        else
        {
            for (var index = alreadyPresent - 1; index >= 0; index--) _entries.Insert(0, _snapshot[index]);
            while (_entries.Count > Capacity) _entries.RemoveAt(_entries.Count - 1);
        }
        Refresh();
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(EmptyLabel));
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
        return (Source == AllSources || (_sourceIds.TryGetValue(Source ?? "", out var id)
                ? entry.InstallationId == id : Source == entry.Source && entry.InstallationId is null))
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
