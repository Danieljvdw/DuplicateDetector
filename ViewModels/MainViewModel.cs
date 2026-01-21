using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateDetector.Custom;
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

    //============================================================
    // 🧩 ENUMS & STATIC MEMBERS
    //============================================================

    // Supported hashing / comparison algorithms
    public enum HashingAlgorithm
    {
        [Description("CRC32 (collision possible for millions of files)")]
        Crc32,

        [Description("MD5 (~1 in 10¹⁷ accidental collision)")]
        MD5,

        [Description("SHA256 (~1 in 10⁷⁵ accidental collision)")]
        SHA256,

        [Description("SHA512 (~1 in 10¹⁵⁰ accidental collision)")]
        SHA512
    }

    // Expose list of hashing algorithms for UI binding
    public static Array HashingAlgorithms => Enum.GetValues(typeof(HashingAlgorithm));

    //============================================================
    // 📦 FOLDER & FILE DATA
    //============================================================

    // List of selected folder paths
    [ObservableProperty] ObservableCollection<FolderEntryViewModel> folders = [];

    // Collection of all scanned file entries
    [ObservableProperty] ThrottledObservableCollection<FileEntryViewModel> files = [];

    // CollectionView for sorting, grouping, and filtering
    public ICollectionView FilesView { get; set; }

    //============================================================
    // ⚙️ APP SETTINGS & PARAMETERS
    //============================================================

    // Currently selected hashing algorithm
    [ObservableProperty] HashingAlgorithm selectedHashingAlgorithm = HashingAlgorithm.SHA512;

#if false // want to enable storing hashes in file for low RAM usage
    [ObservableProperty] bool useHashFile;
