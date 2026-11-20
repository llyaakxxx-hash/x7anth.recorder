using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace x7anth.recorder;

public partial class RecordingOverlayWindow : Window
{
    private readonly DispatcherTimer _durationTimer;
    private readonly DispatcherTimer _autoHideTimer;
    private DateTime _recordingStartUtc;

    public RecordingOverlayWindow()
    {
        InitializeComponent();

        _durationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _durationTimer.Tick += OnDurationTimerTick;

        _autoHideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _autoHideTimer.Tick += OnAutoHideTimerTick;

        Loaded += OnWindowLoaded;
    }

    public void ShowForBriefNotice(DateTime recordingStartUtc)
    {
        _recordingStartUtc = recordingStartUtc;
        UpdateTimerDisplay();

        _durationTimer.Start();
        _autoHideTimer.Stop();
        _autoHideTimer.Start();

        if (!IsVisible)
        {
            Show();
        }

        PositionTopRight();
    }

    public void StopAndHide()
    {
        _autoHideTimer.Stop();
        _durationTimer.Stop();

        if (IsVisible)
        {
            Hide();
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        PositionTopRight();
    }

    private void PositionTopRight()
    {
        Rect workArea = SystemParameters.WorkArea;
        if (ActualWidth > 0)
        {
            Left = workArea.Right - ActualWidth - 12;
            Top = workArea.Top + 12;
        }
    }

    private void OnDurationTimerTick(object? sender, EventArgs e)
    {
        UpdateTimerDisplay();
    }

    private void UpdateTimerDisplay()
    {
        TimeSpan elapsed = DateTime.UtcNow - _recordingStartUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        TimerTextBlock.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private void OnAutoHideTimerTick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        _durationTimer.Stop();
        Hide();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoHideTimer.Stop();
        _durationTimer.Stop();
        _autoHideTimer.Tick -= OnAutoHideTimerTick;
        _durationTimer.Tick -= OnDurationTimerTick;
        base.OnClosed(e);
    }
}
