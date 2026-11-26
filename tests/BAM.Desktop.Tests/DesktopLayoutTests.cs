using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BAM.Adapters;
using BAM.Core;
using BAM.Desktop.Controls;
using BAM.Desktop.Data;
using BAM.Desktop.Settings;
using BAM.Desktop.ViewModels;
using BAM.Desktop.Views;
using BAM.Infrastructure;
using Xunit;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace BAM.Desktop.Tests;

public sealed class DesktopLayoutTests
{
    [Fact]
    public async Task ButtonsCenterRenderedTextAndCardsKeepStatusSpacing()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new BAM.Desktop.App();
                app.InitializeComponent();
                foreach (var theme in new[] { "Dark", "Light", "HighContrast" })
                {
                    var palette = new ResourceDictionary { Source = new Uri($"/BAM;component/Themes/{theme}.xaml", UriKind.Relative) };
                    app.Resources.MergedDictionaries.Add(palette);
                    foreach (var dpi in new[] { 96, 120, 144, 192 })
                    {
                        AssertCentered(new Button { Content = "打开窗口", Style = (Style)app.FindResource("Bam.PrimaryButton") }, dpi);
                        AssertCentered(new Button { Content = "启动模拟器和软件" }, dpi);
                        AssertCentered(new RadioButton { Content = "内存", Style = (Style)app.FindResource("Bam.Segment") }, dpi);
                    }
                    AssertStatusSpacing(260);
                    AssertStatusSpacing(340);
                    VerifyPageLayout();
                    VerifyDashboardFirstScreen();
                    VerifyChartEmptyState();
                    VerifyLoopbackAddresses();
                    VerifyProgramEditorInheritance();
                    VerifyPairedCountdownButton();
                    VerifyLogsAtCapacity();
                    app.Resources.MergedDictionaries.Remove(palette);
                }
                app.Shutdown();
                result.SetResult();
            }
            catch (Exception error) { result.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    private static void AssertCentered(ButtonBase button, int dpi)
    {
        const int width = 180;
        const int height = 40;
        button.Background = Brushes.Black;
        button.BorderBrush = Brushes.Black;
        button.Foreground = Brushes.White;
        Layout(button, width, height);
        var scale = dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(button);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var glyph = new List<(int X, int Y)>();
        for (var y = 0; y < bitmap.PixelHeight; y++)
        for (var x = 0; x < bitmap.PixelWidth; x++)
        {
            var index = y * stride + x * 4;
            if (pixels[index] > 150 && pixels[index + 1] > 150 && pixels[index + 2] > 150) glyph.Add((x, y));
        }
        Assert.NotEmpty(glyph);
        var centerX = (glyph.Min(pixel => pixel.X) + glyph.Max(pixel => pixel.X) + 1) / (2 * scale);
        var centerY = (glyph.Min(pixel => pixel.Y) + glyph.Max(pixel => pixel.Y) + 1) / (2 * scale);
        Assert.InRange(centerX, width / 2d - 2, width / 2d + 2);
        Assert.InRange(centerY, height / 2d - 2, height / 2d + 2);
    }

    private static void AssertStatusSpacing(int width)
    {
        var item = new ScriptItem("MaaEnd", "明日方舟：终末地", "2.11.2", SymbolRegular.Apps24, Tone.Accent,
            ProcessState.Running, GameTaskState.Unsupported, "", "", "", "") { CanActivate = true };
        var card = new ScriptCard { DataContext = new ScriptCardViewModel(item), FontSize = 14, UseLayoutRounding = true };
        Layout(card, width, 260);
        Assert.DoesNotContain(FindAll<ToneBadge>(card), badge => badge.ActualWidth == 0);
        var caption = Assert.Single(FindAll<TextBlock>(card), label => label.Text == "软件进程");
        var status = Assert.Single(FindAll<ToneBadge>(card), badge => badge.Text == "已启动");
        Assert.True(status.TranslatePoint(new Point(), card).Y -
            caption.TranslatePoint(new Point(0, caption.ActualHeight), card).Y >= 4);
    }

    private static void VerifyPairedCountdownButton()
    {
        var entry = new LaunchEntry("fixture.exe", "fixture.exe", "fixture", "");
        var program = new ProgramInstallation(Guid.NewGuid(), "MaaEnd", ProgramKind.MaaEnd, entry, "fixture", [entry.TargetPath])
        { Emulator = new(entry, 20) };
        var item = new ScriptItem(program.Name, "游戏", "1.0", SymbolRegular.Apps24, Tone.Accent,
            ProcessState.Stopped, GameTaskState.Unsupported, "", "", "", "")
        { Installation = program, CanStartWithEmulator = true };
        var model = new ScriptCardViewModel(item);
        var card = new ScriptCard { DataContext = model };
        Layout(card, 260, 380);
        var button = Assert.Single(FindAll<Button>(card), control => AutomationProperties.GetAutomationId(control) == "ScriptEmulatorAction");
        Assert.True(button.IsEnabled);
        Assert.True(button.Focusable);
        Assert.True(button.IsTabStop);
        var peer = Assert.IsAssignableFrom<ButtonAutomationPeer>(FrameworkElementAutomationPeer.CreatePeerForElement(button));
        Assert.Equal("启动模拟器和软件", peer.GetName());
        Assert.Equal(item.EmulatorHint, peer.GetHelpText());
        model.Script = item with { Process = ProcessState.Starting, CanStartWithEmulator = false,
            PairedStartup = new(PairedStartupPhase.Waiting, 20) };
        Assert.Equal("20 秒后启动脚本", button.Content);
        Assert.Equal("20 秒后启动脚本", peer.GetName());
        Assert.False(button.IsEnabled);
        model.Script = model.Script with { PairedStartup = new(PairedStartupPhase.Waiting, 1) };
        Assert.Equal("1 秒后启动脚本", button.Content);
        model.Script = model.Script with { PairedStartup = new(PairedStartupPhase.StartingScript) };
        Assert.Equal("正在启动脚本…", button.Content);
        model.Script = model.Script with { PairedStartup = null, Process = ProcessState.Running };
        Assert.Equal("启动模拟器和软件", button.Content);
    }

    private static void VerifyPageLayout()
    {
        var data = new LayoutData();
        var pages = new (UserControl Page, string Heading)[]
        {
            (new DashboardView { DataContext = new DashboardViewModel(data) }, "总览"),
            (new ScriptsView { DataContext = new ScriptsViewModel(data) }, "脚本管理"),
            (new LogsView { DataContext = new LogsViewModel(data) }, "运行日志"),
            (new SettingsView { DataContext = new SettingsViewModel(new(true), new(), null, true, false) }, "设置")
        };
        foreach (var (page, heading) in pages)
        {
            page.FontSize = 14;
            page.UseLayoutRounding = true;
            foreach (var width in new[] { 730, 1000 })
            {
                Layout(page, width, 700);
                Assert.Contains(FindAll<TextBlock>(page), label => label.Text == heading && label.ActualHeight > 0);
            }
        }
    }

    private static void VerifyLogsAtCapacity()
    {
        var data = new LayoutData
        {
            OlderLogsExcluded = 100,
            Logs = Enumerable.Range(0, 2100).Reverse().Select(index =>
                new LogEntry(DateTimeOffset.UnixEpoch.AddSeconds(index), "BAM", LogLevel.Information, $"合成记录 {index}", "")).ToArray()
        };
        var model = new LogsViewModel(data);
        var view = new LogsView { DataContext = model };
        Layout(view, 900, 650);
        Assert.Equal("2000 条记录", model.CountLabel);
        Assert.Contains(FindAll<TextBlock>(view), label => label.Text == model.CountLabel && label.ActualHeight > 0);
        Assert.Contains(FindAll<TextBlock>(view), label => label.Text == model.LimitLabel && label.Text.Contains("已略过 100 条较早记录"));
        model.Query = "合成记录 2099";
        Assert.Equal("合成记录 2099", Assert.IsType<LogEntry>(Assert.Single(model.Entries.Cast<object>())).Message);
        model.Query = "合成记录 0";
        Assert.False(model.HasResults);
    }

    private static void VerifyDashboardFirstScreen()
    {
        var names = new[] { "MaaEnd", "MAA", "BBchannel" };
        var kinds = new[] { ProgramKind.MaaEnd, ProgramKind.Maa, ProgramKind.BBchannel };
        var entry = new LaunchEntry("fixture.exe", "fixture.exe", "fixture", "");
        var data = new LayoutData
        {
            Scripts = names.Select((name, index) => new ScriptItem(name, "游戏", "2.0", SymbolRegular.Apps24, Tone.Accent,
                ProcessState.Running, GameTaskState.Unsupported, "", "", "", "")
            {
                CanActivate = true,
                EmulatorUsage = new(5.0, 2000, "夹具占用"),
                Installation = new(Guid.NewGuid(), name, kinds[index], entry, "fixture", [])
                {
                    Emulator = index == 1 ? null : new(entry, 30)
                }
            }).ToArray(),
            Logs = names.Select((name, index) => new LogEntry(DateTimeOffset.UnixEpoch, name,
                LogLevel.Information, $"已确认软件运行 {index}", "")).ToArray()
        };
        var dashboard = new DashboardView { DataContext = new DashboardViewModel(data), FontSize = 14, UseLayoutRounding = true };
        var scroll = Assert.IsType<ScrollViewer>(dashboard.FindName("OverviewScroll"));
        var preview = Assert.IsType<Border>(dashboard.FindName("EventPreview"));

        Layout(dashboard, 892, 665);
        Assert.Equal(0, scroll.ScrollableHeight);
        Assert.Equal(3, FindAll<ScriptCard>(dashboard).Count());
        AssertPreviewFits(665);

        Layout(dashboard, 736, 460);
        Assert.True(scroll.ScrollableHeight > 0);
        var top = preview.TranslatePoint(new Point(), dashboard).Y;
        scroll.ScrollToBottom();
        dashboard.UpdateLayout();
        Assert.Equal(top, preview.TranslatePoint(new Point(), dashboard).Y);
        AssertPreviewFits(460);

        void AssertPreviewFits(int height)
        {
            var rows = FindAll<TextBlock>(preview).Where(label => label.Text.StartsWith("已确认软件运行", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, rows.Length);
            Assert.All(rows, row => Assert.InRange(row.TranslatePoint(new Point(0, row.ActualHeight), dashboard).Y, 0, height));
        }
    }

    private sealed class LayoutData : IDesktopData
    {
        public bool IsDemo => true;
        public bool HasResourceSource { get; init; }
        public IReadOnlyList<ScriptItem> Scripts { get; init; } = [];
        public IReadOnlyList<LogEntry> Logs { get; init; } = [];
        public long OlderLogsExcluded { get; init; }
        public string? GetLogSourceNotice(string source) => null;
        public ResourceFrame? ReadResources(DateTimeOffset time) => time == DateTimeOffset.UnixEpoch.AddSeconds(1)
            ? new(time, [], 0, null) : time == DateTimeOffset.UnixEpoch.AddSeconds(2) ? new(time, [], null, 64) : null;
    }

    private static void VerifyChartEmptyState()
    {
        var model = new DashboardViewModel(new LayoutData { HasResourceSource = true });
        var view = new DashboardView { DataContext = model };
        var empty = Assert.IsType<Border>(view.FindName("ChartEmptyState"));
        var chart = Assert.IsType<ResourceChart>(view.FindName("ResourcePlot"));
        Layout(view, 900, 650);
        Assert.Equal(Visibility.Visible, empty.Visibility);
        Assert.False(empty.IsHitTestVisible);
        Assert.False(chart.Focusable);

        var start = DateTimeOffset.UnixEpoch;
        model.Refresh(start.AddSeconds(1));
        Assert.Equal(Visibility.Collapsed, empty.Visibility);
        Assert.True(chart.Focusable);
        Assert.Equal($"{0d:0.0}%", model.Cpu);
        model.ShowMemory = true;
        Assert.Equal(Visibility.Visible, empty.Visibility);
        model.Refresh(start.AddSeconds(2));
        Assert.Equal(Visibility.Collapsed, empty.Visibility);
        model.Refresh(start.AddSeconds(122));
        Assert.Equal(Visibility.Collapsed, empty.Visibility);
        model.ShowMemory = false;
        Assert.Equal(Visibility.Visible, empty.Visibility);
        model.ShowMemory = true;
        model.Refresh(start.AddSeconds(123));
        Assert.Equal(Visibility.Visible, empty.Visibility);
    }

    private static void VerifyLoopbackAddresses()
    {
        var host = new LayoutLoopbackHost();
        var model = new MaaLoopbackViewModel(host, TimeProvider.System, CancellationToken.None);
        var view = new SettingsView { DataContext = new SettingsViewModel(new(true), new(), null, true, false, model) };
        Layout(view, 730, 700);
        var addresses = FindAll<TextBox>(view).Where(box => box.IsReadOnly).ToArray();
        Assert.Collection(addresses,
            box => Assert.Equal("http://127.0.0.1:18373/getTask", box.Text),
            box => Assert.Equal("http://127.0.0.1:18373/reportStatus", box.Text));
        model.PortText = "99999";
        Assert.All(addresses, box => Assert.Empty(box.Text));
        model.PortText = "19373";
        Assert.Equal("http://127.0.0.1:19373/reportStatus", addresses[1].Text);
        Assert.True(model.StartAsync("fixture-user", "fixture-device").IsCompletedSuccessfully);
        Assert.Equal(19373, host.RequestedPort);
        Assert.Equal("http://127.0.0.1:19374/getTask", addresses[0].Text);
        Assert.True(model.ShutdownAsync().IsCompletedSuccessfully);
        Assert.Equal("http://127.0.0.1:19373/getTask", addresses[0].Text);
    }

    private static void VerifyProgramEditorInheritance()
    {
        var folder = Path.GetTempPath();
        var target = Path.Combine(folder, "fixture.exe");
        var entry = new LaunchEntry(Path.Combine(folder, "fixture.lnk"), target, folder, "--saved");
        var program = new ProgramInstallation(Guid.NewGuid(), "Fixture", ProgramKind.Generic, entry, folder, [target])
        {
            InheritShortcutArguments = true
        };
        var catalog = new InstallationCatalog(new WindowsProgramFiles());
        using var platform = new WindowsProgramPlatform();
        using var monitor = new ProgramMonitor(catalog, platform, new LocalProgramStore(folder),
            new LocalProgramEventStore(folder), TimeProvider.System);
        using var live = new LiveDesktopData(monitor, catalog);
        var editor = new ProgramEditor(live, program, entry with { Arguments = "--current" });
        try
        {
            var inherit = Assert.IsType<CheckBox>(editor.FindName("InheritArguments"));
            var arguments = Assert.IsType<TextBox>(editor.FindName("LaunchArguments"));
            var directory = Assert.IsType<TextBox>(editor.FindName("WorkDirectory"));
            Assert.Equal(Visibility.Visible, inherit.Visibility);
            Assert.True(inherit.IsChecked);
            Assert.Equal(Visibility.Collapsed, arguments.Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<DockPanel>(directory.Parent).Visibility);
            inherit.IsChecked = false;
            Assert.Equal(Visibility.Visible, arguments.Visibility);
            Assert.Equal("--current", arguments.Text);
        }
        finally { editor.Close(); }
    }

    private sealed class LayoutLoopbackHost : IMaaLoopbackHost
    {
        public int RequestedPort { get; private set; }

        public Task<int> StartAsync(int port, IMaaHeartbeatSession session, CancellationToken cancellation)
        {
            RequestedPort = port;
            return Task.FromResult(port + 1);
        }

        public Task StopAsync(CancellationToken cancellation) => Task.CompletedTask;
    }

    private static void Layout(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindAll<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
