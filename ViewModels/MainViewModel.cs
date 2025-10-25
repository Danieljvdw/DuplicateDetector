using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
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
    ObservableCollection<string> folders = new();

    // Collection of all scanned file entries
    [ObservableProperty]
    ObservableCollection<FileEntryViewModel> files = new();

    // CollectionView for sorting, grouping, and filtering
    public ICollectionView FilesView { get; }

#if false // want to enable storing hashes in file for low RAM usage
    [ObservableProperty]
    bool useHashFile;
#endif

    // Currently selected hashing algorithm
    [ObservableProperty]
    CompareAlgorithm selectedAlgorithm = CompareAlgorithm.Crc32;

    // Progress bar value (0–100%)
    [ObservableProperty]
    double progressValue;

    private double lastProgressValue = 0;
    private readonly object progressLock = new();

    // Whether scanning is currently active
    [ObservableProperty]
    bool isScanning;

    // Optional filter to display only certain file states
    [ObservableProperty]
    FileEntryViewModel.FileState? fileStateFilter = null;

    // Max degree of parallelism
    [ObservableProperty]
    int maxThreads = Environment.ProcessorCount;

    // Total bytes scanned
    [ObservableProperty]
    long totalData;

    // Bytes belonging to unique (non-duplicate) files
    [ObservableProperty]
    long uniqueData;

    // Bytes that would be freed by deleting duplicates
    public long DeleteData => Files.Where(f => f.State == FileEntryViewModel.FileState.delete)
                                   .Sum(f => f.Size);

    // For cancelling asynchronous operations
    private CancellationTokenSource? cts = null;

    public MainViewModel()
    {
        // Attach handler to Files collection change events
        Files.CollectionChanged += (s, e) =>
        {
            // Subscribe to OnStateChanged of each new file
            if (e.NewItems != null)
            {
                foreach (FileEntryViewModel newFile in e.NewItems)
                {
                    newFile.OnStateChanged = () => OnPropertyChanged(nameof(DeleteData));
                }
            }

            // Update DeleteData whenever collection changes
            App.Current.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(DeleteData));
            });
        };

        // Configure sorting behavior for file view
        var cvs = new CollectionViewSource { Source = Files };
        FilesView = cvs.View;

        // Default sort: by duplicate group, size (desc), and filename
        FilesView.SortDescriptions.Add(new SortDescription(nameof(FileEntryViewModel.DuplicateGroup), ListSortDirection.Ascending));
        FilesView.SortDescriptions.Add(new SortDescription(nameof(FileEntryViewModel.Size), ListSortDirection.Descending));
        FilesView.SortDescriptions.Add(new SortDescription(nameof(FileEntryViewModel.Filename), ListSortDirection.Ascending));
    }

    // Opens a folder picker and adds a selected folder to scan list
    [RelayCommand]
    void AddFolder()
    {
        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = true,
            Title = "Select a folder"
        };
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            Folders.Add(dialog.FileName);
        }
    }

    // Main method to scan all folders and detect duplicates
    [RelayCommand]
    async Task ScanAsync()
    {
        await Task.Run(async () =>
        {
            // clear previous results
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                Files.Clear();
                FilesView.Refresh();
            });

            // setup cancellation token
            cts = new CancellationTokenSource();

            try
            {
                // mark as scanning
                IsScanning = true;

                // lock for updating stats
                object statUpdateLock = new object();

                // reset stats
                TotalData = 0;
                UniqueData = 0;

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
                    var dirInfo = new DirectoryInfo(folder);
                    allFiles.AddRange(dirInfo.GetFiles("*", System.IO.SearchOption.AllDirectories));
                }

                // total files to process
                int numberOfSteps = 4;
                int processed = 0;

                // create file entries in parallel
                var fileEntries = new List<FileEntryViewModel>();

                var fileTasks = allFiles.Select(async file =>
                {
                    await semaphore.WaitAsync(cts.Token);
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        fileEntries.Add(new FileEntryViewModel(file));

                        // accumulate total data
                        lock (statUpdateLock)
                        {
                            TotalData += file.Length;
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                // wait for all files to complete
                await Task.WhenAll(fileTasks);

                // Sequentially add to the ObservableCollection in batches
                processed = 0;
                const int batchSize = 100; // adjust batch size as needed
                for (int i = 0; i < fileEntries.Count; i += batchSize)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var batch = fileEntries.Skip(i).Take(batchSize).ToList();

                    // Add a batch on the UI thread
                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var f in batch)
                        {
                            Files.Add(f);
                        }

                        FilesView.Refresh();
                    });

                    processed += batch.Count;
                    UpdateProgressSafely(processed, fileEntries.Count, numberOfSteps, 0);
                }

                // wait for all files to complete
                await Task.WhenAll(fileTasks);

                // create groups of files with same size
                var sizeGroups = Files.GroupBy(f => f.Size)
                                      .Where(g => g.Count() > 1)
                                      .ToList();

                // mark files with unique sizes as unique
                var uniqueSizeFiles = Files.GroupBy(f => f.Size)
                                           .Where(g => g.Count() == 1)
                                           .SelectMany(g => g);

                processed = 0;

                // Semaphore to limit concurrency
                fileTasks = uniqueSizeFiles.Select(async file =>
                {
                    await semaphore.WaitAsync(cts.Token);
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        file.State = FileEntryViewModel.FileState.unique;
                        lock (statUpdateLock)
                        {
                            UniqueData += file.Size;
                        }

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

                // group files by hash
                var hashGroups = Files
                    .Where(f => f.State == FileEntryViewModel.FileState.hashed)
                    .GroupBy(f => f.HashString)
                    .Where(g => g.Count() > 1)  // only groups with potential duplicates
                    .ToList();

                processed = 0;
                int totalFilesInHashGroups = hashGroups.Sum(g => g.Count());

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
                            UniqueData += fileA.Size;
                        }

                        // update progress
                        processed++;
                        UpdateProgressSafely(processed, totalFilesInHashGroups, numberOfSteps, 3);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // canceled
            }

            // mark as not scanning
            IsScanning = false;

            // clear cancellation token
            cts = null;
        });
    }

    // Cancels current scan or delete task
    [RelayCommand]
    void Cancel()
    {
        cts?.Cancel();
    }

    // Deletes all files marked for deletion (moves to recycle bin)
    [RelayCommand]
    async Task DeleteSelectedAsync()
    {
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
                    UpdateProgressSafely(processed, filesToDelete.Count);
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

    private void UpdateProgressSafely(int processed, int files, int numberOfSteps, int step)
    {
        // compute new value
        double newValue = ((double)processed / files * 100 / numberOfSteps) + ((double)step / numberOfSteps * 100);

        // only update if change is noticeable (e.g., >= 0.1%)
        if (Math.Abs(newValue - lastProgressValue) >= 0.01)
        {
            lock (progressLock)
            {
                lastProgressValue = newValue;
                App.Current.Dispatcher.InvokeAsync(() => ProgressValue = newValue);
            }
        }
    }
}
