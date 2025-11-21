using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Timers;

namespace DuplicateDetector.ViewModels;

public partial class DiskInfo : ObservableObject
{
    [ObservableProperty] private long totalSpace;
    [ObservableProperty] private long freeSpace;
    [ObservableProperty] private long usedSpace;
    [ObservableProperty] private double utilization;

    [ObservableProperty] private double readSpeed;
    [ObservableProperty] private double writeSpeed;
    [ObservableProperty] private double activeTime;

    private PerformanceCounter? readCounter;
    private PerformanceCounter? writeCounter;
    private PerformanceCounter? activeCounter;

    public string DriveLetter { get; set; }

    public SemaphoreSlim DiskSemaphore { get; } = new SemaphoreSlim(1, 1);

    public DiskInfo(DriveInfo drive)
    {
        DriveLetter = drive.Name.TrimEnd('\\');

        var instance = GetDiskInstanceName(DriveLetter);
        if (instance != null)
        {
            readCounter = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", instance);
            writeCounter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", instance);
            activeCounter = new PerformanceCounter("LogicalDisk", "% Disk Time", instance);

            // First call usually returns 0, so read once
            _ = readCounter.NextValue();
            _ = writeCounter.NextValue();
            _ = activeCounter.NextValue();
        }
    }

    private static string? GetDiskInstanceName(string driveLetter)
    {
        var category = new PerformanceCounterCategory("LogicalDisk");
        return category.GetInstanceNames()
                       .FirstOrDefault(i => i.Equals(driveLetter, StringComparison.OrdinalIgnoreCase));
    }

    public void Update()
    {
        var drive = new DriveInfo(DriveLetter + "\\");
        if (!drive.IsReady)
        {
            return;
        }

        // Disk space
        TotalSpace = drive.TotalSize;
        FreeSpace = drive.TotalFreeSpace;
        UsedSpace = TotalSpace - FreeSpace;
        Utilization = (double)UsedSpace / TotalSpace * 100.0;

        // Disk performance
        if (readCounter != null)
        {
            ReadSpeed = readCounter.NextValue();
        }
        if (writeCounter != null)
        {
            WriteSpeed = writeCounter.NextValue();
        }
        if (activeCounter != null)
        {
            ActiveTime = activeCounter.NextValue();
        }
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

        _updateTimer = new System.Timers.Timer(1000);
        _updateTimer.Elapsed += (s, e) => Update();
        _updateTimer.Start();
    }

    public void EnumerateDisks()
    {
        Disks.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType != DriveType.CDRom))
        {
            var info = new DiskInfo(drive);
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
        var insertWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2"));
        var removeWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 3"));

        insertWatcher.EventArrived += (s, e) => EnumerateDisks();
        removeWatcher.EventArrived += (s, e) => EnumerateDisks();

        insertWatcher.Start();
        removeWatcher.Start();
    }
}
