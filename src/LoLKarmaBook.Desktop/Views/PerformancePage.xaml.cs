using System.Windows;
using System.Windows.Controls;
using LoLKarmaBook.Desktop.ViewModels;

namespace LoLKarmaBook.Desktop.Views;

/// <summary>
/// Hosts the Riot API key field. WPF's PasswordBox deliberately has no bindable
/// Password (to keep the plaintext out of the binding system), so this page copies
/// it one way into the view model. The value is never read back out of the view
/// model, so a redraw cannot show it again.
/// </summary>
public partial class PerformancePage : UserControl
{
    public PerformancePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not PerformanceViewModel viewModel)
        {
            return;
        }

        // A key restored from DPAPI after restart fills the box once; typing
        // later flows through PasswordChanged below.
        if (ApiKeyBox.Password.Length == 0 && viewModel.ApiKey.Length > 0)
        {
            ApiKeyBox.Password = viewModel.ApiKey;
        }
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is PerformanceViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.ApiKey = box.Password;
        }
    }
}
