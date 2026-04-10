using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateDetector.Custom;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace DuplicateDetector.ViewModels;

public partial class MainViewModel : ObservableObject
{

    //============================================================
    // 🧩 ENUMS & STATIC MEMBERS
    //============================================================

    // Supported hashing / comparison algorithms
    public enum HashingAlgorithm
    {
        [Description("CRC32     collision possible for millions of files")]
        Crc32,

        [Description("MD5       ~1 in 10¹⁷ accidental collision")]
        MD5,

        [Description("SHA256    ~1 in 10⁷⁵ accidental collision")]
        SHA256,

        [Description("SHA512    ~1 in 10¹⁵⁰ accidental collision")]
        SHA512
    }

    // Expose list of hashing algorithms for UI binding
    public static Array HashingAlgorithms => Enum.GetValues(typeof(HashingAlgorithm));

    public enum FolderComparisonMode
    {
        [Description("All                      File is compared to all files in all selected folders including sub-folders")]
        All,

        [Description("Same Folder              File is only compared to files in the same folder as itself excluding sub-folders")]
        SameFolder,

        [Description("Different Folder         File is only compared to files in different selected folders")]
        DifferentFolder,

        [Description("Same User Folder         File is only compared to files in the same user's folder including sub-folders")]
        SameUserFolder,

        [Description("Different User Folder    File is only compared to files in different user's folders including sub-folders")]
        DifferentUserFolder
    }

    public static Array FolderComparisonModes => Enum.GetValues(typeof(FolderComparisonMode));

    //============================================================
    // 📦 FOLDER & FILE DATA
    //============================================================

    // List of selected folder paths
    [ObservableProperty] ObservableCollection<FolderEntryViewModel> folders = [];

    private void SaveFolders()
    {
        var sc = new StringCollection();

        foreach (var path in Folders.Select(f => f.Path))
            sc.Add(path);

        Properties.UserSettings.Default.Folders = sc;
        Properties.UserSettings.Default.Save();
    }

    private void Folders_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // save folders
        SaveFolders();

        // update can execute for folder moving buttons
        MoveFolderUpCommand.NotifyCanExecuteChanged();
        MoveFolderDownCommand.NotifyCanExecuteChanged();

        // recalculate all headers as a folder has been added/removed
        RecalculateIdleHeader();
        RecalculateHashingHeader();
        RecalculateHashedHeader();
        RecalculateKeepHeader();
        RecalculateDeleteHeader();
        RecalculateUniqueHeader();
        RecalculateDeletingHeader();
        RecalculateDeletedHeader();
        RecalculateIgnoredHeader();
        RecalculateErrorHeader();
    }

    // Collection of all scanned file entries
    [ObservableProperty] ThrottledObservableCollection<FileEntryViewModel> files = [];

    // View for the file list with sorting and filtering
    public VirtualizedFileList FilesView { get; private set; } = new VirtualizedFileList([]);

    //============================================================
    // ⚙️ APP SETTINGS & PARAMETERS
    //============================================================

    // Currently selected hashing algorithm
    [ObservableProperty] HashingAlgorithm selectedHashingAlgorithm = HashingAlgorithm.SHA512;

    partial void OnSelectedHashingAlgorithmChanged(HashingAlgorithm oldValue, HashingAlgorithm newValue)
    {
        Properties.UserSettings.Default.HashingAlgorithm = newValue.ToString();
        Properties.UserSettings.Default.Save();
    }

#if false // want to enable storing hashes in file for low RAM usage
    [ObservableProperty] bool useHashFile;
