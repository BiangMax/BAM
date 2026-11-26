using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BAM.Core;
using BAM.Desktop.Controls;
using BAM.Desktop.Data;
using BAM.Desktop.Settings;
using BAM.Desktop.ViewModels;
using BAM.Desktop.Views;
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
                foreach (var theme in new[] { "Dark", "Light" })
                {
                    var palette = new ResourceDictionary { Source = new Uri($"/BAM;component/Themes/{theme}.xaml", UriKind.Relative) };
                    app.Resources.MergedDictionaries.Add(palette);
                    foreach (var dpi in new[] { 96, 144 })
                    {
                        AssertCentered(new Button { Content = "打开窗口", Style = (Style)app.FindResource("Bam.PrimaryButton") }, dpi);
                        AssertCentered(new Button { Content = "启动模拟器和软件" }, dpi);
                        AssertCentered(new RadioButton { Content = "内存", Style = (Style)app.FindResource("Bam.Segment") }, dpi);
                    }
                    AssertStatusSpacing(260);
                    AssertStatusSpacing(340);
                    VerifyPageLayout();
                    VerifyDashboardFirstScreen();
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
        public bool HasResourceSource => false;
        public IReadOnlyList<ScriptItem> Scripts { get; init; } = [];
        public IReadOnlyList<LogEntry> Logs { get; init; } = [];
        public string? GetLogSourceNotice(string source) => null;
        public ResourceFrame? ReadResources(DateTimeOffset time) => null;
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
