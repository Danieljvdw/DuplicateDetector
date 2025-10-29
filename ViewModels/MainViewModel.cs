using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Data;

namespace DuplicateDetector.ViewModels;

// ViewModel representing a single file entry in the duplicate detection process
public partial class FileEntryViewModel : ObservableObject
{
    // Enum representing possible file processing states
    public enum FileState
    {
        idle,
        hashing,
        hashed,
        keep,
        delete,
        unique,
        deleting,
        deleted,
        error
    }

    // Static counter to assign unique IDs to duplicate groups
    public static int duplicateGroupIndex = 0;

    // Full file path
    [ObservableProperty]
    string filename;

    // File size in bytes
    [ObservableProperty]
    long size;

    // Event invoked whenever file state changes
    public Action? OnStateChanged;

    // Backing field for the file's current state
    private FileState state;
    public FileState State
    {
        get => state;
        set
        {
            // Notify property change and trigger callback
            if (SetProperty(ref state, value))
            {
                OnStateChanged?.Invoke();
            }
        }
    }

    // Returns all possible file states as an array (for UI binding)
    public static Array FileStates => Enum.GetValues(typeof(FileEntryViewModel.FileState));

    // Hexadecimal representation of computed file hash
    [ObservableProperty]
    string? hashString = null;

    // Group index for duplicate detection
    [ObservableProperty]
    int? duplicateGroup = null;

    // Constructor initializes from FileInfo
    public FileEntryViewModel(FileInfo info)
    {
        Filename = info.FullName;
        Size = info.Length;
    }

    // Calculates hash of the file using selected algorithm
    public void Hash(MainViewModel.CompareAlgorithm compareAlgorithm)
    {
        State = FileState.hashing;

        try
        {
            using var stream = File.OpenRead(Filename); // or FilePath if you store that instead

            byte[] hashBytes;

            // Select hashing algorithm
            switch (compareAlgorithm)
            {
                case MainViewModel.CompareAlgorithm.Crc32:
                case MainViewModel.CompareAlgorithm.Crc32PlusFullCompare:
                    var crc32 = new System.IO.Hashing.Crc32();
                    byte[] buffer = new byte[8192];
                    int bytesRead;

                    // Incrementally compute CRC32 checksum
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        crc32.Append(buffer.AsSpan(0, bytesRead));
                    }

                    hashBytes = crc32.GetCurrentHash();
                    break;

                case MainViewModel.CompareAlgorithm.MD5:
                    using (var md5 = MD5.Create())
                    {
                        hashBytes = md5.ComputeHash(stream);
                    }
                    break;

                case MainViewModel.CompareAlgorithm.SHA256:
                    using (var sha256 = SHA256.Create())
                    {
                        hashBytes = sha256.ComputeHash(stream);
                    }
                    break;

                case MainViewModel.CompareAlgorithm.SHA512:
                    using (var sha512 = SHA512.Create())
                    {
                        hashBytes = sha512.ComputeHash(stream);
                    }
                    break;

                default:
                    throw new Exception("Unknown algorithm");
            }

            // Convert hash bytes to lowercase hex string
            HashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            State = FileState.hashed;
        }
        catch
        {
            // Handle I/O or permission errors
            State = FileState.error;
        }
    }

    // Moves the file to the recycle bin and updates state
    public void DeleteToRecycleBin()
    {
        State = FileState.deleting;
        FileSystem.DeleteFile(Filename, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        State = FileState.deleted;
    }
}

public partial class FolderEntryViewModel : ObservableObject
{
    public string Path { get; }

    [ObservableProperty]
    bool isVisible = true;

    public FolderEntryViewModel(string path)
    {
        Path = path;
    }
}


public partial class MainViewModel : ObservableObject
{
    // Supported hashing / comparison algorithms
    public enum CompareAlgorithm
    {
        [Description("CRC32 (collision possible for millions of files)")]
        Crc32,

        [Description("CRC32 + Full Compare (safe)")]
        Crc32PlusFullCompare,

        [Description("MD5 (~1 in 10¹⁷ accidental collision)")]
        MD5,

        [Description("SHA256 (~1 in 10⁷⁵ accidental collision)")]
        SHA256,

        [Description("SHA512 (~1 in 10¹⁵⁰ accidental collision)")]
        SHA512
    }

    // Expose list of algorithms for UI binding
    public static Array CompareAlgorithms => Enum.GetValues(typeof(CompareAlgorithm));

    // List of selected folder paths
    [ObservableProperty]
    ObservableCollection<FolderEntryViewModel> folders = new();

    // Collection of all scanned file entries
    [ObservableProperty]
    ObservableCollection<FileEntryViewModel> files = new();

    // CollectionView for sorting, grouping, and filtering
    public ICollectionView FilesView { get; set; }

#if false // want to enable storing hashes in file for low RAM usage
    [ObservableProperty]
    bool useHashFile;
#endif

