using System;
using System.IO;
using System.Text.Json;
using x7anth.recorder.Models;

namespace x7anth.recorder.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsFilePath;

    public SettingsService()
    {
        string appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appDataDir, "x7anth.recorder");
        _settingsFilePath = Path.Combine(appFolder, "settings.json");
    }

    public RecorderSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new RecorderSettings();
            }

            string json = File.ReadAllText(_settingsFilePath);
            RecorderSettings? loaded = JsonSerializer.Deserialize<RecorderSettings>(json, JsonOptions);
            if (loaded is null)
            {
                return new RecorderSettings();
            }

            if (loaded.Resolution != VideoResolution.Hd720p && loaded.Resolution != VideoResolution.FullHd1080p)
            {
                loaded.Resolution = VideoResolution.Hd720p;
            }

            if (loaded.Fps != 30 && loaded.Fps != 60)
            {
                loaded.Fps = 30;
            }

            if (loaded.HotkeyVirtualKey <= 0 || loaded.HotkeyVirtualKey > 0xFF)
            {
                loaded.HotkeyVirtualKey = 0x78;
                loaded.HotkeyModifiers = 0;
            }

            loaded.HotkeyModifiers &= 0x0007; // Only Alt (1), Ctrl (2), Shift (4)
            return loaded;
        }
        catch
        {
            return new RecorderSettings();
        }
    }

    public void Save(RecorderSettings settings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // Игнорируем ошибку записи настроек, чтобы не прерывать работу приложения
        }
    }
}
