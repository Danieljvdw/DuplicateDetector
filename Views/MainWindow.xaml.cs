using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DuplicateDetector;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void FilesDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var depObj = (DependencyObject)e.OriginalSource;
        while (depObj != null && !(depObj is DataGridCell))
            depObj = VisualTreeHelper.GetParent(depObj);

        if (depObj is DataGridCell cell && !cell.IsEditing)
        {
            var dataGrid = sender as DataGrid;
            dataGrid?.BeginEdit();
        }
    }
}
