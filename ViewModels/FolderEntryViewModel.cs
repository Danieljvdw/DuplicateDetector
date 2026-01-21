using CommunityToolkit.Mvvm.ComponentModel;

namespace DuplicateDetector.ViewModels;

public partial class FolderEntryViewModel : ObservableObject
{
    // Full folder path
    public string Path { get; }

    // Per-folder file-state filters
    [ObservableProperty] bool showIdle = true;
    [ObservableProperty] bool showHashing = true;
    [ObservableProperty] bool showHashed = true;
    [ObservableProperty] bool showKeep = true;
    [ObservableProperty] bool showDelete = true;
    [ObservableProperty] bool showUnique = true;
    [ObservableProperty] bool showDeleting = true;
    [ObservableProperty] bool showDeleted = true;
    [ObservableProperty] bool showError = true;

    public FolderEntryViewModel(string path)
    {
        Path = path;
    }
}