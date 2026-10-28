using System.Windows;
using System.Windows.Input;
using LoLKarmaBook.Desktop.ViewModels;

namespace LoLKarmaBook.Desktop.Views;

/// <summary>
/// Bottom-right end-of-match card. It never takes focus from the game: the window
/// is shown without activation, stays on top, and closes back to the main window
/// without touching notes unless the user pressed 「保存记录」.
/// </summary>
public partial class EndOfGameWindow : Window
{
    private const double ScreenMargin = 16;

    public EndOfGameWindow(EndOfGameViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => PlaceBottomRight();
    }

    private void PlaceBottomRight()
    {
        Rect area = SystemParameters.WorkArea;
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : MinHeight;
        Left = area.Right - width - ScreenMargin;
        Top = area.Bottom - height - ScreenMargin;
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EndOfGameViewModel viewModel) viewModel.DismissCommand.Execute(null);
        Close();
    }
}
