using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace DuplicateDetector.ViewModels;

public partial class DiskInfo : ObservableObject
{
    private PerformanceCounter readCounter;
    private PerformanceCounter writeCounter;
    private PerformanceCounter percentDiskTimeCounter;

    public string DriveLetter { get; set; }     // e.g. "C:"
    public string DriveType { get; set; }       // e.g. "Fixed"
    public string MediaType { get; set; }       // e.g. "SSD", "NVMe", "HDD"

    // Disk space stats (in bytes) — converted in the view with ByteSizeConverter
    [ObservableProperty] private long totalSpace;
    [ObservableProperty] private long freeSpace;
    [ObservableProperty] private long usedSpace;

    // Utilization (percentage of used space)
    [ObservableProperty] double utilization = 0.0d;

    // % busy (Task Manager style)
    [ObservableProperty] double activeTime = 0.0d;

    // Disk performance metrics (in MB/s)
    [ObservableProperty] double readSpeed = 0.0d;
    [ObservableProperty] double writeSpeed = 0.0d;

    public DiskInfo(string driveLetter, string driveType)
    {
        DriveLetter = driveLetter;
        DriveType = driveType;
        MediaType = GetMediaType(driveLetter);

        string instanceName = DriveLetter.TrimEnd('\\');
        string physInstance = GetPhysicalDiskInstanceName();

        readCounter = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", instanceName);
        writeCounter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", instanceName);
        percentDiskTimeCounter = new PerformanceCounter("PhysicalDisk", "PercentDiskTime", physInstance);
    }

    public void Update()
    {
        var drive = new DriveInfo(DriveLetter + "\\");
        if (!drive.IsReady)
        {
            return;
        }

        TotalSpace = drive.TotalSize;
        FreeSpace = drive.TotalFreeSpace;
        UsedSpace = TotalSpace - FreeSpace;
        Utilization = (double)UsedSpace / TotalSpace * 100.0;

        if (readCounter != null && writeCounter != null && percentDiskTimeCounter != null)
        {
            ReadSpeed = readCounter.NextValue();
            WriteSpeed = writeCounter.NextValue();
            ActiveTime = percentDiskTimeCounter.NextValue();
        }
    }

    private string GetMediaType(string driveLetter)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT MediaType, Model FROM Win32_DiskDrive");

            foreach (ManagementObject drive in searcher.Get())
            {
                string mediaType = drive["MediaType"]?.ToString() ?? "";
                string model = drive["Model"]?.ToString() ?? "";

                if (mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("SSD", StringComparison.OrdinalIgnoreCase))
                {
                    return "SSD";
                }

                if (model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                {
                    return "NVMe";
                }
            }
        }
        catch { }

        return "HDD";
    }

    private string GetPhysicalDiskInstanceName()
    {
        // Remove the colon (C: -> C)
        string letter = DriveLetter.TrimEnd('\\').Replace(":", "");

        var category = new PerformanceCounterCategory("PhysicalDisk");
        var instances = category.GetInstanceNames();

        // Usually instances are like "0 C:", "1 D:", or just "0", "1"
        return instances.FirstOrDefault(i => i.EndsWith($" {letter}", StringComparison.OrdinalIgnoreCase))
               ?? instances.FirstOrDefault(i => i.Equals(letter, StringComparison.OrdinalIgnoreCase))
               ?? "_Total"; // fallback
    }
}

public partial class DiskViewModel : ObservableObject
{
    private readonly System.Timers.Timer _updateTimer;

    public ObservableCollection<DiskInfo> Disks { get; } = new ObservableCollection<DiskInfo>();

    public DiskViewModel()
    {
        EnumerateDisks();
        SetupDriveEventWatchers();

        // Update every 1 second (can be called externally too)
        _updateTimer = new System.Timers.Timer(1000);
        _updateTimer.Elapsed += (s, e) => Update();
        _updateTimer.Start();
    }

    public void EnumerateDisks()
    {
        Disks.Clear();

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType != DriveType.CDRom))
        {
            var info = new DiskInfo(drive.Name.TrimEnd('\\'), drive.DriveType.ToString());
            Disks.Add(info);
        }
    }

    public void Update()
    {
        foreach (var disk in Disks)
        {
            disk.Update();
        }
    }

    private void SetupDriveEventWatchers()
    {
        // Listen for drive insert/remove
        var insertWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2")); // inserted
        var removeWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 3")); // removed

        insertWatcher.EventArrived += (s, e) => EnumerateDisks();
        removeWatcher.EventArrived += (s, e) => EnumerateDisks();

        insertWatcher.Start();
        removeWatcher.Start();
    }
}
