using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace MouseKeeper;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int HotkeyId = 0x4D4B;
    private const int ModifierAlt = 0x0001;
    private const int ModifierControl = 0x0002;
    private const int ModifierShift = 0x0004;
    private const int ModifierControlAlt = ModifierControl | ModifierAlt;
    private const int ModifierControlShift = ModifierControl | ModifierShift;
    private const int ModifierAltShift = ModifierAlt | ModifierShift;
    private const int KeyK = 0x4B;
    private const int KeyM = 0x4D;
    private const int WmHotkey = 0x0312;
    private const int WhMouseLl = 14;
    private const int LlmhfInjected = 0x00000001;
    private const int MouseEventFMove = 0x0001;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(32);

    private readonly DispatcherTimer _timer;
    private readonly IntPtr _windowHandle;
    private readonly SUBCLASSPROC _subclassProc;
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _hookHandle;
    private DateTimeOffset _lastRealMouseInput = DateTimeOffset.Now;
    private bool _hotkeyRegistered;
    private bool _isEnabled;
    private bool _isMoving;
    private double _phase;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();

        _windowHandle = WindowNative.GetWindowHandle(this);
        _subclassProc = WindowSubclassProc;
        _mouseProc = MouseHookProc;

        ResizeWindow();
        RegisterBestAvailableHotkey();
        SetWindowSubclass(_windowHandle, _subclassProc, 1, IntPtr.Zero);
        _hookHandle = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);

        _timer = new DispatcherTimer { Interval = FrameInterval };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        Closed += MainWindow_Closed;
        RefreshStatus();
    }

    public string StatusTitle { get; private set; } = string.Empty;

    public string StatusDescription { get; private set; } = string.Empty;

    public string ToggleButtonText { get; private set; } = string.Empty;

    public string ShortcutText { get; private set; } = string.Empty;

    public double IdleProgress { get; private set; }

    public string IdleProgressText { get; private set; } = string.Empty;

    public Brush StatusBrush { get; private set; } = new SolidColorBrush(Colors.Gray);

    private void ResizeWindow()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(520, 560));
    }

    private void RegisterBestAvailableHotkey()
    {
        var candidates = new[]
        {
            (Modifiers: ModifierControlAlt, Key: KeyM, Text: "Strg + Alt + M"),
            (Modifiers: ModifierControlShift, Key: KeyM, Text: "Strg + Shift + M"),
            (Modifiers: ModifierControlAlt, Key: KeyK, Text: "Strg + Alt + K"),
            (Modifiers: ModifierAltShift, Key: KeyM, Text: "Alt + Shift + M")
        };

        foreach (var candidate in candidates)
        {
            if (RegisterHotKey(_windowHandle, HotkeyId, candidate.Modifiers, candidate.Key))
            {
                _hotkeyRegistered = true;
                ShortcutText = candidate.Text;
                return;
            }
        }

        _hotkeyRegistered = false;
        ShortcutText = "Kein Kurzbefehl frei";
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleKeeper();
    }

    private void ToggleKeeper()
    {
        _isEnabled = !_isEnabled;
        _isMoving = false;
        _lastRealMouseInput = DateTimeOffset.Now;
        UpdateIdleProgress(TimeSpan.Zero);
        RefreshStatus();
    }

    private void Timer_Tick(object? sender, object e)
    {
        if (!_isEnabled)
        {
            UpdateIdleProgress(TimeSpan.Zero);
            return;
        }

        var idleFor = DateTimeOffset.Now - _lastRealMouseInput;
        var shouldMove = idleFor >= IdleDelay;
        UpdateIdleProgress(idleFor);

        if (_isMoving != shouldMove)
        {
            _isMoving = shouldMove;
            RefreshStatus();
        }

        if (_isMoving)
        {
            MoveMouseGently();
        }
    }

    private void MoveMouseGently()
    {
        _phase += 0.16;
        var dx = Math.Sin(_phase) * 1.15;
        var dy = Math.Cos(_phase * 0.8) * 0.85;

        SendInput(1, new[]
        {
            new INPUT
            {
                type = 0,
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round(dx),
                    dy = (int)Math.Round(dy),
                    dwFlags = MouseEventFMove
                }
            }
        }, Marshal.SizeOf<INPUT>());
    }

    private void RefreshStatus()
    {
        StatusTitle = _isEnabled ? (_isMoving ? "Aktiv" : "Bereit") : "Ausgeschaltet";
        StatusDescription = _isEnabled
            ? (_isMoving
                ? "MouseKeeper bewegt den Zeiger sanft. Deine eigene Mausbewegung hat jederzeit Vorrang."
                : "Warte auf Mausruhe. Nach 3 Sekunden übernimmt MouseKeeper ganz dezent.")
            : _hotkeyRegistered
                ? $"Drücke den Button oder {ShortcutText}, um die Mausbewegung zu starten."
                : "Drücke den Button, um die Mausbewegung zu starten. Alle Kurzbefehl-Varianten sind gerade belegt.";
        ToggleButtonText = _isEnabled ? "MouseKeeper stoppen" : "MouseKeeper starten";
        StatusBrush = new SolidColorBrush(_isEnabled ? (_isMoving ? Colors.LimeGreen : Colors.Goldenrod) : Colors.Gray);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusTitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDescription)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShortcutText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
    }

    private void UpdateIdleProgress(TimeSpan idleFor)
    {
        IdleProgress = _isEnabled ? Math.Clamp(idleFor.TotalMilliseconds / IdleDelay.TotalMilliseconds * 100, 0, 100) : 0;
        var remainingSeconds = Math.Max(0, IdleDelay.TotalSeconds - idleFor.TotalSeconds);
        IdleProgressText = _isMoving ? "Läuft jetzt" : _isEnabled ? $"{remainingSeconds:0.0} s" : "Pausiert";

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IdleProgress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IdleProgressText)));
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr refData)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ToggleKeeper();
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var hookData = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((hookData.flags & LlmhfInjected) == 0)
            {
                _lastRealMouseInput = DateTimeOffset.Now;
            }
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
        }
        RemoveWindowSubclass(_windowHandle, _subclassProc, 1);

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr refData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
