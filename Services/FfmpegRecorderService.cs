using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using x7anth.recorder.Models;

namespace x7anth.recorder.Services;

public sealed partial class FfmpegRecorderService : IDisposable
{
    private const int DesktopHorzRes = 118;
    private const int DesktopVertRes = 117;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private readonly object _syncRoot = new();
    private readonly Queue<string> _stderrTail = new(24);

    private Process? _process;
    private string? _currentTempPath;
    private bool _isStoppingRequested;
    private bool _disposed;
    private bool? _cachedDdagrabSupport;
    private string? _cachedFfmpegPathForProbe;

    public bool IsRecording
    {
        get
        {
            lock (_syncRoot)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public event Action<string>? RecordingStoppedUnexpectedly;

    public string? FindFfmpegPath()
    {
        string localCandidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(localCandidate))
        {
            return localCandidate;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Пропускаем некорректные пути в PATH
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<string>> GetAudioDevicesAsync(CancellationToken cancellationToken = default)
    {
        string? ffmpegPath = FindFfmpegPath();
        if (ffmpegPath is null)
        {
            return Array.Empty<string>();
        }

        List<string> devices = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        ProcessStartInfo psi = new()
        {
            FileName = ffmpegPath,
            Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8
        };

        using Process proc = new() { StartInfo = psi };
        if (!proc.Start())
        {
            return devices;
        }

        string stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);

        bool inAudioSection = false;
        string[] lines = stderr.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = true;
                continue;
            }

            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = false;
                continue;
            }

            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isExplicitAudio = line.Contains("(audio)", StringComparison.OrdinalIgnoreCase);
            if (!isExplicitAudio && !inAudioSection)
            {
                continue;
            }

            if (line.Contains("(video)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match match = QuotedDeviceRegex().Match(line);
            if (match.Success)
            {
                string deviceName = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(deviceName) && seen.Add(deviceName))
                {
                    devices.Add(deviceName);
                }
            }
        }

        return devices;
    }

    public async Task<(bool Success, string? ErrorMessage)> StartRecordingAsync(RecorderSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? ffmpegPath = FindFfmpegPath();
        if (ffmpegPath is null)
        {
            return (false, "Файл ffmpeg.exe не найден рядом с программой или в системном PATH.");
        }

        lock (_syncRoot)
        {
            if (_process is { HasExited: false })
            {
                return (false, "Запись уже выполняется.");
            }
        }

        string tempFileName = $"x7anth_rec_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
        string tempOutputPath = Path.Combine(Path.GetTempPath(), tempFileName);

        (int srcWidth, int srcHeight) = GetPrimaryMonitorPhysicalSize();
        (int targetWidth, int targetHeight) = settings.Resolution == VideoResolution.FullHd1080p
            ? (1920, 1080)
            : (1280, 720);

        int fps = settings.Fps == 60 ? 60 : 30;
        string? audioDevice = settings.RecordAudio && !string.IsNullOrWhiteSpace(settings.AudioDeviceName)
            ? settings.AudioDeviceName
            : null;

        bool canUseDdagrab = await SupportsDdagrabAsync(ffmpegPath);
        if (canUseDdagrab)
        {
            string ddagrabArgs = BuildDdagrabArguments(targetWidth, targetHeight, fps, audioDevice, tempOutputPath);
            (bool ok, string? ddaErr) = await TryLaunchFfmpegAsync(ffmpegPath, ddagrabArgs, tempOutputPath);
            if (ok)
            {
                return (true, null);
            }

            // Если DXGI Desktop Duplication не поддерживается на этом адаптере/сессии — откатываемся на DPI-aware gdigrab
            _cachedDdagrabSupport = false;
            _ = ddaErr;
        }

        string gdiArgs = BuildGdigrabArguments(srcWidth, srcHeight, targetWidth, targetHeight, fps, audioDevice, tempOutputPath);
        return await TryLaunchFfmpegAsync(ffmpegPath, gdiArgs, tempOutputPath);
    }