    // Currently selected hashing algorithm
    [ObservableProperty]
    CompareAlgorithm selectedAlgorithm = CompareAlgorithm.MD5;

    // Progress bar value (0–100%)
    [ObservableProperty]
    double progressValue;

    private double lastProgressValue = 0;
    private readonly object progressLock = new();

    // Whether a process is currently running
    [ObservableProperty]
    bool isBusy;

    // whether the buttons can be used
    public bool CanRunOperations => !IsBusy;
    public bool CanCancel => IsBusy;


    // Optional filter to display only certain file states
    [ObservableProperty]
    FileEntryViewModel.FileState? fileStateFilter = null;

    // Max degree of parallelism
    [ObservableProperty]
    int maxThreads = Environment.ProcessorCount * 2;

    // Total bytes scanned
    public long TotalData => Files.Sum(f => f.Size);

    // Bytes belonging to unique (non-duplicate) files
    public long UniqueData => Files.Where(f => f.State == FileEntryViewModel.FileState.unique).Sum(f => f.Size);

    // Bytes that would be freed by deleting duplicates
    public long DeleteData => Files.Where(f => f.State == FileEntryViewModel.FileState.delete).Sum(f => f.Size);

    // Bytes marked to keep (including unique + manually kept files)
    public long KeepData => Files.Where(f => f.State == FileEntryViewModel.FileState.keep).Sum(f => f.Size);

    // Bytes that will remain after deleting files marked for deletion
    public long TotalAfterDelete => TotalData - DeleteData;

    // for stacked chart displaying percentages
    public double KeepPercentage => TotalData > 0 ? (double)KeepData / TotalData : 0;
    public double DeletePercentage => TotalData > 0 ? (double)DeleteData / TotalData : 0;
    public double UniquePercentage => TotalData > 0 ? (double)UniqueData / TotalData : 0;

