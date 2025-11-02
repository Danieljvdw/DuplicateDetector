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
    public async Task HashAsync(MainViewModel.CompareAlgorithm compareAlgorithm, CancellationToken token, ManualResetEventSlim pauseEvent)
    {
        State = FileState.hashing;

        try
        {
            // 1 MB buffer for efficient SSD reads
            byte[] buffer = new byte[1024 * 1024];

            await using var stream = new FileStream(
                Filename,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: buffer.Length,
                useAsync: true);

            byte[] hashBytes;

            // Select hashing algorithm
            switch (compareAlgorithm)
            {
                case MainViewModel.CompareAlgorithm.Crc32:
                case MainViewModel.CompareAlgorithm.Crc32PlusFullCompare:
                    var crc32 = new System.IO.Hashing.Crc32();
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    {
                        // Incrementally compute CRC32 checksum
                        crc32.Append(buffer.AsSpan(0, bytesRead));
                        pauseEvent.Wait(token);
                        token.ThrowIfCancellationRequested();
                    }

                    hashBytes = crc32.GetCurrentHash();
                    break;

                case MainViewModel.CompareAlgorithm.MD5:
                    using (var md5 = MD5.Create())
                    {
                        int read;
                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                        {
                            md5.TransformBlock(buffer, 0, read, null, 0);
                            pauseEvent.Wait(token);
                            token.ThrowIfCancellationRequested();
                        }
                        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        hashBytes = md5.Hash!;
                    }
                    break;

                case MainViewModel.CompareAlgorithm.SHA256:
                    using (var sha256 = SHA256.Create())
                    {

                        int read;
                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                        {
                            sha256.TransformBlock(buffer, 0, read, null, 0);
                            pauseEvent.Wait(token);
                            token.ThrowIfCancellationRequested();
                        }
                        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        hashBytes = sha256.Hash!;
                    }
                    break;

                case MainViewModel.CompareAlgorithm.SHA512:
                    using (var sha512 = SHA512.Create())
                    {
                        int read;
                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                        {
                            sha512.TransformBlock(buffer, 0, read, null, 0);
                            pauseEvent.Wait(token);
                            token.ThrowIfCancellationRequested();
                        }
                        sha512.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        hashBytes = sha512.Hash!;
                    }
                    break;

                default:
                    throw new Exception("Unknown algorithm");
            }

            // Convert hash bytes to lowercase hex string
            HashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            State = FileState.hashed;
        }
        catch (OperationCanceledException)
        {
            // allow cancellation to propagate silently
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