using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace x7anth.recorder.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x7A01;

    public const uint ModNone = 0x0000;
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _windowHandle;
    private HwndSource? _hwndSource;
    private bool _isRegistered;
    private bool _disposed;

    public event EventHandler? HotkeyPressed;

    public void Attach(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Invalid window handle.", nameof(windowHandle));
        }

        if (_windowHandle == windowHandle && _hwndSource is not null)
        {
            return;
        }

        DetachHook();

        _windowHandle = windowHandle;
        _hwndSource = HwndSource.FromHwnd(windowHandle);
        _hwndSource?.AddHook(WndProc);
    }

    public bool Register(uint modifiers, int virtualKey, out string? errorMessage)
    {
        errorMessage = null;

        if (_windowHandle == IntPtr.Zero)
        {
            errorMessage = "Окно ещё не инициализировано для регистрации хоткея.";
            return false;
        }

        Unregister();

        uint win32Modifiers = (modifiers & 0x0007) | ModNoRepeat;
        if (!RegisterHotKey(_windowHandle, HotkeyId, win32Modifiers, (uint)virtualKey))
        {
            int err = Marshal.GetLastWin32Error();
            errorMessage = err == 1409
                ? $"Комбинация {FormatHotkey(modifiers, virtualKey)} уже занята другой программой."
                : $"Не удалось зарегистрировать хоткей (код Win32: {err}).";
            return false;
        }

        _isRegistered = true;
        return true;
    }

    public void Unregister()
    {
        if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
            _isRegistered = false;
        }
    }

    public static uint FromWpfModifiers(ModifierKeys modifiers)
    {
        uint result = ModNone;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= ModControl;
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= ModAlt;
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= ModShift;
        }
        return result;
    }

    public static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System;
    }

    public static string FormatHotkey(uint modifiers, int virtualKey)
    {
        List<string> parts = new(4);

        if ((modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }
        if ((modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }
        if ((modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        Key key = KeyInterop.KeyFromVirtualKey(virtualKey);
        parts.Add(FormatKeyName(key));

        return string.Join(" + ", parts);
    }

    private static string FormatKeyName(Key key)
    {
        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => $"Num {key - Key.NumPad0}",
            Key.OemTilde => "~",
            Key.OemMinus => "-",
            Key.OemPlus => "+",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Capital => "CapsLock",
            Key.Next => "PageDown",
            Key.Prior => "PageUp",
            Key.Snapshot => "PrintScreen",
            _ => key.ToString()
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void DetachHook()
    {
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        DetachHook();
    }
}
