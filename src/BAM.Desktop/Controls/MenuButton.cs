using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BAM.Desktop.Controls;

internal sealed class MenuButton : Button
{
    protected override void OnClick()
    {
        base.OnClick();
        if (ContextMenu is not { } menu) return;
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
