using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using BAM.Desktop.Data;

namespace BAM.Desktop.Controls;

internal sealed class ResourceChart : FrameworkElement
{
    public static readonly DependencyProperty HistoryProperty = Register(nameof(History), typeof(ResourceHistory));
    public static readonly DependencyProperty RevisionProperty = Register(nameof(Revision), typeof(int), 0);
    public static readonly DependencyProperty ShowMemoryProperty = Register(nameof(ShowMemory), typeof(bool), false);
    public static readonly DependencyProperty AxisBrushProperty = Register(nameof(AxisBrush), typeof(Brush));
    public static readonly DependencyProperty GridBrushProperty = Register(nameof(GridBrush), typeof(Brush));
    public static readonly DependencyProperty CpuBrushProperty = Register(nameof(CpuBrush), typeof(Brush));
    public static readonly DependencyProperty MemoryBrushProperty = Register(nameof(MemoryBrush), typeof(Brush));
    private static readonly DependencyPropertyKey InspectionPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Inspection), typeof(string), typeof(ResourceChart), new PropertyMetadata(null));
    public static readonly DependencyProperty InspectionProperty = InspectionPropertyKey.DependencyProperty;

    public ResourceHistory? History { get => (ResourceHistory?)GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public bool ShowMemory { get => (bool)GetValue(ShowMemoryProperty); set => SetValue(ShowMemoryProperty, value); }
    public Brush AxisBrush { get => (Brush)GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush CpuBrush { get => (Brush)GetValue(CpuBrushProperty); set => SetValue(CpuBrushProperty, value); }
    public Brush MemoryBrush { get => (Brush)GetValue(MemoryBrushProperty); set => SetValue(MemoryBrushProperty, value); }
    public string? Inspection => (string?)GetValue(InspectionProperty);

    private Rect Plot => new(44, 9, Math.Max(0, ActualWidth - 56), Math.Max(0, ActualHeight - 35));
    private ResourcePoint? _cursor;
    private double _memoryMaximum = 512;

    private static DependencyProperty Register(string name, Type type, object? defaultValue = null)
        => DependencyProperty.Register(name, type, typeof(ResourceChart),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext drawing)
    {
        if (!IsVisible) return;
        var plot = Plot;
        if (plot.Width < 50 || plot.Height < 24) return;

        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var grid = new Pen(GridBrush, 1);
        var line = new Pen(ShowMemory ? MemoryBrush : CpuBrush, 1.8)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
        };
        var maximum = ShowMemory ? MemoryMaximum() : 100;
        for (var index = 0; index <= 4; index++)
        {
            var fraction = index / 4d;
            var y = plot.Top + fraction * plot.Height;
            var x = plot.Left + fraction * plot.Width;
            drawing.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            drawing.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var tick = Text((maximum * (1 - fraction)).ToString("0") + (ShowMemory ? "" : "%"));
            drawing.DrawText(tick, new Point(plot.Left - tick.Width - 8, y - tick.Height / 2));
            var seconds = ResourceHistory.WindowSeconds - 30 * index;
            var label = Text(seconds == 0 ? "现在" : $"−{seconds} 秒");
            drawing.DrawText(label, new Point(Math.Min(x - label.Width / 2, RenderSize.Width - label.Width), plot.Bottom + 7));
        }

        if (History is not { Count: > 0 } history) return;
        var segment = new List<Point>(history.Count);
        var fill = line.Brush.CloneCurrentValue();
        fill.Opacity = SystemParameters.HighContrast ? 0 : 0.11;
        FillSegments(drawing, history, plot, maximum, line, fill, segment);
        if (_cursor is { } cursor && cursor.Time >= history[0].Time)
            DrawCursor(drawing, cursor, plot, maximum, line);
    }

    private void FillSegments(DrawingContext drawing, ResourceHistory history, Rect plot, double maximum,
        Pen line, Brush fill, List<Point> segment)
    {
        DateTimeOffset? previous = null;
        for (var index = 0; index < history.Count; index++)
        {
            var point = history[index];
            var value = Value(point);
            if (value is null || (previous is { } time && point.Time - time > TimeSpan.FromSeconds(2.5)))
            {
                DrawSegment(drawing, plot, line, fill, segment);
                segment.Clear();
            }
            if (value is { } known) segment.Add(Position(point, known, plot, maximum));
            previous = point.Time;
        }
        DrawSegment(drawing, plot, line, fill, segment);
    }

    private static void DrawSegment(DrawingContext drawing, Rect plot, Pen line, Brush fill, List<Point> segment)
    {
        if (segment.Count == 0) return;
        if (segment.Count == 1)
        {
            drawing.DrawEllipse(line.Brush, null, segment[0], 2, 2);
            return;
        }

        var stroke = new StreamGeometry();
        using (var geometry = stroke.Open())
        {
            geometry.BeginFigure(segment[0], false, false);
            for (var index = 1; index < segment.Count; index++) geometry.LineTo(segment[index], true, true);
        }
        var area = new StreamGeometry();
        using (var geometry = area.Open())
        {
            geometry.BeginFigure(new Point(segment[0].X, plot.Bottom), true, true);
            geometry.PolyLineTo(segment, false, false);
            geometry.LineTo(new Point(segment[^1].X, plot.Bottom), false, false);
        }
        drawing.DrawGeometry(fill, null, area);
        drawing.DrawGeometry(null, line, stroke);
    }

    private void DrawCursor(DrawingContext drawing, ResourcePoint point, Rect plot, double maximum, Pen line)
    {
        var position = Position(point, Value(point) ?? 0, plot, maximum);
        drawing.DrawLine(new Pen(AxisBrush, 1) { DashStyle = DashStyles.Dash },
            new Point(position.X, plot.Top), new Point(position.X, plot.Bottom));
        if (Value(point) is not null) drawing.DrawEllipse(line.Brush, null, position, 3, 3);
    }

    private double MemoryMaximum()
    {
        if (History is not { Count: > 0 } history) return _memoryMaximum;
        var peak = 0d;
        for (var index = 0; index < history.Count; index++)
            peak = Math.Max(peak, history[index].MemoryMiB ?? 0);
        if (peak == 0) return _memoryMaximum;
        var target = Math.Max(128, Math.Ceiling(peak * 1.13 / 128) * 128);
        if (peak > _memoryMaximum || target <= _memoryMaximum * 0.72) _memoryMaximum = target;
        return _memoryMaximum;
    }

    private double? Value(ResourcePoint point) => ShowMemory ? point.MemoryMiB : point.Cpu;

    private Point Position(ResourcePoint point, double value, Rect plot, double maximum)
        => new(plot.Right - (History!.End - point.Time).TotalSeconds / ResourceHistory.WindowSeconds * plot.Width,
            plot.Bottom - Math.Clamp(value / maximum, 0, 1) * plot.Height);

    private FormattedText Text(string text) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface((FontFamily)FindResource("Bam.Font"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        11, AxisBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (History is not { Count: > 0 } history || Plot.Width == 0 || !Plot.Contains(args.GetPosition(this)))
        {
            SetCursor(null);
            return;
        }
        var elapsed = (Plot.Right - args.GetPosition(this).X) * ResourceHistory.WindowSeconds / Plot.Width;
        var time = history.End.AddSeconds(-elapsed);
        var closest = 0;
        for (var index = 1; index < history.Count; index++)
            if ((history[index].Time - time).Duration() < (history[closest].Time - time).Duration()) closest = index;
        SetCursor(history[closest]);
    }

    protected override void OnMouseLeave(MouseEventArgs args)
    {
        base.OnMouseLeave(args);
        SetCursor(null);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs args)
    {
        base.OnLostKeyboardFocus(args);
        SetCursor(null);
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (History is not { Count: > 0 } history || args.Key is not (Key.Left or Key.Right)) return;
        var index = history.Count - 1;
        if (_cursor is { } cursor)
        {
            var step = args.Key == Key.Left ? -1 : 1;
            while (index > 0 && history[index].Time > cursor.Time) index--;
            index = Math.Clamp(index + step, 0, history.Count - 1);
        }
        SetCursor(history[index]);
        args.Handled = true;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.Property == ShowMemoryProperty) UpdateInspection();
        if (args.Property == RevisionProperty && _cursor is { } cursor && History is { Count: > 0 } history
            && cursor.Time < history[0].Time)
            SetCursor(null);
    }

    private void SetCursor(ResourcePoint? point)
    {
        if (_cursor == point) return;
        _cursor = point;
        UpdateInspection();
        InvalidateVisual();
    }

    private void UpdateInspection()
    {
        var text = _cursor is { } point
            ? $"{point.Time.ToLocalTime():HH:mm:ss} · " + (Value(point) is double value
                ? ShowMemory ? $"工作集 {value:0} MiB" : $"CPU {value:0.0}%" : "此处缺失样本")
            : null;
        SetValue(InspectionPropertyKey, text);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this);

    private sealed class ChartAutomationPeer(ResourceChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetHelpTextCore() => owner.Inspection ?? base.GetHelpTextCore();
    }
}
