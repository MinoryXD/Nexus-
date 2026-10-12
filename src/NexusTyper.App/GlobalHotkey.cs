using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NexusTyper.App;

public sealed class GlobalHotkey : IDisposable
{
    private const int StartHotkeyId = 1;
    private const int StopHotkeyId = 2;
    private const int WmHotkey = 0x0312;
    private const uint VirtualKeyF6 = 0x75;
    private const uint VirtualKeyF7 = 0x76;

    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;

    private HwndSource? _source;
    private nint _handle;
    private nint _hookId;
    private LowLevelKeyboardProc? _proc;
    private long _lastStartTicks;
    private long _lastStopTicks;

    public event EventHandler? StartPressed;
    public event EventHandler? StopPressed;

    public void Register(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        if (_source is not null)
        {
            _source.AddHook(WindowProcedure);
            RegisterHotKey(_handle, StartHotkeyId, 0, VirtualKeyF6);
            RegisterHotKey(_handle, StopHotkeyId, 0, VirtualKeyF7);
        }

        // Low-level global keyboard hook for reliable global F6/F7 capture across all windows
        _proc = HookCallback;
        nint hMod = 0;
        try
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            if (curModule?.ModuleName != null)
            {
                hMod = GetModuleHandle(curModule.ModuleName);
            }
        }
        catch
        {
            // Ignore process inspection errors
        }

        if (hMod == 0)
        {
            hMod = GetModuleHandle(null);
        }

        _hookId = SetWindowsHookEx(WhKeyboardLl, _proc, hMod, 0);
    }

    private void TriggerStart()
    {
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastStartTicks, now).TotalMilliseconds < 350)
        {
            return;
        }
        _lastStartTicks = now;
        StartPressed?.Invoke(this, EventArgs.Empty);
    }

    private void TriggerStop()
    {
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastStopTicks, now).TotalMilliseconds < 350)
        {
            return;
        }
        _lastStopTicks = now;
        StopPressed?.Invoke(this, EventArgs.Empty);
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == WmKeydown || wParam == WmSyskeydown))
        {
            var vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == (int)VirtualKeyF6)
            {
                TriggerStart();
            }
            else if (vkCode == (int)VirtualKeyF7)
            {
                TriggerStop();
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != 0)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = 0;
        }

        if (_handle != 0)
        {
            UnregisterHotKey(_handle, StartHotkeyId);
            UnregisterHotKey(_handle, StopHotkeyId);
            _handle = 0;
        }

        if (_source is not null)
        {
            _source.RemoveHook(WindowProcedure);
            _source = null;
        }
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam == StartHotkeyId)
        {
            TriggerStart();
            handled = true;
        }
        else if (message == WmHotkey && wParam == StopHotkeyId)
        {
            TriggerStop();
            handled = true;
        }

        return 0;
    }

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
