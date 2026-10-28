using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LoLKarmaBook.Desktop.Views;

public partial class RecordsPage : UserControl
{
    public RecordsPage()
    {
        InitializeComponent();
    }

    private void OnMoreActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
