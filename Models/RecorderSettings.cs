namespace x7anth.recorder.Models;

public enum VideoResolution
{
    Hd720p = 720,
    FullHd1080p = 1080
}

public sealed class RecorderSettings
{
    public VideoResolution Resolution { get; set; } = VideoResolution.Hd720p;
    public int Fps { get; set; } = 30;
    public bool RecordAudio { get; set; } = false;
    public string? AudioDeviceName { get; set; }
    public int HotkeyVirtualKey { get; set; } = 0x78; // VK_F9
    public uint HotkeyModifiers { get; set; } = 0;   // None
}