#endif

    // Whether to compare files only across different folders
    [ObservableProperty] FolderComparisonMode selectedFolderComparisonMode = FolderComparisonMode.All;

    partial void OnSelectedFolderComparisonModeChanged(FolderComparisonMode oldValue, FolderComparisonMode newValue)
    {
        Properties.UserSettings.Default.FolderComparisonMode = newValue.ToString();
        Properties.UserSettings.Default.Save();
    }

    // Comparison criteria
    [ObservableProperty] bool compareSize = true;
    [ObservableProperty] bool compareDateModified = false;
    [ObservableProperty] bool compareContent = false;
    [ObservableProperty] bool compareHash = true;
    [ObservableProperty] bool compareDisk = false;
    [ObservableProperty] bool comparePath = false;
    [ObservableProperty] bool compareFilenameExact = false;
    [ObservableProperty] bool compareFilenameSimilar = false;
    [ObservableProperty] bool compareExtension = false;
    [ObservableProperty] int ignoreSize = 0; // 0 means no size ignored, otherwise files smaller than or equal to this size in bytes are ignored

    partial void OnCompareSizeChanged(bool oldValue, bool newValue)
    {
        Properties.UserSettings.Default.CompareSize = newValue;
        Properties.UserSettings.Default.Save();
    }

    partial void OnCompareDateModifiedChanged(bool oldValue, bool newValue)
    {
        Properties.UserSettings.Default.CompareDateModified = newValue;
        Properties.UserSettings.Default.Save();
    }

    partial void OnCompareContentChanged(bool oldValue, bool newValue)
    {
        Properties.UserSettings.Default.CompareContent = newValue;
        Properties.UserSettings.Default.Save();
    }

    partial void OnCompareHashChanged(bool oldValue, bool newValue)
    {
        Properties.UserSettings.Default.CompareHash = newValue;
        Properties.UserSettings.Default.Save();
    }

    partial void OnCompareDiskChanged(bool oldValue, bool newValue)
    {
        Properties.UserSettings.Default.CompareDisk = newValue;
        Properties.UserSettings.Default.Save();

        UpdateCanEnableCompares();
    }

    partial void OnComparePathChanged(bool oldValue, bool newValue)
    {
        Properties.UserSettings.Default.ComparePath = newValue;
        Properties.UserSettings.Default.Save();

        UpdateCanEnableCompares();
    }

    partial void OnCompareFilenameExactChanged(bool oldValue, bool newValue)
    {
        Properties.UserSettings.Default.CompareFilenameExact = newValue;
        Properties.UserSettings.Default.Save();

        UpdateCanEnableCompares();
    }

    partial void OnCompareFilenameSimilarChanged(bool oldValue, bool newValue)
    {
        Properties.UserSettings.Default.CompareFilenameSimilar = newValue;
        Properties.UserSettings.Default.Save();

        UpdateCanEnableCompares();
    }

    partial void OnCompareExtensionChanged(bool oldValue, bool newValue)
    {
        Properties.UserSettings.Default.CompareExtension = newValue;
        Properties.UserSettings.Default.Save();

        UpdateCanEnableCompares();
    }

    partial void OnIgnoreSizeChanged(int oldValue, int newValue)
    {
        Properties.UserSettings.Default.IgnoreSize = newValue;
        Properties.UserSettings.Default.Save();
    }

    // Whether the compare buttons can be enabled based on current state and other selected criteria (to prevent selecting incompatible criteria)
    public bool CanEnableDisk => CanRunOperations && (CompareDisk || !(ComparePath && (CompareFilenameExact || CompareFilenameSimilar) && CompareExtension));
    public bool CanEnablePath => CanRunOperations && (ComparePath || !(CompareDisk && (CompareFilenameExact || CompareFilenameSimilar) && CompareExtension));
    public bool CanEnableFilenameExact => CanRunOperations && (CompareFilenameExact || !((CompareDisk && ComparePath && CompareExtension) || CompareFilenameSimilar));
    public bool CanEnableFilenameSimilar => CanRunOperations && (CompareFilenameSimilar || !((CompareDisk && ComparePath && CompareExtension) || CompareFilenameExact));
    public bool CanEnableExtension => CanRunOperations && (CompareExtension || !(CompareDisk && ComparePath && (CompareFilenameExact || CompareFilenameSimilar)));

    // update can execute for compare criteria checkboxes when relevant criteria change
    private void UpdateCanEnableCompares()
    {
        OnPropertyChanged(nameof(CanEnableDisk));
        OnPropertyChanged(nameof(CanEnablePath));
        OnPropertyChanged(nameof(CanEnableFilenameExact));
        OnPropertyChanged(nameof(CanEnableFilenameSimilar));
        OnPropertyChanged(nameof(CanEnableExtension));
    }


    // Optional filter to display only certain file states
    [ObservableProperty] FileEntryViewModel.FileState? fileStateFilter = null;

    // Max degree of parallelism
    [ObservableProperty] int maxThreads = Environment.ProcessorCount * 8;

    //============================================================
    // 🔄 STATE & PROGRESS
    //============================================================

    public enum OperationState
    {
        Idle,
        Running,
        Paused,
        Cancelling,
        Cancelled,
        Completed,
        Error
    }

    [ObservableProperty] OperationState currentState = OperationState.Idle; // Current operation state
    [ObservableProperty] double progressValue = 0.0d; // Progress bar value (0–100%)
    [ObservableProperty] string etaText = "ETA: idle";
    [ObservableProperty] string runtimeText = "Run Time: 00:00:00";

    private double lastProgressValue = 0; // to throttle progress updates
    private readonly object progressLock = new(); // lock for progress updates
    private DateTime operationStarted = DateTime.UtcNow; // time when current operation started
    private ManualResetEventSlim pauseEvent = new(true); // for pausing operations
    private CancellationTokenSource cts = new CancellationTokenSource(); // For cancelling asynchronous operations

    private bool CanCancel => CurrentState == OperationState.Running || CurrentState == OperationState.Paused; // Whether current operation can be cancelled
    private bool CanPause => CurrentState == OperationState.Running; // Whether current operation can be paused
    private bool CanResume => CurrentState == OperationState.Paused; // Whether current operation can be resumed

    public bool CanRunOperations => CurrentState == OperationState.Idle || CurrentState == OperationState.Completed || CurrentState == OperationState.Cancelled || CurrentState == OperationState.Error; // Whether operations can be started

    partial void OnCurrentStateChanged(OperationState oldValue, OperationState newValue)
    {
        OnPropertyChanged(nameof(CanRunOperations));

        // update can execute for folder moving buttons
        MoveFolderUpCommand.NotifyCanExecuteChanged();
        MoveFolderDownCommand.NotifyCanExecuteChanged();

        // update can execute for compare criteria checkboxes
        UpdateCanEnableCompares();
    }

    //============================================================
    // 🧮 STATISTICS
    //============================================================

    public long TotalData => Files.Sum(f => f.Size); // Total bytes of all files
    public long IdleData => Files.Where(f => f.State == FileEntryViewModel.FileState.idle).Sum(f => f.Size); // Bytes of files not yet processed
    public long UniqueData => Files.Where(f => f.State == FileEntryViewModel.FileState.unique).Sum(f => f.Size); // Total bytes of all unique files
    public long DeleteData => Files.Where(f => f.State == FileEntryViewModel.FileState.delete || f.State == FileEntryViewModel.FileState.deleting || f.State == FileEntryViewModel.FileState.deleted).Sum(f => f.Size); // Bytes that would be freed by deleting duplicates
    public long KeepData => Files.Where(f => f.State == FileEntryViewModel.FileState.keep).Sum(f => f.Size); // Bytes marked to keep (including unique + manually kept files)
    public long IgnoredData => Files.Where(f => f.State == FileEntryViewModel.FileState.ignored).Sum(f => f.Size); // Bytes of files ignored due to being smaller than or equal to ignore size
    public long ErrorData => Files.Where(f => f.State == FileEntryViewModel.FileState.error).Sum(f => f.Size); // Bytes of files that had errors during processing
    public long OtherData => TotalData - IdleData - UniqueData - DeleteData - KeepData - IgnoredData - ErrorData; // Bytes of files with other states
    public long TotalAfterDeleteData => TotalData - DeleteData; // Bytes that will remain after deleting files marked for deletion

    public long TotalFiles => Files.Count; // Total number of files
    public long IdleFiles => Files.Count(f => f.State == FileEntryViewModel.FileState.idle); // Number of files not yet processed
    public long UniqueFiles => Files.Count(f => f.State == FileEntryViewModel.FileState.unique); // Number of unique files
    public long DeleteFiles => Files.Count(f => f.State == FileEntryViewModel.FileState.delete); // Number of files marked for deletion
    public long KeepFiles => Files.Count(f => f.State == FileEntryViewModel.FileState.keep); // Number of files marked to keep
    public long IgnoredFiles => Files.Count(f => f.State == FileEntryViewModel.FileState.ignored); // Number of files ignored due to being smaller than or equal to ignore size
    public long ErrorFiles => Files.Count(f => f.State == FileEntryViewModel.FileState.error); // Number of files that had errors during processing
    public long OtherFiles => TotalFiles - IdleFiles - UniqueFiles - DeleteFiles - KeepFiles - IgnoredFiles - ErrorFiles; // Number of files with other states
    public long TotalAfterDeleteFiles => Files.Count(f => f.State != FileEntryViewModel.FileState.delete && f.State != FileEntryViewModel.FileState.deleting && f.State != FileEntryViewModel.FileState.deleted); // Number of files remaining after deletion

    // for stacked chart displaying percentages
    public double KeepPercentageData => TotalData > 0 ? (double)KeepData / TotalData : 0;
    public double DeletePercentageData => TotalData > 0 ? (double)DeleteData / TotalData : 0;
    public double UniquePercentageData => TotalData > 0 ? (double)UniqueData / TotalData : 0;

    public double KeepPercentageFiles => TotalFiles > 0 ? (double)KeepFiles / TotalFiles : 0;
    public double DeletePercentageFiles => TotalFiles > 0 ? (double)DeleteFiles / TotalFiles : 0;
    public double UniquePercentageFiles => TotalFiles > 0 ? (double)UniqueFiles / TotalFiles : 0;

    public DiskViewModel DiskViewModel { get; } = new DiskViewModel();

    //============================================================
    // 🧱 VERSION INFO
    //============================================================

    public string Version
    {
        get
        {
            // get version from assembly info
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version == null)
            {
                return "unknown";
            }

            // Take Major, Minor, Build (skip Revision)
            return $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    //============================================================
    // 🧠 CONSTRUCTOR & INITIALIZATION
    //============================================================

    public MainViewModel()
    {
        // load settings
        LoadSettings();
    }

    private void LoadSettings()
    {
        // load hashing algorithm
        SelectedHashingAlgorithm = Enum.TryParse<HashingAlgorithm>(Properties.UserSettings.Default.HashingAlgorithm, out var algo) ? algo : HashingAlgorithm.SHA512;

        // load folder comparison mode
        SelectedFolderComparisonMode = Enum.TryParse<FolderComparisonMode>(Properties.UserSettings.Default.FolderComparisonMode, out var mode) ? mode : FolderComparisonMode.All;

        // load folder list
        var savedFolders = Properties.UserSettings.Default.Folders;
        if (savedFolders != null)
        {
            foreach (string? path in savedFolders)
            {
                if (Directory.Exists(path))
                {
                    var folder = new FolderEntryViewModel(path);
                    Folders.Add(folder);
                    folder.PropertyChanged += Folder_PropertyChanged;
                }
            }
        }

        // attach collection changed handler to save changes to Folders
        Folders.CollectionChanged += Folders_CollectionChanged;

        // load comparison settings
        CompareSize = Properties.UserSettings.Default.CompareSize;
        CompareDateModified = Properties.UserSettings.Default.CompareDateModified;
        CompareContent = Properties.UserSettings.Default.CompareContent;
        CompareHash = Properties.UserSettings.Default.CompareHash;
        CompareDisk = Properties.UserSettings.Default.CompareDisk;
        ComparePath = Properties.UserSettings.Default.ComparePath;
        CompareFilenameExact = Properties.UserSettings.Default.CompareFilenameExact;
        CompareFilenameSimilar = Properties.UserSettings.Default.CompareFilenameSimilar;
        CompareExtension = Properties.UserSettings.Default.CompareExtension;
        IgnoreSize = Properties.UserSettings.Default.IgnoreSize;
    }

    //============================================================
    // 🧩 COMMANDS — FOLDER MANAGEMENT
    //============================================================

    private bool CheckFolderValid(string filename)
    {
        // Check that folder is not inside any existing folder
        if (Folders.Any(f => IsSubfolder(filename, f.Path)))
        {
            MessageBox.Show("The selected folder is already inside an existing folder in the list.",
                        "Cannot Add Folder",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
            return false; // Do not add because it's inside an existing folder
        }

        // Check that no existing folder is inside the newly selected folder
        if (Folders.Any(f => IsSubfolder(f.Path, filename)))
        {
            MessageBox.Show("An existing folder in the list is inside the folder you selected.",
                       "Cannot Add Folder",
                       MessageBoxButton.OK,
                       MessageBoxImage.Warning);
            return false; // Do not add because an existing folder is inside it
        }

        // avoid adding duplicates
        if (Folders.Any(f => f.Path.Equals(filename, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("The selected folder is already in the list.",
                      "Cannot Add Folder",
                      MessageBoxButton.OK,
                      MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    // opens a folder picker dialog and returns the selected folder if it's valid, otherwise returns null
    private string? PickValidFolder(string? initialDirectory = null, string title = "Select a folder")
    {
        // configure dialog
        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = true,
            Title = title,
            InitialDirectory = initialDirectory
        };

        // show dialog
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            if (CheckFolderValid(dialog.FileName))
            {
                // selected folder is valid, return it
                return dialog.FileName;
            }
        }

        return null;
    }

    // opens a folder picker to change the path of an existing folder entry
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    void EditFolder(FolderEntryViewModel folder)
    {
        // open folder picker with current folder as initial directory
        var newPath = PickValidFolder(folder.Path, "Select a folder");

        // user cancelled or selected invalid folder, do not update
        if (newPath == null)
        {
            return;
        }

        // update folder path
        folder.Path = newPath;

        // save updated folder list
        SaveFolders();

        // refresh file view as folder paths have changed
        OnPropertyChanged(nameof(FilesView));
    }

    // opens a folder picker to add a new folder entry to the list
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    void AddFolder()
    {
        // open folder picker
        var newPath = PickValidFolder(title: "Select a folder to add");

        // user cancelled or selected invalid folder, do not add
        if (newPath == null)
        {
            return;
        }

        // add new folder to list
        var folder = new FolderEntryViewModel(newPath);

        // save updated folder list
        Folders.Add(folder);

        // attach property changed handler to update file view when folder properties change
        folder.PropertyChanged += Folder_PropertyChanged;
    }

    [RelayCommand]
    void RemoveFolder(FolderEntryViewModel folder)
    {
        // remove folder from list
        Folders.Remove(folder);
        // refresh file view
        OnPropertyChanged(nameof(FilesView));
    }

    bool IsSubfolder(string folderPath, string basePath)
    {
        // Normalize paths to ensure consistent comparison
        var normalizedFolder = Path.GetFullPath(folderPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var normalizedBase = Path.GetFullPath(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

        // Check if folderPath starts with basePath
        return normalizedFolder.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase);
    }

    string GetFolder(FileEntryViewModel f)
    {
        return Path.GetDirectoryName(f.Filename)!;
    }

    string? GetUserFolder(FileEntryViewModel f)
    {
        return Folders
            .FirstOrDefault(x => f.Filename.StartsWith(x.Path, StringComparison.OrdinalIgnoreCase))
            ?.Path;
    }

    private bool CanMoveFolderUp(FolderEntryViewModel item)
    {
        return CanRunOperations && item != null && Folders.IndexOf(item) > 0;
    }

    private bool CanMoveFolderDown(FolderEntryViewModel item)
    {
        return CanRunOperations && item != null && Folders.IndexOf(item) < Folders.Count - 1;
    }

    [RelayCommand(CanExecute = nameof(CanMoveFolderUp))]
    public void MoveFolderUp(FolderEntryViewModel item)
    {
        var index = Folders.IndexOf(item);
        if (index > 0)
        {
            Folders.Move(index, index - 1);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveFolderDown))]
    public void MoveFolderDown(FolderEntryViewModel item)
    {
        var index = Folders.IndexOf(item);
        if (index < Folders.Count - 1 && index >= 0)
        {
            Folders.Move(index, index + 1);
        }
    }

    //============================================================
    // 🔍 COMMANDS — SCANNING
    //============================================================

    // Main method to scan all folders and detect duplicates
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    async Task ScanAsync()
    {
        StartOperation();

        try
        {
            // clear previous results
            await ResetUiAsync();

            await Task.Run(async () =>
            {
                // total files to process
                int numberOfSteps = 3;

                // populate file list
                var allFiles = CollectAllFiles(numberOfSteps);

                // create file entries in parallel
                await CreateFileEntriesAsync(allFiles, numberOfSteps);

                // create indexes for quick lookup during comparison
                var sizeIndex = Files
                    .GroupBy(f => f.Size)
                    .ToDictionary(g => g.Key, g => g.ToList());

                var folderIndex = Files
                    .GroupBy(GetFolder)
                    .ToDictionary(g => g.Key, g => g.ToList());

                var userFolderIndex = Files
                    .GroupBy(GetUserFolder)
                    .ToDictionary(g => g.Key!, g => g.ToList());

                // ignore files smaller than or equal to ignore size
                if (IgnoreSize > 0)
                {
                    foreach (var file in Files)
                    {
                        if (file.Size < IgnoreSize)
                        {
                            file.State = FileEntryViewModel.FileState.ignored;
                        }
                    }
                }

                // configure sorting behavior for file view
                RefreshFilesView();

                int processed = 1;

                // mark unique files as unique if we can
                if (CompareSize || CompareContent || CompareHash)
                {
                    switch (SelectedFolderComparisonMode)
                    {
                        case FolderComparisonMode.All:
                            {
                                foreach (var group in sizeIndex.Where(g => g.Value.Count == 1))
                                {
                                    group.Value[0].State = FileEntryViewModel.FileState.unique;
                                    processed++;
                                }
                            }
                            break;
                        case FolderComparisonMode.SameFolder:
                            {
                                foreach (var folder in folderIndex.Values)
                                {
                                    foreach (var g in folder.GroupBy(f => f.Size).Where(g => g.Count() == 1))
                                    {
                                        g.First().State = FileEntryViewModel.FileState.unique;
                                        processed++;
                                    }
                                }
                            }
                            break;
                        case FolderComparisonMode.SameUserFolder:
                            {
                                foreach (var folder in userFolderIndex.Values)
                                {
                                    foreach (var g in folder.GroupBy(f => f.Size).Where(g => g.Count() == 1))
                                    {
                                        g.First().State = FileEntryViewModel.FileState.unique;
                                        processed++;
                                    }
                                }
                            }
                            break;
                        case FolderComparisonMode.DifferentFolder:
                            {
                                foreach (var g in sizeIndex.Values)
                                {
                                    var folders = g.Select(GetFolder).Distinct().Count();

                                    if (folders == 1)
                                    {
                                        foreach (var f in g)
                                        {
                                            f.State = FileEntryViewModel.FileState.unique;
                                            processed++;
                                        }
                                    }
                                }
                            }
                            break;
                        case FolderComparisonMode.DifferentUserFolder:
                            {
                                foreach (var g in sizeIndex.Values)
                                {
                                    var users = g.Select(GetUserFolder).Distinct().Count();

                                    if (users == 1)
                                    {
                                        foreach (var f in g)
                                        {
                                            f.State = FileEntryViewModel.FileState.unique;
                                            processed++;
                                        }
                                    }
                                }
                            }
                            break;
                    }
                }

                foreach (var file in Files)
                {
                    // exit immediately if cancelled
                    cts.Token.ThrowIfCancellationRequested();

                    // wait if paused
                    pauseEvent.Wait(cts.Token);

                    // skip files already marked
                    if (file.State != FileEntryViewModel.FileState.idle && file.State != FileEntryViewModel.FileState.hashed)
                    {
                        continue;
                    }

                    // get list of files to compare against
                    var compareFiles = new List<FileEntryViewModel>();
                    if (CompareSize || CompareContent || CompareHash)
                    {
                        compareFiles = sizeIndex[file.Size]
                        .Where(f => f != file &&
                            f.State != FileEntryViewModel.FileState.unique &&
                            f.State != FileEntryViewModel.FileState.ignored)
                        .ToList();
                    }

                    // apply folder comparison mode
                    switch (SelectedFolderComparisonMode)
                    {
                        case FolderComparisonMode.All:
                            // do not exclude any files
                            break;
                        case FolderComparisonMode.SameFolder:
                            {
                                // we only want to compare against files in the same folder
                                compareFiles = compareFiles.Where(f => GetFolder(f) == GetFolder(file)).ToList();
                            }
                            break;
                        case FolderComparisonMode.DifferentFolder:
                            {
                                // we only want to compare against files in different folders
                                compareFiles = compareFiles.Where(f => GetFolder(f) != GetFolder(file)).ToList();
                            }
                            break;
                        case FolderComparisonMode.SameUserFolder:
                            {
                                // we only want to compare against files in the same user's folder
                                compareFiles = compareFiles.Where(f => GetUserFolder(f) == GetUserFolder(file)).ToList();
                            }
                            break;
                        case FolderComparisonMode.DifferentUserFolder:
                            {
                                // we only want to compare against files in different user's folders
                                compareFiles = compareFiles.Where(f => GetUserFolder(f) != GetUserFolder(file)).ToList();
                            }
                            break;
                    }

                    // if there are no files to compare against, mark as unique and continue
                    bool fileIsUnique = true;

                    if (compareFiles.Any())
                    {
                        // compare current file against others
                        int newProcessedOrNone = await CompareFile(file, compareFiles, numberOfSteps, processed);

                        // if new processed count is returned, update it, otherwise it means file was not unique but we still need to update progress
                        if (newProcessedOrNone != 0)
                        {
                            fileIsUnique = false;
                        }
                    }

                    // if file is unique, mark as unique
                    if (fileIsUnique)
                    {
                        // mark as unique
                        file.State = FileEntryViewModel.FileState.unique;

                        // update progress
                        processed++;
                        UpdateProgressSafely(processed, Files.Count, numberOfSteps, 2);
                    }

                    // refresh file view
                    OnPropertyChanged(nameof(FilesView));
                }

                // final progress update
                UpdateProgressSafely(1, 1);

                // refresh file view
                OnPropertyChanged(nameof(FilesView));

                // show error summary if any
                int errorFiles = Files.Where(f => f.State == FileEntryViewModel.FileState.error).Count();
                if (errorFiles > 0)
                {
                    MessageBox.Show($"Errors on {errorFiles} files");
                }
            });
        }
        catch (OperationCanceledException)
        {
            // operation was cancelled
            EndOperation(OperationState.Cancelled);
        }
        catch
        {
            // some other error occurred
            if (CurrentState == OperationState.Running || CurrentState == OperationState.Paused)
            {
                EndOperation(OperationState.Error);
            }
        }
        finally
        {
            // finalize operation
            EndOperation();
        }
    }

    private async Task ResetUiAsync()
    {
        await App.Current.Dispatcher.InvokeAsync(() =>
        {
            // clear existing files and reset view
            Files = new ThrottledObservableCollection<FileEntryViewModel>();
            Files.BeginSuppressNotifications();

            // refresh file view
            OnPropertyChanged(nameof(FilesView));
        });
    }

    private List<FileInfo> CollectAllFiles(int numberOfSteps)
    {
        // collect all files from selected folders
        var allFiles = new List<FileInfo>();

        var options = new EnumerationOptions()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.System
        };

        // first, count total number of files for progress tracking
        int numberOfFiles = 0;
        foreach (var folder in Folders)
        {
            numberOfFiles += Directory.EnumerateFiles(folder.Path, "*", options).Count();
        }

        // now, collect files with progress updates
        foreach (var folder in Folders)
        {
            // Exit immediately if cancelled
            cts.Token.ThrowIfCancellationRequested();

            // wait if paused
            pauseEvent.Wait(cts.Token);

            // collect files
            foreach (var file in Directory.EnumerateFiles(folder.Path, "*", options))
            {
                cts.Token.ThrowIfCancellationRequested();
                pauseEvent.Wait(cts.Token);

                allFiles.Add(new FileInfo(file));

                UpdateProgressSafely(allFiles.Count, numberOfFiles, numberOfSteps, 0);
            }
        }

        return allFiles;
    }

    private async Task CreateFileEntriesAsync(
    List<FileInfo> allFiles,
    int numberOfSteps)
    {
        // create file entries in parallel
        int processed = 0;

        // sort files - files should be sorted by explorer style compare, but also in order of selected folders (files from first selected folder come first, then second, etc)
        var folderList = Folders.ToList();

        allFiles = allFiles
            .OrderBy(f => folderList.FindIndex(folder => f.FullName.StartsWith(folder.Path, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(f => f.Name, Comparer<string>.Create(ExplorerStyleCompare))
            .ToList();

        foreach (var file in allFiles)
        {
            // Exit immediately if cancelled
            if (cts.IsCancellationRequested)
            {
                return;
            }

            // wait if paused
            pauseEvent.Wait(cts.Token);

            try
            {

                var newFile = new FileEntryViewModel(file);
                newFile.OnStateChanged = () =>
                {
                    OnPropertyChanged(nameof(TotalData));
                    OnPropertyChanged(nameof(IdleData));
                    OnPropertyChanged(nameof(UniqueData));
                    OnPropertyChanged(nameof(DeleteData));
                    OnPropertyChanged(nameof(KeepData));
                    OnPropertyChanged(nameof(IgnoredData));
                    OnPropertyChanged(nameof(ErrorData));
                    OnPropertyChanged(nameof(OtherData));
                    OnPropertyChanged(nameof(TotalAfterDeleteData));

                    OnPropertyChanged(nameof(TotalFiles));
                    OnPropertyChanged(nameof(IdleFiles));
                    OnPropertyChanged(nameof(UniqueFiles));
                    OnPropertyChanged(nameof(DeleteFiles));
                    OnPropertyChanged(nameof(KeepFiles));
                    OnPropertyChanged(nameof(IgnoredFiles));
                    OnPropertyChanged(nameof(ErrorFiles));
                    OnPropertyChanged(nameof(OtherFiles));
                    OnPropertyChanged(nameof(TotalAfterDeleteFiles));

                    OnPropertyChanged(nameof(KeepPercentageData));
                    OnPropertyChanged(nameof(DeletePercentageData));
                    OnPropertyChanged(nameof(UniquePercentageData));

                    OnPropertyChanged(nameof(KeepPercentageFiles));
                    OnPropertyChanged(nameof(DeletePercentageFiles));
                    OnPropertyChanged(nameof(UniquePercentageFiles));
                };

                Files.Add(newFile);

                processed++;
                UpdateProgressSafely(processed, allFiles.Count, numberOfSteps, 1);
            }
            catch
            {
                // error, not sure, maybe just silently ignore?
            }
        }

        // exit if cancelled
        cts.Token.ThrowIfCancellationRequested();
    }

    public static int ExplorerStyleCompare(string a, string b)
    {
        int ai = 0, bi = 0;

        while (ai < a.Length && bi < b.Length)
        {
            char ca = a[ai];
            char cb = b[bi];

            // Special rule: '.' comes first
            if (ca == '.' && cb != '.')
                return -1;
            if (cb == '.' && ca != '.')
                return 1;

            // Compare digits numerically if both are digits
            if (char.IsAsciiDigit(ca) && char.IsAsciiDigit(cb))
            {
                int startA = ai;
                int startB = bi;
                while (ai < a.Length && char.IsAsciiDigit(a[ai])) ai++;
                while (bi < b.Length && char.IsAsciiDigit(b[bi])) bi++;

                var nA = BigInteger.Parse(a[startA..ai]);
                var nB = BigInteger.Parse(b[startB..bi]);

                int cmp = nA.CompareTo(nB);
                if (cmp != 0) return cmp;
                continue;
            }

            // Otherwise, normal case-insensitive comparison
            int cmpChar = char.ToLower(ca).CompareTo(char.ToLower(cb));
            if (cmpChar != 0) return cmpChar;

            ai++;
            bi++;
        }

        return a.Length.CompareTo(b.Length);
    }

    private void RefreshFilesView()
    {
        // sort files by name using explorer-style comparison
        var sortedFiles = Files.Where(f => FilterByVisibleFolders(f)).OrderBy(f => f.Filename, Comparer<string>.Create(ExplorerStyleCompare)).ToList();

        // create virtualized view
        FilesView = new VirtualizedFileList(sortedFiles);

        // refresh file view
        OnPropertyChanged(nameof(FilesView));
    }

    private async Task<int> CompareFile(FileEntryViewModel file, List<FileEntryViewModel> candidates, int numberOfSteps, int processed)
    {
        // compare size if requested
        if (CompareSize)
        {
            candidates = candidates.Where(f => f.Size == file.Size).ToList();
        }

        // early return check for efficiency
        if (candidates.Count <= 0)
        {
            return 0;
        }

        // compare date modified if requested
        if (CompareDateModified)
        {
            candidates = candidates.Where(f => f.LastModified == file.LastModified).ToList();
        }

        // early return check for efficiency
        if (candidates.Count <= 0)
        {
            return 0;
        }

        // compare disk if requested
        if (CompareDisk)
        {
            candidates = candidates.Where(f => Path.GetPathRoot(f.Filename) == Path.GetPathRoot(file.Filename)).ToList();
        }

        // early return check for efficiency
        if (candidates.Count <= 0)
        {
            return 0;
        }

        // compare path if requested
        if (ComparePath)
        {
            candidates = candidates.Where(f => Path.GetDirectoryName(f.Filename) == Path.GetDirectoryName(file.Filename)).ToList();
        }

        // early return check for efficiency
        if (candidates.Count <= 0)
        {
            return 0;
        }

        // compare exact filename if requested
        if (CompareFilenameExact)
        {
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(file.Filename);
            candidates = candidates.Where(f =>
            {
                string candidateNameWithoutExt = Path.GetFileNameWithoutExtension(f.Filename);
                return string.Equals(candidateNameWithoutExt, fileNameWithoutExt, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        // early return check for efficiency
        if (candidates.Count <= 0)
        {
            return 0;
        }

        // compare similar filename, where we just look if the filename we have without extension is "starts with" of the candidates
        if (CompareFilenameSimilar)
        {
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(file.Filename);
            candidates = candidates.Where(f =>
            {
                string candidateNameWithoutExt = Path.GetFileNameWithoutExtension(f.Filename);
                return candidateNameWithoutExt.StartsWith(fileNameWithoutExt, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        // early return check for efficiency
        if (candidates.Count <= 0)
        {
            return 0;
        }

        // compare extension if requested
        if (CompareExtension)
        {
            string fileExt = Path.GetExtension(file.Filename);
            candidates = candidates.Where(f => string.Equals(Path.GetExtension(f.Filename), fileExt, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // early return check for efficiency
        if (candidates.Count <= 0)
        {
            return 0;
        }

        // compare content if requested
        if (CompareContent)
        {
            // remove files with different content due to differences in size
            candidates = candidates.Where(f => f.Size == file.Size).ToList();

            foreach (var candidate in candidates)
            {
                // Exit immediately if cancelled
                cts.Token.ThrowIfCancellationRequested();

                // wait if paused
                pauseEvent.Wait(cts.Token);

                // detemine if contents match
                using var fs1 = new FileStream(file.Filename, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var fs2 = new FileStream(candidate.Filename, FileMode.Open, FileAccess.Read, FileShare.Read);

                // allocate temporary buffers
                byte[] buffer1 = new byte[8192];
                byte[] buffer2 = new byte[8192];

                while (true)
                {
                    // Exit immediately if cancelled
                    cts.Token.ThrowIfCancellationRequested();

                    // wait if paused
                    pauseEvent.Wait(cts.Token);

                    // read chunks
                    int bytesRead1 = await fs1.ReadAsync(buffer1, 0, buffer1.Length, cts.Token);
                    int bytesRead2 = await fs2.ReadAsync(buffer2, 0, buffer2.Length, cts.Token);

                    // different sizes read, files differ
                    if (bytesRead1 != bytesRead2)
                    {
                        candidates.Remove(candidate);
                    }

                    // reached end of both files and all bytes matched
                    if (bytesRead1 == 0)
                    {
                        break;
                    }

                    // compare buffers
                    if (!buffer1.AsSpan(0, bytesRead1).SequenceEqual(buffer2.AsSpan(0, bytesRead2)))
                    {
                        candidates.Remove(candidate);
                    }
                }
            }
        }

        // early return check for efficiency
        if (candidates.Count <= 0)
        {
            return 0;
        }

        // compare hash if requested
        if (CompareHash)
        {
            // remove files with different content due to differences in size
            candidates = candidates.Where(f => f.Size == file.Size).ToList();

            // if there are candidates left, compute hashes
            if (candidates.Count != 0)
            {
                // flatten the groups into a single list of files to hash
                var allFilesToHash = candidates.Concat([file]);

                // we want a task for each disk so that each disk can be processed in parallel
                var filesByDisk = allFilesToHash
                    .GroupBy(f => Path.GetPathRoot(f.Filename))
                    .Select(g => g.ToList())
                    .ToList();

                var diskTasks = filesByDisk.Select(async diskGroup =>
                {
                    // get disk semaphore - make sure one file reads at a time per disk
                    SemaphoreSlim diskSemaphore = new(1, 1);

                    // semaphore for maximum number of files being hashed at the same time
                    SemaphoreSlim filesPerDisk = new(5, 5);

                    var fileTasks = diskGroup.Select(async file =>
                    {
                        // Exit immediately if cancelled
                        if (cts.IsCancellationRequested)
                        {
                            return;
                        }

                        await filesPerDisk.WaitAsync(cts.Token);
                        try
                        {
                            pauseEvent.Wait(cts.Token);

                            // compute hash
                            await file.HashAsync(
                                SelectedHashingAlgorithm,
                                cts.Token,
                                pauseEvent,
                                diskSemaphore);
                        }
                        catch
                        {
                            // ignore individual file errors
                        }
                        finally
                        {
                            filesPerDisk.Release();
                        }
                    });

                    await Task.WhenAll(fileTasks);
                });

                await Task.WhenAll(diskTasks);

                // after hashing, filter candidates by matching hash
                candidates = candidates.Where(f => file.HashString == f.HashString).ToList();
            }
        }

        // early return check for efficiency
        if (candidates.Count <= 0)
        {
            return 0;
        }

        // candidates are left - mark current file as keep, others as delete
        file.State = FileEntryViewModel.FileState.keep;
        foreach (var match in candidates)
        {
            match.State = FileEntryViewModel.FileState.delete;
        }

        // update progress
        processed++;
        UpdateProgressSafely(processed, Files.Count, numberOfSteps, 2);

        return processed;
    }

    //============================================================
    // 🗑️ COMMANDS — DELETE & COPY
    //============================================================

    // Deletes all files marked for deletion (moves to recycle bin)
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    async Task DeleteVisibleFilesAsync()
    {
        // confirm deletion
        var restult = MessageBox.Show("Are you sure you want to delete all VISIBLE files marked for deletion? This will move them to the Recycle Bin.",
            "Confirm Deletion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (restult != MessageBoxResult.Yes)
        {
            return;
        }

        StartOperation();

        try
        {
            // keep track of copied and failed files
            int deleted = 0;
            int failed = 0;

            // Capture visible files
            var visibleFiles = FilesView.Cast<FileEntryViewModel>().ToList();
            if (visibleFiles.Count == 0)
            {
                MessageBox.Show("No visible files to copy.");
                return;
            }

            await Task.Run(async () =>
            {
                // get files to delete
                int total = visibleFiles.Count;
                int processed = 0;

                // Semaphore to limit concurrency
                using var semaphore = new SemaphoreSlim(MaxThreads);
                var deleteTasks = visibleFiles.Select(async file =>
                {
                    // Exit immediately if cancelled
                    if (cts?.IsCancellationRequested ?? true)
                    {
                        return;
                    }

                    await semaphore.WaitAsync(cts.Token);
                    try
                    {
                        pauseEvent.Wait(cts.Token);
                        file.DeleteToRecycleBin();
                        Interlocked.Increment(ref deleted);
                        Interlocked.Increment(ref processed);
                        UpdateProgressSafely(processed, total);
                    }
                    catch
                    {
                        Interlocked.Increment(ref failed);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                // wait for all deletions to complete
                await Task.WhenAll(deleteTasks);
            });


            // show summary
            MessageBox.Show($"Deleted {deleted} files\nFailed to delete {failed} files");
        }
        catch (OperationCanceledException)
        {
            // operation was cancelled
            EndOperation(OperationState.Cancelled);
        }
        catch
        {
            // some other error occurred
            if (CurrentState == OperationState.Running || CurrentState == OperationState.Paused)
            {
                EndOperation(OperationState.Error);
            }
        }
        finally
        {
            // finalize operation
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    async Task CopyVisibleFilesAsync()
    {
        // confirm copy all visible files
        var result = MessageBox.Show("Are you sure you want to copy all VISIBLE files to a selected folder? The folder structure will be preserved.",
            "Confirm Copy",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        StartOperation();

        try
        {
            var dialog = new CommonOpenFileDialog()
            {
                IsFolderPicker = true,
                Title = "Select destination folder"
            };
            if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
            {
                return;
            }

            string destRoot = dialog.FileName;

            // keep track of copied and failed files
            int copied = 0;
            int failed = 0;

            // Capture visible files
            var visibleFiles = FilesView.Cast<FileEntryViewModel>().ToList();
            if (visibleFiles.Count == 0)
            {
                MessageBox.Show("No visible files to copy.");
                return;
            }

            await Task.Run(async () =>
            {
                // Build a lookup of folder roots for relative paths
                var folderRoots = Folders.Select(f => f.Path).ToList();

                int total = visibleFiles.Count;
                int processed = 0;

                // Semaphore to limit concurrency
                using var semaphore = new SemaphoreSlim(MaxThreads);
                var copyTasks = visibleFiles.Select(async file =>
                {
                    // Exit immediately if cancelled
                    if (cts?.IsCancellationRequested ?? true)
                    {
                        return;
                    }

                    await semaphore.WaitAsync(cts.Token);
                    try
                    {
                        pauseEvent.Wait(cts.Token);

                        // Find which root folder this file came from
                        string? root = folderRoots.FirstOrDefault(r => file.Filename.StartsWith(r, StringComparison.OrdinalIgnoreCase));
                        if (root == null)
                        {
                            return;
                        }

                        string relativePath = Path.GetRelativePath(root, file.Filename);
                        string targetDir = Path.Combine(destRoot, Path.GetFileName(root), Path.GetDirectoryName(relativePath) ?? "");
                        string targetFile = Path.Combine(targetDir, Path.GetFileName(file.Filename));

                        try
                        {
                            Directory.CreateDirectory(targetDir);
                            File.Copy(file.Filename, targetFile, overwrite: true);
                            Interlocked.Increment(ref copied);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to copy {file.Filename}: {ex.Message}");
                            Interlocked.Increment(ref failed);
                        }

                        Interlocked.Increment(ref processed);
                        UpdateProgressSafely(processed, total);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                // wait for all deletions to complete
                await Task.WhenAll(copyTasks);
            });

            // show summary
            MessageBox.Show($"Copied {copied} files to:\n{destRoot}\nFailed to copy {failed} files");
        }
        catch (OperationCanceledException)
        {
            // operation was cancelled
            EndOperation(OperationState.Cancelled);
        }
        catch
        {
            // some other error occurred
            if (CurrentState == OperationState.Running || CurrentState == OperationState.Paused)
            {
                EndOperation(OperationState.Error);
            }
        }
        finally
        {
            // finalize operation
            EndOperation();
        }
    }

    //============================================================
    // ⏱️ COMMANDS — PAUSE / RESUME / CANCEL
    //============================================================

    private void StartOperation()
    {
        CurrentState = OperationState.Running;
        operationStarted = DateTime.UtcNow;
        ProgressValue = 0;
        cts = new CancellationTokenSource();
        pauseEvent.Set();
    }

    private void EndOperation(OperationState endState = OperationState.Completed)
    {
        // if already in a terminal state, do nothing
        if (CurrentState == OperationState.Cancelled || CurrentState == OperationState.Completed || CurrentState == OperationState.Error)
        {
            return;
        }

        // if we were cancelling, set to cancelled
        if (CurrentState == OperationState.Cancelling)
        {
            endState = OperationState.Cancelled;
        }
        CurrentState = endState;
        pauseEvent.Set();

        Files.EndSuppressNotifications();
    }

    // Cancels current scan or delete task
    [RelayCommand(CanExecute = nameof(CanCancel))]
    void Cancel()
    {
        CurrentState = OperationState.Cancelling;
        cts.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    void Pause()
    {
        if (CurrentState == OperationState.Running)
        {
            CurrentState = OperationState.Paused;
            pauseEvent.Reset(); // causes all worker loops to wait
            EtaText = "Paused";
        }
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    void Resume()
    {
        if (CurrentState == OperationState.Paused)
        {
            CurrentState = OperationState.Running;
            pauseEvent.Set(); // releases all waiting threads
        }
    }

    public async Task Close()
    {
        Cancel();
        while (CurrentState == OperationState.Running || CurrentState == OperationState.Paused || CurrentState == OperationState.Cancelling)
        {
            await Task.Delay(100);
        }
    }

    //============================================================
    // 📊 PROGRESS & ETA UPDATES
    //============================================================

    // Safely updates UI progress with throttling to avoid lag
    private void UpdateProgressSafely(int processed, int files)
    {
        UpdateProgressSafely(processed, files, 1, 0);
    }

    private void UpdateProgressSafely(int processed, int totalInStep, int numberOfSteps, int currentStep)
    {
        // Progress fraction within current step
        double stepProgress = (double)processed / totalInStep;

        // Overall progress fraction considering step number
        double overallProgressFraction = ((double)currentStep + stepProgress) / numberOfSteps;

        // Only update if change is noticeable
        if (Math.Abs(overallProgressFraction - lastProgressValue) >= 0.0001)
        {
            lock (progressLock)
            {
                lastProgressValue = overallProgressFraction;

                App.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressValue = overallProgressFraction * 100;
                });

                UpdateEta(stepProgress);
            }
        }
    }

    private void UpdateEta(double stepProgress)
    {
        var elapsed = (DateTime.UtcNow - operationStarted).TotalSeconds;

        // calculate ETA based on step progress
        if (stepProgress > 0)
        {
            double remainingSeconds = elapsed * (1 - stepProgress) / stepProgress;

            TimeSpan eta = TimeSpan.FromSeconds(remainingSeconds);
            EtaText = $"ETA: {eta.Hours:D2}:{eta.Minutes:D2}:{eta.Seconds:D2}";
        }
        else
        {
            EtaText = "ETA: calculating...";
        }

        // update runtime every time eta is updated
        TimeSpan runTime = DateTime.UtcNow - operationStarted;
        if (runTime.TotalDays >= 1)
        {
            RuntimeText = $"Run Time: {runTime.Days}d {runTime.Hours:D2}:{runTime.Minutes:D2}:{runTime.Seconds:D2}";
        }
        else
        {
            RuntimeText = $"Run Time: {runTime.Hours:D2}:{runTime.Minutes:D2}:{runTime.Seconds:D2}";
        }

    }

    //============================================================
    // 🪟 UI TOGGLES & FILTERS
    //============================================================

    // Filter function to show/hide files based on folder visibility
    private bool isUpdatingFromHeader = false;
    private bool isUpdatingFromChildren = false;

    private static bool? ComputeTriState(IEnumerable<bool> values)
    {
        // track if any true/false found
        bool anyTrue = false;
        bool anyFalse = false;

        // check values
        foreach (var value in values)
        {
            if (value)
            {
                // found a true value
                anyTrue = true;
            }
            else
            {
                // found a false value
                anyFalse = true;
            }

            // if both true and false found, return indeterminate
            if (anyTrue && anyFalse)
            {
                return null;
            }
        }

        // return final state
        return anyTrue ? true : false;
    }

    private void UpdateAllFromHeader(bool? value, Action<FolderEntryViewModel, bool> setter, Action<bool?> setHeader)
    {
        // avoid recursion
        if (isUpdatingFromChildren)
        {
            return;
        }

        // if indeterminate, set all to false
        if (!value.HasValue)
        {
            setHeader(false);
            return;
        }

        // update all children
        isUpdatingFromHeader = true;
        foreach (var f in Folders)
        {
            setter(f, value.Value);
        }
        isUpdatingFromHeader = false;
    }

    private void RecalculateHeader(Action<bool?> setHeader, Func<FolderEntryViewModel, bool> selector)
    {
        // avoid recursion
        if (isUpdatingFromHeader)
        {
            return;
        }

        // recalculate based on children
        isUpdatingFromChildren = true;
        setHeader(ComputeTriState(Folders.Select(selector)));
        isUpdatingFromChildren = false;
    }

    private void Folder_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // refresh file view
        RefreshFilesView();

        // avoid recursion
        if (isUpdatingFromHeader)
            return;

        // update header based on which property changed
        switch (e.PropertyName)
        {
            case nameof(FolderEntryViewModel.ShowIdle):
                RecalculateIdleHeader();
                break;
            case nameof(FolderEntryViewModel.ShowHashing):
                RecalculateHashingHeader();
                break;
            case nameof(FolderEntryViewModel.ShowHashed):
                RecalculateHashedHeader();
                break;
            case nameof(FolderEntryViewModel.ShowKeep):
                RecalculateKeepHeader();
                break;
            case nameof(FolderEntryViewModel.ShowDelete):
                RecalculateDeleteHeader();
                break;
            case nameof(FolderEntryViewModel.ShowUnique):
                RecalculateUniqueHeader();
                break;
            case nameof(FolderEntryViewModel.ShowDeleting):
                RecalculateDeletingHeader();
                break;
            case nameof(FolderEntryViewModel.ShowDeleted):
                RecalculateDeletedHeader();
                break;
            case nameof(FolderEntryViewModel.ShowIgnored):
                RecalculateIgnoredHeader();
                break;
            case nameof(FolderEntryViewModel.ShowError):
                RecalculateErrorHeader();
                break;
        }
    }

    [ObservableProperty] bool? allIdleChecked = true;
    partial void OnAllIdleCheckedChanged(bool? value)
     => UpdateAllFromHeader(value, (f, v) => f.ShowIdle = v, v => AllIdleChecked = v);

    private void RecalculateIdleHeader()
        => RecalculateHeader(v => AllIdleChecked = v, f => f.ShowIdle);

    [ObservableProperty] bool? allHashingChecked = true;
    partial void OnAllHashingCheckedChanged(bool? value)
        => UpdateAllFromHeader(value, (f, v) => f.ShowHashing = v, v => AllHashingChecked = v);

    private void RecalculateHashingHeader()
        => RecalculateHeader(v => AllHashingChecked = v, f => f.ShowHashing);

    [ObservableProperty] bool? allHashedChecked = true;
    partial void OnAllHashedCheckedChanged(bool? value)
     => UpdateAllFromHeader(value, (f, v) => f.ShowHashed = v, v => AllHashedChecked = v);

    private void RecalculateHashedHeader()
        => RecalculateHeader(v => AllHashedChecked = v, f => f.ShowHashed);

    [ObservableProperty] bool? allKeepChecked = true;
    partial void OnAllKeepCheckedChanged(bool? value)
     => UpdateAllFromHeader(value, (f, v) => f.ShowKeep = v, v => AllKeepChecked = v);

    private void RecalculateKeepHeader()
        => RecalculateHeader(v => AllKeepChecked = v, f => f.ShowKeep);

    [ObservableProperty] bool? allDeleteChecked = true;
    partial void OnAllDeleteCheckedChanged(bool? value)
     => UpdateAllFromHeader(value, (f, v) => f.ShowDelete = v, v => AllDeleteChecked = v);

    private void RecalculateDeleteHeader()
        => RecalculateHeader(v => AllDeleteChecked = v, f => f.ShowDelete);

    [ObservableProperty] bool? allUniqueChecked = true;
    partial void OnAllUniqueCheckedChanged(bool? value)
     => UpdateAllFromHeader(value, (f, v) => f.ShowUnique = v, v => AllUniqueChecked = v);

    private void RecalculateUniqueHeader()
        => RecalculateHeader(v => AllUniqueChecked = v, f => f.ShowUnique);

    [ObservableProperty] bool? allDeletingChecked = true;
    partial void OnAllDeletingCheckedChanged(bool? value)
     => UpdateAllFromHeader(value, (f, v) => f.ShowDeleting = v, v => AllDeletingChecked = v);

    private void RecalculateDeletingHeader()
        => RecalculateHeader(v => AllDeletingChecked = v, f => f.ShowDeleting);

    [ObservableProperty] bool? allDeletedChecked = true;
    partial void OnAllDeletedCheckedChanged(bool? value)
     => UpdateAllFromHeader(value, (f, v) => f.ShowDeleted = v, v => AllDeletedChecked = v);

    private void RecalculateDeletedHeader()
        => RecalculateHeader(v => AllDeletedChecked = v, f => f.ShowDeleted);

    [ObservableProperty] bool? allIgnoredChecked = true;

    partial void OnAllIgnoredCheckedChanged(bool? value)
     => UpdateAllFromHeader(value, (f, v) => f.ShowIgnored = v, v => AllIgnoredChecked = v);

    private void RecalculateIgnoredHeader()
        => RecalculateHeader(v => AllIgnoredChecked = v, f => f.ShowIgnored);

    [ObservableProperty] bool? allErrorChecked = true;
    partial void OnAllErrorCheckedChanged(bool? value)
     => UpdateAllFromHeader(value, (f, v) => f.ShowError = v, v => AllErrorChecked = v);

    private void RecalculateErrorHeader()
        => RecalculateHeader(v => AllErrorChecked = v, f => f.ShowError);

    //============================================================
    // 🪄 EVENT HANDLERS & HELPERS
    //============================================================

    partial void OnCurrentStateChanged(OperationState value)
    {
        // Update command availability whenever state changes
        ScanCommand.NotifyCanExecuteChanged();
        DeleteVisibleFilesCommand.NotifyCanExecuteChanged();
        CopyVisibleFilesCommand.NotifyCanExecuteChanged();
        AddFolderCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    bool FilterByVisibleFolders(object item)
    {
        // only show FileEntryViewModel items
        if (item is not FileEntryViewModel f)
        {
            return false;
        }

        // find the folder this file belongs to
        var folder = Folders.FirstOrDefault(x => f.Filename.StartsWith(x.Path, StringComparison.OrdinalIgnoreCase));

        // if no folder found, filter out
        if (folder == null)
        {
            return false;
        }

        // apply that folder's filters
        return f.State switch
        {
            FileEntryViewModel.FileState.idle => folder.ShowIdle,
            FileEntryViewModel.FileState.hashing => folder.ShowHashing,
            FileEntryViewModel.FileState.hashed => folder.ShowHashed,
            FileEntryViewModel.FileState.keep => folder.ShowKeep,
            FileEntryViewModel.FileState.delete => folder.ShowDelete,
            FileEntryViewModel.FileState.unique => folder.ShowUnique,
            FileEntryViewModel.FileState.deleting => folder.ShowDeleting,
            FileEntryViewModel.FileState.deleted => folder.ShowDeleted,
            FileEntryViewModel.FileState.ignored => folder.ShowIgnored,
            FileEntryViewModel.FileState.error => folder.ShowError,
            _ => false
        };
    }
}