    private async Task<(bool Success, string? ErrorMessage)> TryLaunchFfmpegAsync(
        string ffmpegPath,
        string arguments,
        string tempOutputPath)
    {
        lock (_syncRoot)
        {
            _stderrTail.Clear();
            _isStoppingRequested = false;
        }

        ProcessStartInfo psi = new()
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Заставляем ffmpeg.exe учитывать реальное физическое разрешение при масштабировании Windows (125%/150%)
        psi.Environment["__COMPAT_LAYER"] = "HIGHDPIAWARE";

        Process process = new()
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        process.ErrorDataReceived += OnProcessErrorDataReceived;
        process.Exited += OnProcessExited;

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return (false, "Не удалось запустить процесс ffmpeg.exe.");
            }

            lock (_syncRoot)
            {
                _process = process;
                _currentTempPath = tempOutputPath;
            }

            process.BeginErrorReadLine();

            await Task.Delay(400);

            if (process.HasExited)
            {
                string errorDetails = GetStderrSummary();
                CleanupProcessReference(process);
                TryDeleteFile(tempOutputPath);

                string message = string.IsNullOrWhiteSpace(errorDetails)
                    ? $"FFmpeg завершился сразу с кодом {process.ExitCode}."
                    : $"Ошибка FFmpeg: {errorDetails}";

                return (false, message);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            CleanupProcessReference(process);
            TryDeleteFile(tempOutputPath);
            return (false, $"Ошибка запуска FFmpeg: {ex.Message}");
        }
    }

    private async Task<bool> SupportsDdagrabAsync(string ffmpegPath)
    {
        if (_cachedDdagrabSupport.HasValue &&
            string.Equals(_cachedFfmpegPathForProbe, ffmpegPath, StringComparison.OrdinalIgnoreCase))
        {
            return _cachedDdagrabSupport.Value;
        }

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -h filter=ddagrab",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using Process proc = new() { StartInfo = psi };
            if (!proc.Start())
            {
                return false;
            }

            string stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            bool supported = proc.ExitCode == 0 && stdout.Contains("ddagrab", StringComparison.OrdinalIgnoreCase);
            _cachedFfmpegPathForProbe = ffmpegPath;
            _cachedDdagrabSupport = supported;
            return supported;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(string? TempFilePath, string? ErrorMessage)> StopRecordingAsync()
    {
        Process? process;
        string? tempPath;

        lock (_syncRoot)
        {
            process = _process;
            tempPath = _currentTempPath;
            _isStoppingRequested = true;
        }

        if (process is null)
        {
            return (null, "Активный процесс записи не найден.");
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("q");
                    await process.StandardInput.FlushAsync();
                }
                catch
                {
                    // Если stdin уже закрыт, переходим к ожиданию завершения
                }

                using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(10));
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                    catch
                    {
                        // Игнорируем ошибку принудительного завершения
                    }
                }
            }
        }
        finally
        {
            CleanupProcessReference(process);
        }

        if (string.IsNullOrEmpty(tempPath) || !File.Exists(tempPath))
        {
            string details = GetStderrSummary();
            return (null, string.IsNullOrWhiteSpace(details)
                ? "Временный видеофайл не был создан."
                : $"Ошибка записи: {details}");
        }

        FileInfo fi = new(tempPath);
        if (fi.Length == 0)
        {
            TryDeleteFile(tempPath);
            return (null, "Записанный файл оказался пустым (0 байт).");
        }

        return (tempPath, null);
    }

    private static string BuildDdagrabArguments(
        int targetWidth,
        int targetHeight,
        int fps,
        string? audioDeviceName,
        string outputPath)
    {
        StringBuilder sb = new();
        sb.Append("-hide_banner -loglevel error ");
        sb.Append($"-f lavfi -thread_queue_size 1024 -i \"ddagrab=framerate={fps}:output_idx=0:draw_mouse=1\" ");

        if (!string.IsNullOrWhiteSpace(audioDeviceName))
        {
            string escapedDevice = audioDeviceName.Replace("\"", "\\\"");
            sb.Append($"-f dshow -thread_queue_size 1024 -i audio=\"{escapedDevice}\" ");
            sb.Append("-map 0:v:0 -map 1:a:0 ");
        }

        sb.Append($"-vf \"hwdownload,format=bgra,scale={targetWidth}:{targetHeight}:flags=fast_bilinear,setsar=1,format=yuv420p\" ");
        sb.Append($"-fps_mode cfr -r {fps} ");

        int crf = targetHeight >= 1080 ? 26 : 24;
        int keyframeInterval = fps * 2;
        sb.Append($"-c:v libx264 -preset ultrafast -tune zerolatency -crf {crf} -g {keyframeInterval} ");

        if (!string.IsNullOrWhiteSpace(audioDeviceName))
        {
            sb.Append("-c:a aac -b:a 128k -ac 2 ");
        }

        sb.Append($"-y \"{outputPath}\"");
        return sb.ToString();
    }

    private static string BuildGdigrabArguments(
        int srcWidth,
        int srcHeight,
        int targetWidth,
        int targetHeight,
        int fps,
        string? audioDeviceName,
        string outputPath)
    {
        StringBuilder sb = new();
        sb.Append("-hide_banner -loglevel error ");
        sb.Append($"-f gdigrab -thread_queue_size 1024 -framerate {fps} -draw_mouse 1 -offset_x 0 -offset_y 0 -video_size {srcWidth}x{srcHeight} -i desktop ");

        if (!string.IsNullOrWhiteSpace(audioDeviceName))
        {
            string escapedDevice = audioDeviceName.Replace("\"", "\\\"");
            sb.Append($"-f dshow -thread_queue_size 1024 -i audio=\"{escapedDevice}\" ");
            sb.Append("-map 0:v:0 -map 1:a:0 ");
        }

        sb.Append($"-vf \"scale={targetWidth}:{targetHeight}:flags=fast_bilinear,setsar=1,format=yuv420p\" ");
        sb.Append($"-vsync cfr -r {fps} ");

        int crf = targetHeight >= 1080 ? 26 : 24;
        int keyframeInterval = fps * 2;
        sb.Append($"-c:v libx264 -preset ultrafast -tune zerolatency -crf {crf} -g {keyframeInterval} ");

        if (!string.IsNullOrWhiteSpace(audioDeviceName))
        {
            sb.Append("-c:a aac -b:a 128k -ac 2 ");
        }

        sb.Append($"-y \"{outputPath}\"");
        return sb.ToString();
    }

    private static (int Width, int Height) GetPrimaryMonitorPhysicalSize()
    {
        int width = 0;
        int height = 0;

        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc != IntPtr.Zero)
        {
            try
            {
                width = GetDeviceCaps(hdc, DesktopHorzRes);
                height = GetDeviceCaps(hdc, DesktopVertRes);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        if (width <= 0 || height <= 0)
        {
            width = GetSystemMetrics(SmCxScreen);
            height = GetSystemMetrics(SmCyScreen);
        }

        if (width <= 0 || height <= 0)
        {
            width = 1920;
            height = 1080;
        }

        return (width & ~1, height & ~1);
    }

    private void OnProcessErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_stderrTail.Count >= 20)
            {
                _stderrTail.Dequeue();
            }
            _stderrTail.Enqueue(e.Data.Trim());
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        bool wasUnexpected;
        string? tempPath;

        lock (_syncRoot)
        {
            wasUnexpected = !_isStoppingRequested;
            tempPath = _currentTempPath;
        }

        if (sender is Process proc)
        {
            CleanupProcessReference(proc);
        }

        if (wasUnexpected && !_disposed)
        {
            TryDeleteFile(tempPath);
            string summary = GetStderrSummary();
            string message = string.IsNullOrWhiteSpace(summary)
                ? "Процесс FFmpeg неожиданно завершился."
                : $"FFmpeg остановлен с ошибкой: {summary}";
            RecordingStoppedUnexpectedly?.Invoke(message);
        }
    }

    private void CleanupProcessReference(Process process)
    {
        lock (_syncRoot)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _currentTempPath = null;
            }
        }

        process.ErrorDataReceived -= OnProcessErrorDataReceived;
        process.Exited -= OnProcessExited;
        process.Dispose();
    }

    private string GetStderrSummary()
    {
        lock (_syncRoot)
        {
            return _stderrTail.Count == 0 ? string.Empty : string.Join(" | ", _stderrTail);
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Игнорируем ошибку удаления временного файла
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Process? proc;
        lock (_syncRoot)
        {
            proc = _process;
            _isStoppingRequested = true;
        }

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.StandardInput.WriteLine("q");
                    proc.StandardInput.Flush();
                    if (!proc.WaitForExit(3000))
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
            }
            catch
            {
                // Игнорируем при завершении приложения
            }
            finally
            {
                CleanupProcessReference(proc);
            }
        }
    }

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedDeviceRegex();
}
