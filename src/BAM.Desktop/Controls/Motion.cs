using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace BAM.Desktop.Controls;

internal static class Motion
{
    private static readonly DependencyPropertyDescriptor Pressed = DependencyPropertyDescriptor.FromProperty(
        ButtonBase.IsPressedProperty, typeof(ButtonBase));
    public static readonly DependencyProperty HoverFeedbackProperty = DependencyProperty.RegisterAttached(
        "HoverFeedback", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFeedbackChanged));

    public static void SetHoverFeedback(DependencyObject element, bool value) => element.SetValue(HoverFeedbackProperty, value);
    public static bool GetHoverFeedback(DependencyObject element) => (bool)element.GetValue(HoverFeedbackProperty);

    public static bool Reduce { get; set; }
    private static bool Enabled => !Reduce && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    public static void ShowPage(FrameworkElement page)
    {
        if (page.IsVisible)
            Fade(page, 1, "Bam.PageTransitionMs", Enabled ? 0.72 : null);
    }

    private static void OnFeedbackChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement layer) return;
        if ((bool)args.NewValue)
        {
            layer.Loaded += Connect;
            layer.Unloaded += Disconnect;
        }
        else
        {
            layer.Loaded -= Connect;
            layer.Unloaded -= Disconnect;
            Disconnect(layer, new RoutedEventArgs());
        }
    }

    private static void Connect(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement layer || layer.TemplatedParent is not ButtonBase button) return;
        button.MouseEnter += MouseChanged;
        button.MouseLeave += MouseChanged;
        Pressed.AddValueChanged(button, PressChanged);
        layer.IsVisibleChanged += VisibilityChanged;
        Update(button);
    }

    private static void Disconnect(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement layer || layer.TemplatedParent is not ButtonBase button) return;
        button.MouseEnter -= MouseChanged;
        button.MouseLeave -= MouseChanged;
        Pressed.RemoveValueChanged(button, PressChanged);
        layer.IsVisibleChanged -= VisibilityChanged;
        layer.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private static void MouseChanged(object sender, System.Windows.Input.MouseEventArgs args) => Update((ButtonBase)sender);
    private static void PressChanged(object? sender, EventArgs args) => Update((ButtonBase)sender!);

    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement { TemplatedParent: ButtonBase button }) Update(button);
    }

    private static void Update(ButtonBase button)
    {
        if (button.Template?.FindName("Hover", button) is FrameworkElement layer)
            Fade(layer, button.IsEnabled && button.IsVisible && (button.IsMouseOver || button.IsPressed)
                ? (button.IsPressed ? 0.18 : 0.10) : 0, "Bam.FeedbackMs");
    }

    private static void Fade(FrameworkElement element, double to, string durationResource, double? from = null)
    {
        var start = from ?? element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = to;
        if (Enabled && element.IsVisible && Math.Abs(start - to) > 0.001)
        {
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(start, to,
                TimeSpan.FromMilliseconds((double)element.FindResource(durationResource))) { FillBehavior = FillBehavior.Stop });
        }
    }
}
