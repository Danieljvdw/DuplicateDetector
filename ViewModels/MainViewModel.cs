using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;

namespace DuplicateDetector.ViewModels;

public partial class FileEntryViewModel : ObservableObject
{
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

    public static int duplicateGroupIndex = 0;

    [ObservableProperty]
    string filename;

    [ObservableProperty]
    long size;

    [ObservableProperty]
    FileState state;

    [ObservableProperty]
    string? hashString = null;

    [ObservableProperty]
    int? duplicateGroup = null;

    public FileEntryViewModel(FileInfo info)
    {
        Filename = info.FullName;
        Size = info.Length;
    }

    public void Hash(MainViewModel.CompareAlgorithm compareAlgorithm)
    {
        State = FileState.hashing;

        try
        {
            using var stream = File.OpenRead(Filename); // or FilePath if you store that instead

            byte[] hashBytes;

            switch (compareAlgorithm)
            {
                case MainViewModel.CompareAlgorithm.Crc32:
                case MainViewModel.CompareAlgorithm.Crc32PlusFullCompare:
                    var crc32 = new System.IO.Hashing.Crc32();
                    byte[] buffer = new byte[8192];
                    int bytesRead;

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

            HashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant(); // store as hex
            State = FileState.hashed;
        }
        catch
        {
            State = FileState.error;
        }
    }

    public void DeleteToRecycleBin()
    {
        State = FileState.deleting;
        FileSystem.DeleteFile(Filename, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        State = FileState.deleted;
    }
}

public partial class MainViewModel : ObservableObject
{
    public enum CompareAlgorithm
    {
        Crc32,
        Crc32PlusFullCompare,
        MD5,
        SHA256,
        SHA512
    }

    [ObservableProperty]
    ObservableCollection<string> folders = new();

    [ObservableProperty]
    ObservableCollection<FileEntryViewModel> files = new();

#if false // want to enable storing hashes in file for low RAM usage
    [ObservableProperty]
    bool useHashFile;
#endif

    [ObservableProperty]
    CompareAlgorithm selectedAlgorithm = CompareAlgorithm.Crc32;

    [ObservableProperty]
    double progressValue;

    [ObservableProperty]
    bool isScanning;

    [ObservableProperty]
    FileEntryViewModel.FileState? fileStateFilter = null;

    [ObservableProperty]
    int maxThreads = Environment.ProcessorCount;

    [ObservableProperty]
    long totalData;         // total bytes scanned

    [ObservableProperty]
    long uniqueData;        // total bytes unique

    private CancellationTokenSource? cts = null;

    [RelayCommand]
    private void AddFolder()
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

    [RelayCommand]
    private async Task ScanAsync()
    {
        // clear previous results
        Files.Clear();

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
            int totalFiles = allFiles.Count;
            int processed = 0;

            // create file entries in parallel
            var fileEntries = new FileEntryViewModel[allFiles.Count];

            // parallel creation using async style
            await Parallel.ForEachAsync(Enumerable.Range(0, allFiles.Count), new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxThreads,
                CancellationToken = cts.Token
            }, (index, token) =>
            {
                cts.Token.ThrowIfCancellationRequested();
                fileEntries[index] = new FileEntryViewModel(allFiles[index]);

                // accumulate total data
                lock (statUpdateLock)
                {
                    TotalData += fileEntries[index].Size;
                }

                return ValueTask.CompletedTask;
            });

            // sequentially add to the ObservableCollection and update progress
            foreach (var entry in fileEntries)
            {
                cts.Token.ThrowIfCancellationRequested();
                Files.Add(entry);
                processed++;
                ProgressValue = (double)processed / (totalFiles * numberOfSteps) * 100;
            }

            // create groups of files with same size
            var sizeGroups = Files.GroupBy(f => f.Size)
                                  .Where(g => g.Count() > 1)
                                  .ToList();

            // mark files with unique sizes as unique
            var uniqueSizeFiles = Files.GroupBy(f => f.Size)
                                       .Where(g => g.Count() == 1)
                                       .SelectMany(g => g);

            foreach (var file in uniqueSizeFiles)
            {
                file.State = FileEntryViewModel.FileState.unique;
                lock (statUpdateLock)
                {
                    UniqueData += file.Size;
                }

                processed++;
                ProgressValue = (double)processed / (totalFiles * numberOfSteps) * 100;
            }

            // flatten the groups into a single list of files to hash
            var filesToHash = sizeGroups.SelectMany(g => g).ToList();

            // hash all candidate files in parallel
            await Parallel.ForEachAsync(filesToHash, new ParallelOptions
            {
                CancellationToken = cts.Token,
                MaxDegreeOfParallelism = MaxThreads
            }, (file, token) =>
            {
                token.ThrowIfCancellationRequested();
                file.Hash(SelectedAlgorithm); // your existing sync hash method
                Interlocked.Increment(ref processed);
                ProgressValue = (double)processed / (totalFiles * numberOfSteps) * 100;

                return ValueTask.CompletedTask;
            });

            // group files by hash
            var hashGroups = Files
                .Where(f => f.State == FileEntryViewModel.FileState.hashed)
                .GroupBy(f => f.HashString)
                .Where(g => g.Count() > 1)  // only groups with potential duplicates
                .ToList();

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
                            ProgressValue = (double)processed / (totalFiles * numberOfSteps) * 100;
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
                    ProgressValue = (double)processed / (totalFiles * numberOfSteps) * 100;
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
    }

    [RelayCommand]
    void Cancel()
    {
        cts?.Cancel();
    }

    [RelayCommand]
    async Task DeleteSelectedAsync()
    {
        // setup cancellation token
        cts = new CancellationTokenSource();

        // reset progress
        ProgressValue = 0;

        // get files to delete
        var filesToDelete = Files.Where(f => f.State == FileEntryViewModel.FileState.delete).ToList();
        int total = filesToDelete.Count;
        int processed = 0;

        // delete files in parallel
        await Parallel.ForEachAsync(filesToDelete, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxThreads
        }, (file, token) =>
        {
            file.DeleteToRecycleBin();
            Interlocked.Increment(ref processed);
            ProgressValue = (double)processed / total * 100;

            return ValueTask.CompletedTask;
        });
    }

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
}
