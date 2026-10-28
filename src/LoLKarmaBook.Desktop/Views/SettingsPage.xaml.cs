using System.Windows;
using System.Windows.Controls;

namespace LoLKarmaBook.Desktop.Views;

public partial class SettingsPage : UserControl
{
    private bool? _isSingleColumn;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void OnSettingsGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Reflow the same card instances; view-model bindings and expanded state are preserved.
        bool singleColumn = e.NewSize.Width < 820;
        if (_isSingleColumn == singleColumn)
        {
            return;
        }

        _isSingleColumn = singleColumn;
        SettingsGutter.Width = new GridLength(singleColumn ? 0 : 12);
        SettingsRightColumn.Width = new GridLength(singleColumn ? 0 : 360);
        RightSettingsColumn.Visibility = singleColumn ? Visibility.Collapsed : Visibility.Visible;

        LeftSettingsColumn.Children.Clear();
        RightSettingsColumn.Children.Clear();
        if (singleColumn)
        {
            // Keep common preferences and data recovery ahead of optional diagnostics.
            foreach (FrameworkElement card in new FrameworkElement[]
                     { DataPathsCard, PreferencesCard, SnapshotsCard, ProtectionCard, DiagnosticsCard, AboutCard })
            {
                LeftSettingsColumn.Children.Add(card);
            }
        }
        else
        {
            LeftSettingsColumn.Children.Add(DataPathsCard);
            LeftSettingsColumn.Children.Add(PreferencesCard);
            LeftSettingsColumn.Children.Add(DiagnosticsCard);
            RightSettingsColumn.Children.Add(SnapshotsCard);
            RightSettingsColumn.Children.Add(ProtectionCard);
            RightSettingsColumn.Children.Add(AboutCard);
        }
    }
}
