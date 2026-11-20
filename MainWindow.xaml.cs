using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using x7anth.recorder.Models;
using x7anth.recorder.Services;

namespace x7anth.recorder;

public partial class MainWindow : Window
{
    private const string GitHubUrl = "https://github.com/llyaakxxx-hash";
    private const string TelegramUrl = "https://t.me/x7anth";

    private readonly SettingsService _settingsService = new();
    private readonly DiskSpaceService _diskSpaceService = new();
    private readonly HotkeyService _hotkeyService = new();
    private readonly FfmpegRecorderService _recorderService = new();
    private readonly DispatcherTimer _uiRecordingTimer;

    private RecorderSettings _settings;
    private RecordingOverlayWindow? _overlayWindow;
    private DateTime _recordingStartUtc;
    private bool _isCapturingHotkey;
    private bool _isBusyTransition;
    private bool _isInitializingAudioUi;
    private bool _isApplyingUi;

    public MainWindow()
    {
        InitializeComponent();

        _settings = _settingsService.Load();

        _uiRecordingTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uiRecordingTimer.Tick += OnUiRecordingTimerTick;

        _diskSpaceService.DiskInfoUpdated += OnDiskInfoUpdated;
        _hotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;
        _recorderService.RecordingStoppedUnexpectedly += OnRecordingStoppedUnexpectedly;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnWindowLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _hotkeyService.Attach(hwnd);
        RegisterConfiguredHotkey();
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplySettingsToUi();
        _diskSpaceService.Start();
        UpdateFfmpegAvailabilityBanner();

        if (_settings.RecordAudio)
        {
            await RefreshAudioDevicesAsync();
        }
    }

    private void ApplySettingsToUi()
    {
        _isApplyingUi = true;
        try
        {
            Res720Button.IsChecked = _settings.Resolution == VideoResolution.Hd720p;
            Res1080Button.IsChecked = _settings.Resolution == VideoResolution.FullHd1080p;

            Fps30Button.IsChecked = _settings.Fps != 60;
            Fps60Button.IsChecked = _settings.Fps == 60;

            AudioOffButton.IsChecked = !_settings.RecordAudio;
            AudioOnButton.IsChecked = _settings.RecordAudio;
            AudioDevicePanel.Visibility = _settings.RecordAudio ? Visibility.Visible : Visibility.Collapsed;

            UpdateHotkeyLabels();
            UpdateReadyStatusText();
        }
        finally
        {
            _isApplyingUi = false;
        }
    }

    private void UpdateReadyStatusText()
    {
        if (_recorderService.IsRecording)
        {
            return;
        }

        string resLabel = _settings.Resolution == VideoResolution.FullHd1080p ? "1080p" : "720p";
        StatusTextBlock.Text = $"Готов к записи • {resLabel} • {_settings.Fps} FPS • Основной монитор";
    }

    private void UpdateHotkeyLabels()
    {
        string hotkeyText = HotkeyService.FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
        if (!_isCapturingHotkey)
        {
            HotkeyBindButton.Content = hotkeyText;
        }

        UpdateRecordButtonContent();
    }

    private void UpdateRecordButtonContent()
    {
        string hotkeyText = HotkeyService.FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
        if (_recorderService.IsRecording)
        {
            string elapsed = FormatElapsed(DateTime.UtcNow - _recordingStartUtc);
            RecordToggleButton.Content = $"Остановить запись — {elapsed} ({hotkeyText})";
            RecordToggleButton.Tag = "Recording";
        }
        else
        {
            RecordToggleButton.Content = $"Начать запись ({hotkeyText})";
            RecordToggleButton.Tag = "Idle";
        }
    }

    private void RegisterConfiguredHotkey()
    {
        if (!_hotkeyService.Register(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey, out string? error))
        {
            StatusTextBlock.Text = error ?? "Не удалось зарегистрировать горячую клавишу.";
        }
    }

