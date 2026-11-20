using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace x7anth.recorder.Services;

public sealed class DiskSpaceService : IDisposable
{
    private const double BytesPerGigabyte = 1024.0 * 1024.0 * 1024.0;

    private readonly DispatcherTimer _timer;
    private readonly string _driveRoot;
    private bool _isChecking;
    private bool _disposed;

    public event Action<string>? DiskInfoUpdated;

    public DiskSpaceService()
    {
        string tempPath = Path.GetTempPath();
        _driveRoot = Path.GetPathRoot(tempPath) ?? @"C:\";

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _timer.Tick += OnTimerTick;
    }

    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _ = RefreshAsync();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || _isChecking)
        {
            return;
        }

        _isChecking = true;
        try
        {
            string text = await Task.Run(() => ReadFormattedDiskSpace(_driveRoot));
            if (!_disposed)
            {
                DiskInfoUpdated?.Invoke(text);
            }
        }
        catch
        {
            if (!_disposed)
            {
                DiskInfoUpdated?.Invoke($"Не удалось определить свободное место на {_driveRoot}");
            }
        }
        finally
        {
            _isChecking = false;
        }
    }

    private static string ReadFormattedDiskSpace(string root)
    {
        DriveInfo drive = new(root);
        if (!drive.IsReady)
        {
            return $"Диск {root} недоступен";
        }

        double freeGb = drive.AvailableFreeSpace / BytesPerGigabyte;
        double totalGb = drive.TotalSize / BytesPerGigabyte;

        string freeFormatted = freeGb.ToString("0.0", CultureInfo.InvariantCulture);
        string totalFormatted = totalGb.ToString("0.0", CultureInfo.InvariantCulture);

        return $"Свободно на диске {drive.Name} — {freeFormatted} ГБ из {totalFormatted} ГБ";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
