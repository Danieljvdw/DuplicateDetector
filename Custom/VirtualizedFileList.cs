using DuplicateDetector.ViewModels;
using System.Collections;

namespace DuplicateDetector.Custom;

public class VirtualizedFileList : IReadOnlyList<FileEntryViewModel>
{
    private readonly List<FileEntryViewModel> source;

    public VirtualizedFileList(List<FileEntryViewModel> source)
    {
        this.source = source;
    }

    public FileEntryViewModel this[int index] => source[index];
    public int Count => source.Count;

    public IEnumerator<FileEntryViewModel> GetEnumerator() => source.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => source.GetEnumerator();
}
