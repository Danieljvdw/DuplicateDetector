using DuplicateDetector.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DuplicateDetector;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is FolderEntryViewModel folderPath)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Folders.Remove(folderPath);
            }
        }
    }

    private void StateCell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border &&
              border.DataContext is FileEntryViewModel file)
        {
            // Only toggle between keep <-> delete
            if (file.State == FileEntryViewModel.FileState.keep)
            {
                file.State = FileEntryViewModel.FileState.delete;
            }
            else if (file.State == FileEntryViewModel.FileState.delete)
            {
                file.State = FileEntryViewModel.FileState.keep;
            }

            // Optional: prevent DataGrid row selection from stealing the click
            e.Handled = true;
        }
    }

    private void FolderVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.FilesView.Refresh();
    }
}
