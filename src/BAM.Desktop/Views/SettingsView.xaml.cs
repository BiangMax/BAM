using System.Windows.Controls;
using System.Windows;
using BAM.Desktop.ViewModels;
namespace BAM.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void StartMaaLoopback(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel { Loopback: { } loopback }) return;
        var user = MaaUser.Password;
        var device = MaaDevice.Password;
        MaaUser.Clear();
        MaaDevice.Clear();
        await loopback.StartAsync(MaaPort.Text, user, device);
    }
}
