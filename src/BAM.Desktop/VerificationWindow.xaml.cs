using System.Windows;

namespace BAM.Desktop;

public partial class VerificationWindow : Window
{
    public VerificationWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
