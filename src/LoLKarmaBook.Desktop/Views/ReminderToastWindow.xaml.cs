using System.Windows;
using System.Windows.Threading;

namespace LoLKarmaBook.Desktop.Views;

/// <summary>
/// Bottom-right "再次相遇" notice. It never takes focus from the game: shown without
/// activation, stays on top, and closes itself after a few seconds.
/// </summary>
public partial class ReminderToastWindow : Window
{
    private const double ScreenMargin = 16;
    private readonly DispatcherTimer _timer;
    private bool _closing;

    public ReminderToastWindow(IReadOnlyList<string> names, int seconds = 12)
    {
        InitializeComponent();
        NamesText.Text = string.Join("、", names);
        Loaded += (_, _) => PlaceBottomRight();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(3, seconds)) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Close();
        };
        Closed += (_, _) =>
        {
            _closing = true;
            _timer.Stop();
        };
    }

    /// <summary>Starts the auto-close countdown.</summary>
    public void StartCountdown() => _timer.Start();

    private void PlaceBottomRight()
    {
        Rect area = SystemParameters.WorkArea;
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : 0;
        Left = area.Right - width - ScreenMargin;
        Top = area.Bottom - height - ScreenMargin;
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        if (!_closing)
        {
            Close();
        }
    }
}
