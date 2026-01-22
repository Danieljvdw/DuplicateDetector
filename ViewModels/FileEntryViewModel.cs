using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.VisualBasic.FileIO;
using System.ComponentModel;
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

    // Full file path
    [ObservableProperty]
    string filename;

    // File size in bytes
    [ObservableProperty]
    long size;

    // Last modified timestamp
    [ObservableProperty]
    DateTime lastModified = DateTime.MinValue;

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

    // Default cancellation token
    public CancellationTokenSource cts = new();
    private readonly CancellationToken fileToken = CancellationToken.None;

    // Constructor initializes from FileInfo
    public FileEntryViewModel(FileInfo info)
    {
        Filename = info.FullName;
        Size = info.Length;
        LastModified = info.LastWriteTimeUtc;

        fileToken = cts.Token;
    }

    // Calculates hash of the file using selected algorithm
    public async Task HashAsync(MainViewModel.HashingAlgorithm hashAlgorithm, CancellationToken processToken, ManualResetEventSlim pauseEvent, SemaphoreSlim diskSemaphore, ICollectionView FilesView)
    {
        // Skip if already hashed
        if (HashString != null)
        {
            return;
        }

        State = FileState.hashing;

        await App.Current.Dispatcher.InvokeAsync(() =>
        {
            FilesView.Refresh();
        });

        try
        {
            // Large buffer (e.g., 16MB)
            byte[] buffer = new byte[16 * 1024 * 1024];

            // Open file stream with asynchronous and sequential scan options
            await using var stream = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan | FileOptions.Asynchronous);

            // Variable to hold computed hash bytes
            byte[] hashBytes;

            // Number of bytes read in each iteration
            int bytesRead;

            // Initialize hash algorithm instances
            var crc32 = new System.IO.Hashing.Crc32();
            var md5 = MD5.Create();
            var sha256 = SHA256.Create();
            var sha512 = SHA512.Create();

            // Read file in chunks
            while (true)
            {
                // Wait for disk semaphore to limit concurrent disk access
                await diskSemaphore.WaitAsync(processToken);
                try
                {
                    bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, processToken);
                }
                finally
                {
                    diskSemaphore.Release();
                }

                // Break if end of file
                if (bytesRead == 0)
                {
                    break;
                }

                // Update hash with read bytes
                switch (hashAlgorithm)
                {
                    case MainViewModel.HashingAlgorithm.Crc32:
                        crc32.Append(buffer.AsSpan(0, bytesRead));
                        break;
                    case MainViewModel.HashingAlgorithm.MD5:
                        md5.TransformBlock(buffer, 0, bytesRead, null, 0);
                        break;
                    case MainViewModel.HashingAlgorithm.SHA256:
                        sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                        break;
                    case MainViewModel.HashingAlgorithm.SHA512:
                        sha512.TransformBlock(buffer, 0, bytesRead, null, 0);
                        break;
                }

                // Check for pause or cancellation
                pauseEvent.Wait(processToken);
                processToken.ThrowIfCancellationRequested();

                // Check for file-specific cancellation
                fileToken.ThrowIfCancellationRequested();
            }

            // Finalize hash computation
            switch (hashAlgorithm)
            {
                case MainViewModel.HashingAlgorithm.Crc32:
                    hashBytes = crc32.GetCurrentHash();
                    break;
                case MainViewModel.HashingAlgorithm.MD5:
                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    hashBytes = md5.Hash!;
                    break;
                case MainViewModel.HashingAlgorithm.SHA256:
                    sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    hashBytes = sha256.Hash!;
                    break;
                case MainViewModel.HashingAlgorithm.SHA512:
                    sha512.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    hashBytes = sha512.Hash!;
                    break;
                default:
                    throw new Exception("Unknown algorithm");
            }

            // Convert hash bytes to hexadecimal string
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
        finally
        {
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                FilesView.Refresh();
            });
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