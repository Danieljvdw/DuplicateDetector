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

    public void Compare(List<FileEntryViewModel> files, MainViewModel.CompareAlgorithm compareAlgorithm)
    {
        if (State != FileState.hashed)
        {
            return;
        }

        foreach (var file in files)
        {
            if (file == this)
            {
                continue;
            }
            if (file.State != FileState.hashed)
            {
                continue;
            }
            if (file.Size != Size)
            {
                continue;
            }
            if (file.HashString != HashString)
            {
                continue;
            }
            if (compareAlgorithm == MainViewModel.CompareAlgorithm.Crc32PlusFullCompare)
            {
                // do full byte-by-byte comparison

            }

            // found a duplicate
            State = FileState.keep;
            file.State = FileState.keep;

            DuplicateGroup = duplicateGroupIndex;
            file.DuplicateGroup = duplicateGroupIndex;

            duplicateGroupIndex++;
            break;
        }

        // if duplcate not found, mark as unique
        State = FileState.unique;
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
        Files.Clear();

        cts = new CancellationTokenSource();

        try
        {
            IsScanning = true;

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

            int totalFiles = allFiles.Count;
            int processed = 0;

            // create file entries
            foreach (var fileInfo in allFiles)
            {
                cts.Token.ThrowIfCancellationRequested();
                Files.Add(new FileEntryViewModel(fileInfo));
                processed++;
                ProgressValue = (double)processed / (totalFiles * 3) * 100;
            }


            // hash all files
            foreach (var file in Files)
            {
                cts.Token.ThrowIfCancellationRequested();
                file.Hash(SelectedAlgorithm);
                processed++;
                ProgressValue = (double)processed / (totalFiles * 3) * 100;
            }

            // compare all files
            foreach (var file in Files)
            {
                cts.Token.ThrowIfCancellationRequested();
                file.Compare(Files.ToList(), SelectedAlgorithm);
                processed++;
                ProgressValue = (double)processed / (totalFiles * 3) * 100;
            }
        }
        catch (OperationCanceledException)
        {
            // canceled
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    void CancelScan()
    {
        cts?.Cancel();
    }

    [RelayCommand]
    void DeleteSelected()
    {
        foreach (var file in Files)
        {
            if (file.State == FileEntryViewModel.FileState.delete)
            {
                file.DeleteToRecycleBin();
            }
        }
    }
}
