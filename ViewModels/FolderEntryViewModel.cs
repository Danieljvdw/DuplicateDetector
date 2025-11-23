using CommunityToolkit.Mvvm.ComponentModel;

namespace DuplicateDetector.ViewModels;

public partial class FolderEntryViewModel : ObservableObject
{
    // Full folder path
    public string Path { get; }

    // Whether this folder is visible in the file list
    [ObservableProperty]
    bool isVisible = true;

    // Per-folder file-state filters 
    [ObservableProperty] bool showKeep = true;
    [ObservableProperty] bool showDelete = true;
    [ObservableProperty] bool showUnique = true;
    [ObservableProperty] bool showHashing = true;
    [ObservableProperty] bool showError = true;

    public FolderEntryViewModel(string path)
    {
        Path = path;
    }
}