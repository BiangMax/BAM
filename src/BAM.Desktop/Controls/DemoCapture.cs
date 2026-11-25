#if BAM_DEMO
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BAM.Desktop.Controls;

// Captures the live WPF visual tree, including the client-side title bar.
// The desktop compositor and other windows are outside this image.
internal sealed class DemoCapture : IDisposable
{
    private const int CaptureMessage = 0x8042;
    private static readonly string[] Names =
    [
        "01-dashboard-dark", "02-dashboard-light", "03-dashboard-narrow", "04-scripts",
        "05-logs", "06-settings", "07-dashboard-memory", "08-empty", "09-narrow-cards"
    ];

    private readonly FrameworkElement _shell;
    private readonly HwndSource _source;
    private readonly string _directory;
    private DispatcherOperation? _scheduled;

    public DemoCapture(Window window, FrameworkElement shell, string directory)
    {
        _shell = shell;
        _directory = directory;
        _source = (HwndSource)PresentationSource.FromVisual(window);
        _source.AddHook(OnMessage);
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var index = wParam.ToInt64();
        if (message != CaptureMessage || index < 1 || index > Names.Length) return IntPtr.Zero;
        handled = true;
        _scheduled?.Abort();
        _scheduled = _shell.Dispatcher.InvokeAsync(() => Capture(Names[index - 1]), DispatcherPriority.ApplicationIdle);
        return IntPtr.Zero;
    }

    private void Capture(string name)
    {
        if (!_shell.IsVisible || _shell.ActualWidth <= 0 || _shell.ActualHeight <= 0) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(_shell);
            var size = _shell.RenderSize;
            var bounds = new Rect(size);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle((Brush)_shell.FindResource("Bam.Rail"), null, bounds);
                drawing.DrawRectangle(new VisualBrush(_shell), null, bounds);
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX),
                (int)Math.Ceiling(size.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, name + ".png");
            using (var stream = File.Create(path + ".tmp")) encoder.Save(stream);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("BAM screenshot could not be saved.");
        }
    }

    public void Dispose()
    {
        _scheduled?.Abort();
        _source.RemoveHook(OnMessage);
    }
}
#endif