    private bool UpdateFfmpegAvailabilityBanner()
    {
        bool available = _recorderService.FindFfmpegPath() is not null;
        FfmpegMissingBanner.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        return available;
    }

    private void OnDiskInfoUpdated(string formattedText)
    {
        Dispatcher.Invoke(() =>
        {
            DiskSpaceTextBlock.Text = formattedText;
        });
    }

    private void NavigationTab_Checked(object sender, RoutedEventArgs e)
    {
        if (RecordTabPanel is null || AuthorTabPanel is null)
        {
            return;
        }

        if (TabRecordButton.IsChecked == true)
        {
            RecordTabPanel.Visibility = Visibility.Visible;
            AuthorTabPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            RecordTabPanel.Visibility = Visibility.Collapsed;
            AuthorTabPanel.Visibility = Visibility.Visible;
        }
    }

    private void ResolutionOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingUi || !IsLoaded)
        {
            return;
        }

        _settings.Resolution = Res1080Button.IsChecked == true
            ? VideoResolution.FullHd1080p
            : VideoResolution.Hd720p;
        _settingsService.Save(_settings);
        UpdateReadyStatusText();
    }

    private void FpsOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingUi || !IsLoaded)
        {
            return;
        }

        _settings.Fps = Fps60Button.IsChecked == true ? 60 : 30;
        _settingsService.Save(_settings);
        UpdateReadyStatusText();
    }

    private async void AudioOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingUi || !IsLoaded)
        {
            return;
        }

        bool enabled = AudioOnButton.IsChecked == true;
        _settings.RecordAudio = enabled;
        AudioDevicePanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _settingsService.Save(_settings);

        if (enabled && AudioDevicesComboBox.Items.Count == 0)
        {
            await RefreshAudioDevicesAsync();
        }
    }

    private async void RefreshAudioDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAudioDevicesAsync();
    }

    private async Task RefreshAudioDevicesAsync()
    {
        RefreshAudioDevicesButton.IsEnabled = false;
        AudioHintTextBlock.Visibility = Visibility.Collapsed;

        try
        {
            IReadOnlyList<string> devices = await _recorderService.GetAudioDevicesAsync();
            _isInitializingAudioUi = true;
            AudioDevicesComboBox.Items.Clear();

            if (devices.Count == 0)
            {
                AudioDevicesComboBox.IsEnabled = false;
                _settings.AudioDeviceName = null;
                AudioHintTextBlock.Text = _recorderService.FindFfmpegPath() is null
                    ? "Для поиска аудиоустройств требуется ffmpeg.exe."
                    : "Аудиоустройства не найдены (запись пойдёт без звука).";
                AudioHintTextBlock.Visibility = Visibility.Visible;
            }
            else
            {
                AudioDevicesComboBox.IsEnabled = true;
                foreach (string device in devices)
                {
                    AudioDevicesComboBox.Items.Add(device);
                }

                int selectedIndex = 0;
                if (!string.IsNullOrWhiteSpace(_settings.AudioDeviceName))
                {
                    for (int i = 0; i < devices.Count; i++)
                    {
                        if (string.Equals(devices[i], _settings.AudioDeviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                }

                AudioDevicesComboBox.SelectedIndex = selectedIndex;
                _settings.AudioDeviceName = devices[selectedIndex];
            }

            _settingsService.Save(_settings);
        }
        catch
        {
            AudioDevicesComboBox.IsEnabled = false;
            AudioHintTextBlock.Text = "Не удалось получить список аудиоустройств.";
            AudioHintTextBlock.Visibility = Visibility.Visible;
        }
        finally
        {
            _isInitializingAudioUi = false;
            RefreshAudioDevicesButton.IsEnabled = true;
        }
    }

    private void AudioDevicesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingAudioUi)
        {
            return;
        }

        if (AudioDevicesComboBox.SelectedItem is string deviceName)
        {
            _settings.AudioDeviceName = deviceName;
            _settingsService.Save(_settings);
        }
    }

    private async void RecheckFfmpegButton_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateFfmpegAvailabilityBanner())
        {
            StatusTextBlock.Text = "FFmpeg найден • Готов к записи";
            if (_settings.RecordAudio)
            {
                await RefreshAudioDevicesAsync();
            }
        }
        else
        {
            StatusTextBlock.Text = "ffmpeg.exe всё ещё не найден.";
        }
    }

    private void HotkeyBindButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recorderService.IsRecording || _isBusyTransition)
        {
            return;
        }

        _isCapturingHotkey = true;
        _hotkeyService.Unregister();
        HotkeyBindButton.Content = "Нажмите клавишу...";
        StatusTextBlock.Text = "Нажмите новую клавишу или комбинацию (Esc — отмена)";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            _isCapturingHotkey = false;
            RegisterConfiguredHotkey();
            UpdateHotkeyLabels();
            StatusTextBlock.Text = "Изменение горячей клавиши отменено";
            return;
        }

        if (HotkeyService.IsModifierKey(key))
        {
            return;
        }

        uint modifiers = HotkeyService.FromWpfModifiers(Keyboard.Modifiers);
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);

        if (virtualKey <= 0)
        {
            return;
        }

        if (_hotkeyService.Register(modifiers, virtualKey, out string? error))
        {
            _settings.HotkeyModifiers = modifiers;
            _settings.HotkeyVirtualKey = virtualKey;
            _settingsService.Save(_settings);

            _isCapturingHotkey = false;
            UpdateHotkeyLabels();
            StatusTextBlock.Text = $"Назначена клавиша: {HotkeyService.FormatHotkey(modifiers, virtualKey)}";
        }
        else
        {
            // Возвращаем предыдущую комбинацию при конфликте
            RegisterConfiguredHotkey();
            _isCapturingHotkey = false;
            UpdateHotkeyLabels();
            StatusTextBlock.Text = error ?? "Не удалось назначить клавишу.";
        }
    }

    private async void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        if (_isCapturingHotkey)
        {
            return;
        }

        await ToggleRecordingAsync();
    }

    private async void RecordToggleButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleRecordingAsync();
    }

    private async Task ToggleRecordingAsync()
    {
        if (_isBusyTransition)
        {
            return;
        }

        if (_recorderService.IsRecording)
        {
            await StopRecordingFlowAsync();
        }
        else
        {
            await StartRecordingFlowAsync();
        }
    }

    private async Task StartRecordingFlowAsync()
    {
        if (!UpdateFfmpegAvailabilityBanner())
        {
            StatusTextBlock.Text = "Невозможно начать запись: отсутствует ffmpeg.exe";
            return;
        }

        _isBusyTransition = true;
        RecordToggleButton.IsEnabled = false;
        SetSettingsControlsEnabled(false);
        StatusTextBlock.Text = "Запуск записи...";

        try
        {
            (bool success, string? error) = await _recorderService.StartRecordingAsync(_settings);
            if (!success)
            {
                SetSettingsControlsEnabled(true);
                StatusTextBlock.Text = error ?? "Не удалось запустить запись.";
                return;
            }

            _recordingStartUtc = DateTime.UtcNow;
            _uiRecordingTimer.Start();
            UpdateRecordButtonContent();
            StatusTextBlock.Text = "Идёт запись • 00:00";

            _overlayWindow ??= new RecordingOverlayWindow();
            _overlayWindow.ShowForBriefNotice(_recordingStartUtc);
        }
        finally
        {
            _isBusyTransition = false;
            RecordToggleButton.IsEnabled = true;
        }
    }

    private async Task StopRecordingFlowAsync()
    {
        _isBusyTransition = true;
        RecordToggleButton.IsEnabled = false;
        _uiRecordingTimer.Stop();
        _overlayWindow?.StopAndHide();
        StatusTextBlock.Text = "Завершение и сохранение MP4...";

        try
        {
            (string? tempPath, string? error) = await _recorderService.StopRecordingAsync();

            SetSettingsControlsEnabled(true);
            UpdateRecordButtonContent();

            if (string.IsNullOrEmpty(tempPath))
            {
                StatusTextBlock.Text = error ?? "Ошибка при завершении записи.";
                return;
            }

            HandlePostRecordingSave(tempPath);
        }
        finally
        {
            _isBusyTransition = false;
            RecordToggleButton.IsEnabled = true;
        }
    }

    private void HandlePostRecordingSave(string tempFilePath)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();

        string finalFileName = $"x7anth.recorder_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";

        OpenFolderDialog folderDialog = new()
        {
            Title = "Выберите папку для сохранения записи",
            Multiselect = false
        };

        bool? dialogResult = folderDialog.ShowDialog(this);
        string? targetFolder = dialogResult == true ? folderDialog.FolderName : null;

        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            MessageBoxResult choice = MessageBox.Show(
                this,
                "Папка для сохранения не выбрана. Сохранить запись на Рабочий стол?\n\nДа — сохранить на Рабочий стол\nНет — удалить временный файл",
                "x7anth.recorder",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (choice == MessageBoxResult.Yes)
            {
                targetFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            else
            {
                TryDeleteTempFile(tempFilePath);
                StatusTextBlock.Text = "Запись отменена, временный файл удалён";
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(targetFolder);
            string finalPath = Path.Combine(targetFolder, finalFileName);
            File.Move(tempFilePath, finalPath, overwrite: true);

            StatusTextBlock.Text = $"Сохранено: {finalFileName}";

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{finalPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Не удалось переместить файл: {ex.Message}";
        }
    }

    private void OnRecordingStoppedUnexpectedly(string errorMessage)
    {
        Dispatcher.Invoke(() =>
        {
            _uiRecordingTimer.Stop();
            _overlayWindow?.StopAndHide();
            SetSettingsControlsEnabled(true);
            UpdateRecordButtonContent();
            StatusTextBlock.Text = errorMessage;
        });
    }

    private void OnUiRecordingTimerTick(object? sender, EventArgs e)
    {
        if (!_recorderService.IsRecording)
        {
            return;
        }

        string elapsed = FormatElapsed(DateTime.UtcNow - _recordingStartUtc);
        StatusTextBlock.Text = $"Идёт запись • {elapsed}";
        UpdateRecordButtonContent();
    }

    private void SetSettingsControlsEnabled(bool isEnabled)
    {
        Res720Button.IsEnabled = isEnabled;
        Res1080Button.IsEnabled = isEnabled;
        Fps30Button.IsEnabled = isEnabled;
        Fps60Button.IsEnabled = isEnabled;
        AudioOffButton.IsEnabled = isEnabled;
        AudioOnButton.IsEnabled = isEnabled;
        AudioDevicesComboBox.IsEnabled = isEnabled && AudioDevicesComboBox.Items.Count > 0;
        RefreshAudioDevicesButton.IsEnabled = isEnabled;
        HotkeyBindButton.IsEnabled = isEnabled;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Игнорируем ошибку удаления
        }
    }

    private void GitHubLinkButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(GitHubUrl);
    }

    private void TelegramLinkButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(TelegramUrl);
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Игнорируем ошибку открытия браузера
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _uiRecordingTimer.Stop();
        _uiRecordingTimer.Tick -= OnUiRecordingTimerTick;

        _diskSpaceService.DiskInfoUpdated -= OnDiskInfoUpdated;
        _diskSpaceService.Dispose();

        _hotkeyService.HotkeyPressed -= OnGlobalHotkeyPressed;
        _hotkeyService.Dispose();

        _recorderService.RecordingStoppedUnexpectedly -= OnRecordingStoppedUnexpectedly;
        _recorderService.Dispose();

        if (_overlayWindow is not null)
        {
            _overlayWindow.StopAndHide();
            _overlayWindow.Close();
            _overlayWindow = null;
        }
    }
}