#endif

    // Comparison criteria
    [ObservableProperty] bool compareFolders = false;
    [ObservableProperty] bool compareSize = true;
    [ObservableProperty] bool compareDateModified = false;
    [ObservableProperty] bool compareFilename = false;
    [ObservableProperty] bool compareContent = false;
    [ObservableProperty] bool compareHash = true;

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
    }

    //============================================================
    // 🧮 STATISTICS
    //============================================================

    public long TotalData => Files.Sum(f => f.Size); // Total bytes of all files
    public long UniqueData => Files.Where(f => f.State == FileEntryViewModel.FileState.unique).Sum(f => f.Size); // Total bytes of all unique files
    public long DeleteData => Files.Where(f => f.State == FileEntryViewModel.FileState.delete).Sum(f => f.Size); // Bytes that would be freed by deleting duplicates
    public long KeepData => Files.Where(f => f.State == FileEntryViewModel.FileState.keep).Sum(f => f.Size); // Bytes marked to keep (including unique + manually kept files)
    public long TotalAfterDeleteData => TotalData - DeleteData; // Bytes that will remain after deleting files marked for deletion

    public long TotalFiles => Files.Count; // Total number of files
    public long UniqueFiles => Files.Count(f => f.State == FileEntryViewModel.FileState.unique); // Number of unique files
    public long DeleteFiles => Files.Count(f => f.State == FileEntryViewModel.FileState.delete); // Number of files marked for deletion
    public long KeepFiles => Files.Count(f => f.State == FileEntryViewModel.FileState.keep); // Number of files marked to keep
    public long TotalAfterDeleteFiles => Files.Count(f => f.State != FileEntryViewModel.FileState.delete); // Number of files remaining after deletion

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
        // Configure sorting behavior for file view
        var cvs = new CollectionViewSource { Source = Files };
        FilesView = cvs.View;
    }

    //============================================================
    // 🧩 COMMANDS — FOLDER MANAGEMENT
    //============================================================

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

            // add folder to list
            var folder = new FolderEntryViewModel(dialog.FileName);
            Folders.Add(folder);
            folder.PropertyChanged += Folder_PropertyChanged;
            RecalculateIdleHeader();
        }
    }

    [RelayCommand]
    void RemoveFolder(FolderEntryViewModel folder)
    {
        // remove folder from list
        Folders.Remove(folder);

        // refresh file view
        FilesView.Refresh();
    }

    bool IsSubfolder(string folderPath, string basePath)
    {
        // Normalize paths to ensure consistent comparison
        var normalizedFolder = Path.GetFullPath(folderPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var normalizedBase = Path.GetFullPath(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

        // Check if folderPath starts with basePath
        return normalizedFolder.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase);
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
                // reset duplicate group index
                FileEntryViewModel.duplicateGroupIndex = 0;

                // semaphore to limit concurrency
                var semaphore = new SemaphoreSlim(MaxThreads);

                // populate file list
                var allFiles = CollectAllFiles();

                // total files to process
                int numberOfSteps = 2;

                // create file entries in parallel
                await CreateFileEntriesAsync(allFiles, semaphore, numberOfSteps);

                // configure sorting behavior for file view
                InitializeFilesView();

                int processed = 1;

                foreach (var file in Files)
                {
                    // exit immediately if cancelled
                    cts.Token.ThrowIfCancellationRequested();

                    // wait if paused
                    pauseEvent.Wait(cts.Token);

                    // skip files already marked
                    if (file.State == FileEntryViewModel.FileState.keep || file.State == FileEntryViewModel.FileState.delete || file.State == FileEntryViewModel.FileState.unique)
                    {
                        continue;
                    }

                    // get list of files to compare against
                    List<FileEntryViewModel> compareFiles = Files.Where(f => f != file && f.State != FileEntryViewModel.FileState.unique).ToList();

                    // if we are comparing folders, we don't care if there are duplicates in the same folder, we are comparing accross folders
                    if (CompareFolders)
                    {
                        // find folder of current file
                        var fileFolder = Folders.FirstOrDefault(f => file.Filename.StartsWith(f.Path, StringComparison.OrdinalIgnoreCase));

                        // exclude files from the same folder which is to be compared
                        if (fileFolder != null)
                        {
                            compareFiles = compareFiles.Where(f =>
                            {
                                var compareFileFolder = Folders.FirstOrDefault(ff => f.Filename.StartsWith(ff.Path, StringComparison.OrdinalIgnoreCase));
                                return compareFileFolder != fileFolder;
                            }).ToList();
                        }
                    }

                    // compare current file against others
                    processed = await CompareFile(file, compareFiles, numberOfSteps, processed);
                }

                // final progress update
                UpdateProgressSafely(1, 1);

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
            var cvs = new CollectionViewSource { Source = Files };
            FilesView = cvs.View;
            FilesView.Refresh();
        });
    }

    private List<FileInfo> CollectAllFiles()
    {
        var allFiles = new List<FileInfo>();

        foreach (var folder in Folders)
        {
            // Exit immediately if cancelled
            cts.Token.ThrowIfCancellationRequested();

            pauseEvent.Wait(cts.Token);

            var dirInfo = new DirectoryInfo(folder.Path);
            var options = new EnumerationOptions()
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.System
            };

            foreach (var file in Directory.EnumerateFiles(folder.Path, "*", options))
            {
                cts.Token.ThrowIfCancellationRequested();
                pauseEvent.Wait(cts.Token);

                allFiles.Add(new FileInfo(file));
            }
        }

        return allFiles;
    }

    private async Task CreateFileEntriesAsync(
    List<FileInfo> allFiles,
    SemaphoreSlim semaphore,
    int numberOfSteps)
    {
        // create file entries in parallel
        int processed = 0;

        var fileTasks = allFiles.Select(async file =>
        {
            // Exit immediately if cancelled
            if (cts.IsCancellationRequested)
            {
                return;
            }

            await semaphore.WaitAsync(cts.Token);
            try
            {
                pauseEvent.Wait(cts.Token);

                var newFile = new FileEntryViewModel(file);
                newFile.OnStateChanged = () =>
                {
                    OnPropertyChanged(nameof(TotalData));
                    OnPropertyChanged(nameof(UniqueData));
                    OnPropertyChanged(nameof(DeleteData));
                    OnPropertyChanged(nameof(KeepData));
                    OnPropertyChanged(nameof(TotalAfterDeleteData));

                    OnPropertyChanged(nameof(TotalFiles));
                    OnPropertyChanged(nameof(UniqueFiles));
                    OnPropertyChanged(nameof(DeleteFiles));
                    OnPropertyChanged(nameof(KeepFiles));
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
                UpdateProgressSafely(processed, allFiles.Count, numberOfSteps, 0);
            }
            finally
            {
                semaphore.Release();
            }
        });

        // wait for all files to complete
        await Task.WhenAll(fileTasks);

        // exit if cancelled
        cts.Token.ThrowIfCancellationRequested();
    }

    private void InitializeFilesView()
    {
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

        App.Current.Dispatcher.Invoke(() => FilesView.Refresh());
    }

    private async Task<int> CompareFile(FileEntryViewModel file, List<FileEntryViewModel> files, int numberOfSteps, int processed)
    {
        // Exclude self just in case the caller didn't
        var candidates = files.Where(f => f != file).ToList();

        // compare size if requested
        if (CompareSize)
        {
            candidates = candidates.Where(f => f.Size == file.Size).ToList();
        }

        // compare date modified if requested
        if (CompareDateModified)
        {
            candidates = candidates.Where(f => f.LastModified == file.LastModified).ToList();
        }

        // compare filename if requested
        if (CompareFilename)
        {
            candidates = candidates.Where(f => string.Equals(Path.GetFileName(f.Filename), Path.GetFileName(file.Filename), StringComparison.OrdinalIgnoreCase)).ToList();
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

                while (true)
                {
                    // Exit immediately if cancelled
                    cts.Token.ThrowIfCancellationRequested();

                    // wait if paused
                    pauseEvent.Wait(cts.Token);

                    // read chunks
                    byte[] buffer1 = new byte[8192];
                    byte[] buffer2 = new byte[8192];
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
                                diskSemaphore,
                                FilesView);
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

        // if there are no candidates left, mark as unique and return
        if (candidates.Count == 0)
        {
            // mark as unique
            file.State = FileEntryViewModel.FileState.unique;

            // update progress
            processed++;
            UpdateProgressSafely(processed, Files.Count, numberOfSteps, 1);
        }
        else
        {
            // mark current file as keep, others as delete
            file.State = FileEntryViewModel.FileState.keep;
            foreach (var match in candidates)
            {
                match.State = FileEntryViewModel.FileState.delete;
            }

            // update progress
            processed++;
            UpdateProgressSafely(processed, Files.Count, numberOfSteps, 1);
        }

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
        if (isUpdatingFromHeader)
            return;

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
            FileEntryViewModel.FileState.error => folder.ShowError,
            _ => false
        };
    }
}
