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
        if (sender is Button btn && btn.CommandParameter is string folderPath)
        {
            if (DataContext is DuplicateDetector.ViewModels.MainViewModel vm)
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
}
