using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.VisualBasic.FileIO;
using System.IO;
using System.Security.Cryptography;

namespace DuplicateDetector.ViewModels;

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
    public async Task HashAsync(MainViewModel.CompareAlgorithm compareAlgorithm, CancellationToken token, ManualResetEventSlim pauseEvent, SemaphoreSlim diskSemaphore)
    {
        State = FileState.hashing;

        try
        {
            // Large buffer (e.g., 16MB)
            byte[] buffer = new byte[16 * 1024 * 1024];

            await using var stream = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan | FileOptions.Asynchronous);

            byte[] hashBytes;

            int bytesRead;

            var crc32 = new System.IO.Hashing.Crc32();
            var md5 = MD5.Create();
            var sha256 = SHA256.Create();
            using var sha512 = SHA512.Create();

            while (true)
            {
                await diskSemaphore.WaitAsync(token);
                try
                {
                    bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                }
                finally
                {
                    diskSemaphore.Release();
                }

                if (bytesRead == 0)
                {
                    break;
                }

                switch (compareAlgorithm)
                {
                    case MainViewModel.CompareAlgorithm.Crc32:
                    case MainViewModel.CompareAlgorithm.Crc32PlusFullCompare:
                        crc32.Append(buffer.AsSpan(0, bytesRead));
                        break;
                    case MainViewModel.CompareAlgorithm.MD5:
                        md5.TransformBlock(buffer, 0, bytesRead, null, 0);
                        break;
                    case MainViewModel.CompareAlgorithm.SHA256:
                        sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                        break;
                    case MainViewModel.CompareAlgorithm.SHA512:
                        sha512.TransformBlock(buffer, 0, bytesRead, null, 0);
                        break;
                }

                pauseEvent.Wait(token);
                token.ThrowIfCancellationRequested();
            }

            switch (compareAlgorithm)
            {
                case MainViewModel.CompareAlgorithm.Crc32:
                case MainViewModel.CompareAlgorithm.Crc32PlusFullCompare:
                    hashBytes = crc32.GetCurrentHash();
                    break;
                case MainViewModel.CompareAlgorithm.MD5:
                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    hashBytes = md5.Hash!;
                    break;
                case MainViewModel.CompareAlgorithm.SHA256:
                    sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    hashBytes = sha256.Hash!;
                    break;
                case MainViewModel.CompareAlgorithm.SHA512:
                    sha512.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    hashBytes = sha512.Hash!;
                    break;
                default:
                    throw new Exception("Unknown algorithm");
            }

            HashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            State = FileState.hashed;
        }
        catch (OperationCanceledException)
        {
            State = FileState.idle;
            throw;
        }
        catch
        {
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