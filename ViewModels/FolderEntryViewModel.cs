using CommunityToolkit.Mvvm.ComponentModel;

namespace DuplicateDetector.ViewModels;

public partial class FolderEntryViewModel : ObservableObject
{
    // Full folder path
    [ObservableProperty]
    string path;

    // Per-folder file-state filters
    [ObservableProperty] bool showIdle = false;
    [ObservableProperty] bool showHashing = true;
    [ObservableProperty] bool showHashed = true;
    [ObservableProperty] bool showKeep = false;
    [ObservableProperty] bool showDelete = false;
    [ObservableProperty] bool showUnique = false;
    [ObservableProperty] bool showDeleting = true;
    [ObservableProperty] bool showDeleted = true;
    [ObservableProperty] bool showIgnored = true;
    [ObservableProperty] bool showError = true;

    public FolderEntryViewModel(string path)
    {
        Path = path;
    }
}