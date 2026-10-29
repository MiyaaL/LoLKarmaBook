using System.Windows;
using LoLKarmaBook.Desktop.ViewModels;

namespace LoLKarmaBook.Desktop.Views;

/// <summary>Edits one stored record without requiring a live match roster.</summary>
public partial class NoteEditWindow : Window
{
    private readonly NoteEditViewModel _viewModel;

    public NoteEditWindow(NoteEditViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Saved += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