    // application version
    public string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version == null)
            {
                return "unknown";
            }

            // Take Major, Minor, Build (skip Revision)
            return $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        CopyFilesCommand.NotifyCanExecuteChanged();
        AddFolderCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    // For cancelling asynchronous operations
    private CancellationTokenSource? cts = null;

    [ObservableProperty]
    string etaText = "ETA: idle";

    private DateTime stepStartTime;

    public MainViewModel()
    {
        // Configure sorting behavior for file view
        var cvs = new CollectionViewSource { Source = Files };
        FilesView = cvs.View;
    }

    bool FilterByVisibleFolders(object item)
    {
        if (item is not FileEntryViewModel f)
            return false;

        if (Folders.Count == 0)
            return true;

        var visibleFolders = Folders.Where(x => x.IsVisible).Select(x => x.Path);
        return visibleFolders.Any(folder =>
            f.Filename.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

    // Opens a folder picker and adds a selected folder to scan list
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    void AddFolder()
    {
        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = true,
            Title = "Select a folder"
        };
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            if (!Folders.Any(f => f.Path.Equals(dialog.FileName, StringComparison.OrdinalIgnoreCase)))
                Folders.Add(new FolderEntryViewModel(dialog.FileName));
        }
    }

    // Main method to scan all folders and detect duplicates
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    async Task ScanAsync()
    {
        // set busy state
        IsBusy = true;

        // clear previous results
        await App.Current.Dispatcher.InvokeAsync(() =>
        {
            Files = new ObservableCollection<FileEntryViewModel>();
            var cvs = new CollectionViewSource { Source = Files };
            FilesView = cvs.View;
            FilesView.Refresh();
        });

        // setup cancellation token
        cts = new CancellationTokenSource();

        try
        {
            await Task.Run(async () =>
            {
                // reset progress
                ProgressValue = 0;

                // reset duplicate group index
                FileEntryViewModel.duplicateGroupIndex = 0;

                // sempaphore to limit concurrency
                var semaphore = new SemaphoreSlim(MaxThreads);

                // populate file list
                var allFiles = new List<FileInfo>();
                foreach (var folder in Folders)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var dirInfo = new DirectoryInfo(folder.Path);
                    allFiles.AddRange(dirInfo.GetFiles("*", System.IO.SearchOption.AllDirectories));
                }

                // total files to process
                int numberOfSteps = 4;
                int processed = 0;

                // create file entries in parallel
                ObservableCollection<FileEntryViewModel> tempObservableFiles = new ObservableCollection<FileEntryViewModel>();

                // update step time
                stepStartTime = DateTime.UtcNow;

                var fileTasks = allFiles.Select(async file =>
                {
                    await semaphore.WaitAsync(cts.Token);
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var newFile = new FileEntryViewModel(file);
                        newFile.OnStateChanged = () =>
                        {
                            OnPropertyChanged(nameof(TotalData));
                            OnPropertyChanged(nameof(UniqueData));
                            OnPropertyChanged(nameof(DeleteData));
                            OnPropertyChanged(nameof(KeepData));
                            OnPropertyChanged(nameof(TotalAfterDelete));
                            OnPropertyChanged(nameof(KeepPercentage));
                            OnPropertyChanged(nameof(DeletePercentage));
                            OnPropertyChanged(nameof(UniquePercentage));
                        };
                        tempObservableFiles.Add(newFile);

                        processed++;
                        UpdateProgressSafely(processed, allFiles.Count, numberOfSteps, 0);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                // wait for all files to complete
                await Task.WhenAll(fileTasks);

                // update Files
                Files = tempObservableFiles;

                // Configure sorting behavior for file view
                var cvs = new CollectionViewSource { Source = Files };
                FilesView = cvs.View;
                FilesView.Filter = FilterByVisibleFolders;

                // Default sort: by duplicate group, size (desc), and filename
                FilesView.SortDescriptions.Add(new SortDescription(nameof(FileEntryViewModel.DuplicateGroup), ListSortDirection.Ascending));
                FilesView.SortDescriptions.Add(new SortDescription(nameof(FileEntryViewModel.Size), ListSortDirection.Descending));
                FilesView.SortDescriptions.Add(new SortDescription(nameof(FileEntryViewModel.Filename), ListSortDirection.Ascending));

                OnPropertyChanged(nameof(FilesView));

                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    FilesView.Refresh();
                });

                // create groups of files with same size
                var sizeGroups = Files.GroupBy(f => f.Size)
                                  .Where(g => g.Count() > 1)
                                  .ToList();

                // mark files with unique sizes as unique
                var uniqueSizeFiles = Files.GroupBy(f => f.Size)
                                       .Where(g => g.Count() == 1)
                                       .SelectMany(g => g);

                processed = 0;

                // update step time
                stepStartTime = DateTime.UtcNow;

                // Semaphore to limit concurrency
                fileTasks = uniqueSizeFiles.Select(async file =>
                {
                    await semaphore.WaitAsync(cts.Token);
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        file.State = FileEntryViewModel.FileState.unique;

                        processed++;
                        UpdateProgressSafely(processed, uniqueSizeFiles.Count(), numberOfSteps, 1);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                // wait for all files to complete
                await Task.WhenAll(fileTasks);

                // flatten the groups into a single list of files to hash
                var filesToHash = sizeGroups.SelectMany(g => g).ToList();

                processed = 0;

                // update step time
                stepStartTime = DateTime.UtcNow;

                // hash all candidate files in parallel
                // Semaphore to limit concurrency
                fileTasks = filesToHash.Select(async file =>
                {
                    await semaphore.WaitAsync(cts.Token);
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        file.Hash(SelectedAlgorithm); // your existing sync hash method
                        Interlocked.Increment(ref processed);
                        UpdateProgressSafely(processed, filesToHash.Count, numberOfSteps, 2);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                // wait for all files to complete
                await Task.WhenAll(fileTasks);

                // if there were no files to hash, update progress
                if (filesToHash.Count == 0)
                {
                    UpdateProgressSafely(1, 1, numberOfSteps, 2);
                }

                // group files by hash
                var hashGroups = Files
                .Where(f => f.State == FileEntryViewModel.FileState.hashed)
                .GroupBy(f => f.HashString)
                .Where(g => g.Count() > 1)  // only groups with potential duplicates
                .ToList();

                processed = 0;
                int totalFilesInHashGroups = hashGroups.Sum(g => g.Count());

                // update step time
                stepStartTime = DateTime.UtcNow;

                // compare files within each hash group
                foreach (var group in hashGroups)
                {
                    // optional: for weak hashes like CRC32, do byte-by-byte comparison
                    bool needFullCompare = SelectedAlgorithm == MainViewModel.CompareAlgorithm.Crc32PlusFullCompare;

                    // list to hold duplicates in this group
                    var duplicates = new List<FileEntryViewModel>();

                    // compare each file with every other file in the group
                    for (int i = 0; i < group.Count(); i++)
                    {
                        var fileA = group.ElementAt(i);

                        // skip if already marked
                        if (fileA.State != FileEntryViewModel.FileState.hashed)
                        {
                            continue;
                        }

                        for (int j = i + 1; j < group.Count(); j++)
                        {
                            var fileB = group.ElementAt(j);

                            // skip if already marked
                            if (fileB.State != FileEntryViewModel.FileState.hashed)
                            {
                                continue;
                            }

                            // perform full comparison if needed
                            bool isDuplicate = true;
                            if (needFullCompare)
                            {
                                isDuplicate = AreFilesIdentical(fileA.Filename, fileB.Filename);
                            }

                            // mark as duplicates if they match
                            if (isDuplicate)
                            {
                                if (!duplicates.Contains(fileA))
                                {
                                    duplicates.Add(fileA);
                                }
                                duplicates.Add(fileB);
                            }
                        }

                        // add the current file to duplicates if any were found
                        if (duplicates.Count > 0)
                        {
                            // pick file with shortest filename to keep
                            var fileToKeep = duplicates.OrderBy(f => f.Filename.Length).First();
                            fileToKeep.State = FileEntryViewModel.FileState.keep;
                            fileToKeep.DuplicateGroup = FileEntryViewModel.duplicateGroupIndex;

                            // mark the rest for deletion
                            foreach (var file in duplicates.Except(new[] { fileToKeep }))
                            {
                                // mark for deletion
                                file.State = FileEntryViewModel.FileState.delete;

                                file.DuplicateGroup = FileEntryViewModel.duplicateGroupIndex;

                                // update progress
                                processed++;
                                UpdateProgressSafely(processed, totalFilesInHashGroups, numberOfSteps, 3);
                            }

                            FileEntryViewModel.duplicateGroupIndex++;
                        }
                        else
                        {
                            // mark as unique if no duplicates
                            fileA.State = FileEntryViewModel.FileState.unique;
                        }

                        // update progress
                        processed++;
                        UpdateProgressSafely(processed, totalFilesInHashGroups, numberOfSteps, 3);
                    }
                }

                // mark any files which have been hashed but not marked as a keep or delete (duplicate), then mark as unique
                var hashedUniqueFiles = Files
                .Where(f => f.State == FileEntryViewModel.FileState.hashed)
                .ToList();

                foreach (var file in hashedUniqueFiles)
                {
                    file.State = FileEntryViewModel.FileState.unique;
                }

                // if there were no files to compare, update progress
                if (totalFilesInHashGroups == 0)
                {
                    UpdateProgressSafely(1, 1, numberOfSteps, 3);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // canceled
        }
        finally
        {
            // clear cancellation token
            cts = null;
        }

        // clear busy state
        IsBusy = false;
    }

    // Cancels current scan or delete task
    [RelayCommand(CanExecute = nameof(CanCancel))]
    void Cancel()
    {
        cts?.Cancel();
    }

    // Deletes all files marked for deletion (moves to recycle bin)
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    async Task DeleteSelectedAsync()
    {
        // set busy state
        IsBusy = true;

        await Task.Run(async () =>
        {
            // setup cancellation token
            cts = new CancellationTokenSource();

            // reset progress
            ProgressValue = 0;

            // get files to delete
            var filesToDelete = Files.Where(f => f.State == FileEntryViewModel.FileState.delete).ToList();
            int total = filesToDelete.Count;
            int processed = 0;

            // update step time
            stepStartTime = DateTime.UtcNow;

            // Semaphore to limit concurrency
            using var semaphore = new SemaphoreSlim(MaxThreads);
            var deleteTasks = filesToDelete.Select(async file =>
            {
                await semaphore.WaitAsync(cts.Token);
                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    file.DeleteToRecycleBin();
                    Interlocked.Increment(ref processed);
                    UpdateProgressSafely(processed, total);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            // wait for all deletions to complete
            await Task.WhenAll(deleteTasks);

            // clear cancellation token
            cts = null;
        });

        // clear busy state
        IsBusy = false;
    }

    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    async Task CopyFilesAsync()
    {
        // set busy state
        IsBusy = true;

        // setup cancellation token
        cts = new CancellationTokenSource();

        // reset progress
        ProgressValue = 0;

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

            // update step time
            stepStartTime = DateTime.UtcNow;

            // Semaphore to limit concurrency
            using var semaphore = new SemaphoreSlim(MaxThreads);
            var copyTasks = visibleFiles.Select(async file =>
            {
                await semaphore.WaitAsync(cts.Token);
                try
                {
                    cts.Token.ThrowIfCancellationRequested();

                    // Find which root folder this file came from
                    string? root = folderRoots.FirstOrDefault(r =>
                        file.Filename.StartsWith(r, StringComparison.OrdinalIgnoreCase));
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

            // clear cancellation
            cts = null;
        });

        // show summary
        MessageBox.Show($"Copied {copied} files to:\n{destRoot}\nFailed to copy {failed} files");

        // clear busy state
        IsBusy = false;
    }

    // Byte-by-byte comparison for final verification
    private bool AreFilesIdentical(string file1, string file2)
    {
        using var fs1 = File.OpenRead(file1);
        using var fs2 = File.OpenRead(file2);

        if (fs1.Length != fs2.Length) return false;

        int b1, b2;
        do
        {
            b1 = fs1.ReadByte();
            b2 = fs2.ReadByte();
            if (b1 != b2) return false;
        } while (b1 != -1);

        return true;
    }

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
        var elapsed = (DateTime.UtcNow - stepStartTime).TotalSeconds;

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
    }
}
