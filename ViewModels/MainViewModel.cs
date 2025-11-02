using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;

namespace DuplicateDetector.ViewModels;

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
    int maxThreads = Environment.ProcessorCount * 8;

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

    private ManualResetEventSlim pauseEvent = new(true); // starts in "running" state
    [ObservableProperty]
    bool isPaused;

    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        CopyFilesCommand.NotifyCanExecuteChanged();
        AddFolderCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPausedChanged(bool value)
    {
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    bool CanPause => IsBusy && !IsPaused;
    bool CanResume => IsBusy && IsPaused;


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
        // only show FileEntryViewModel items
        if (item is not FileEntryViewModel f)
        {
            return false;
        }

        // find the folder this file belongs to
        var folder = Folders.FirstOrDefault(x => f.Filename.StartsWith(x.Path, StringComparison.OrdinalIgnoreCase));

        // if no folder found or folder is hidden, filter out
        if (folder == null || !folder.IsVisible)
        {
            return false;
        }

        // apply that folder's filters
        return f.State switch
        {
            FileEntryViewModel.FileState.keep => folder.ShowKeep,
            FileEntryViewModel.FileState.delete => folder.ShowDelete,
            FileEntryViewModel.FileState.unique => folder.ShowUnique,
            _ => true
        };
    }

    // Opens a folder picker and adds a selected folder to scan list
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    void AddFolder()
    {
        // configure dialog
        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = true,
            Title = "Select a folder"
        };

        // show dialog
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            // Check that folder is not inside any existing folder
            if (Folders.Any(f => IsSubfolder(dialog.FileName, f.Path)))
            {
                MessageBox.Show("The selected folder is already inside an existing folder in the list.",
                            "Cannot Add Folder",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                return; // Do not add because it's inside an existing folder
            }

            // Check that no existing folder is inside the newly selected folder
            if (Folders.Any(f => IsSubfolder(f.Path, dialog.FileName)))
            {
                MessageBox.Show("An existing folder in the list is inside the folder you selected.",
                           "Cannot Add Folder",
                           MessageBoxButton.OK,
                           MessageBoxImage.Warning);
                return; // Do not add because an existing folder is inside it
            }

            // avoid adding duplicates
            if (Folders.Any(f => f.Path.Equals(dialog.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("The selected folder is already in the list.",
                          "Cannot Add Folder",
                          MessageBoxButton.OK,
                          MessageBoxImage.Information);
                return;
            }

            Folders.Add(new FolderEntryViewModel(dialog.FileName));
        }
    }

    bool IsSubfolder(string folderPath, string basePath)
    {
        var normalizedFolder = Path.GetFullPath(folderPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var normalizedBase = Path.GetFullPath(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

        return normalizedFolder.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    void RemoveFolder(FolderEntryViewModel folder)
    {
        // remove folder from list
        Folders.Remove(folder);

        // refresh file view
        FilesView.Refresh();
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
                    pauseEvent.Wait(cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    var dirInfo = new DirectoryInfo(folder.Path);
                    var options = new EnumerationOptions()
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.System
                    };
                    allFiles.AddRange(dirInfo.GetFiles("*", options));
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
                        pauseEvent.Wait(cts.Token);
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

                // re-add folders visibility and filtering
                foreach (var folder in Folders)
                {
                    folder.PropertyChanged += (_, __) => FilesView.Refresh();
                }

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
                        pauseEvent.Wait(cts.Token);
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
                        pauseEvent.Wait(cts.Token);
                        cts.Token.ThrowIfCancellationRequested();
                        await file.HashAsync(SelectedAlgorithm, cts.Token, pauseEvent); // your existing sync hash method
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
        IsPaused = false;
        pauseEvent.Set(); // ensure unpaused state for next run
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
                    pauseEvent.Wait(cts.Token);
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
                    pauseEvent.Wait(cts.Token);
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

    [ObservableProperty]
    bool? allVisibleChecked = true;
    partial void OnAllVisibleCheckedChanged(bool? value)
    {
        if (value.HasValue)
        {
            foreach (var f in Folders)
                f.IsVisible = value.Value;
        }
    }

    [ObservableProperty]
    bool? allKeepChecked = true;
    partial void OnAllKeepCheckedChanged(bool? value)
    {
        if (value.HasValue)
        {
            foreach (var f in Folders)
                f.ShowKeep = value.Value;
        }
    }

    [ObservableProperty]
    bool? allDeleteChecked = true;
    partial void OnAllDeleteCheckedChanged(bool? value)
    {
        if (value.HasValue)
        {
            foreach (var f in Folders)
                f.ShowDelete = value.Value;
        }
    }

    [ObservableProperty]
    bool? allUniqueChecked = true;
    partial void OnAllUniqueCheckedChanged(bool? value)
    {
        if (value.HasValue)
        {
            foreach (var f in Folders)
                f.ShowUnique = value.Value;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    void Pause()
    {
        if (!IsPaused)
        {
            IsPaused = true;
            pauseEvent.Reset(); // causes all worker loops to wait
            EtaText = "Paused";
        }
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    void Resume()
    {
        if (IsPaused)
        {
            IsPaused = false;
            pauseEvent.Set(); // releases all waiting threads
            stepStartTime = DateTime.UtcNow; // restart ETA timing
        }
    }

}
