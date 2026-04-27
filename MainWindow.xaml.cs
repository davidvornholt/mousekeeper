using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
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
    private const double MovementPhaseStep = 0.16;
    private const double HorizontalMovementPixels = 8.25;
    private const double VerticalMovementPixels = 5.75;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(32);

    private readonly DispatcherTimer _timer;
    private readonly IntPtr _windowHandle;
    private readonly SUBCLASSPROC _subclassProc;
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _hookHandle;
    private DateTimeOffset _lastRealMouseInput = DateTimeOffset.Now;
    private DateTimeOffset? _activeSince;
    private readonly TimeSpan _idleDelay = TimeSpan.FromSeconds(3);
    private bool _hotkeyRegistered;
    private bool _isEnabled;
    private bool _isMoving;
    private double _phase;
    private double _movementRemainderX;
    private double _movementRemainderY;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();

        _windowHandle = WindowNative.GetWindowHandle(this);
        _subclassProc = WindowSubclassProc;
        _mouseProc = MouseHookProc;

        ConfigureWindow();
        RegisterBestAvailableHotkey();
        SetWindowSubclass(_windowHandle, _subclassProc, 1, IntPtr.Zero);
        _hookHandle = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);

        _timer = new DispatcherTimer { Interval = FrameInterval };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        Closed += MainWindow_Closed;
        RefreshAll();
    }

    public string HeroText { get; private set; } = string.Empty;
    public string HeroSubtext { get; private set; } = string.Empty;
    public string ToggleButtonText { get; private set; } = string.Empty;
    public string ToggleGlyph { get; private set; } = "\uE768";
    public string ShortcutText { get; private set; } = string.Empty;
    public Brush StatusBrush { get; private set; } = new SolidColorBrush(Color.FromArgb(255, 140, 140, 140));

    private void ConfigureWindow()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        appWindow.Resize(new SizeInt32(460, 600));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
    }

    private void RegisterBestAvailableHotkey()
    {
        var candidates = new[]
        {
            (Modifiers: ModifierControlAlt, Key: KeyM, Text: "Ctrl + Alt + M"),
            (Modifiers: ModifierControlShift, Key: KeyM, Text: "Ctrl + Shift + M"),
            (Modifiers: ModifierControlAlt, Key: KeyK, Text: "Ctrl + Alt + K"),
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
        ShortcutText = "Kein Kurzbefehl";
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e) => ToggleKeeper();

    private void ToggleKeeper()
    {
        _isEnabled = !_isEnabled;
        _isMoving = false;
        _activeSince = null;
        ResetMovementSmoothing();
        StopPulse();
        _lastRealMouseInput = DateTimeOffset.Now;
        RefreshAll();
    }

    private void Timer_Tick(object? sender, object e)
    {
        if (!_isEnabled)
        {
            StopPulse();
            RefreshAll();
            return;
        }

        var idleFor = DateTimeOffset.Now - _lastRealMouseInput;
        var shouldMove = idleFor >= _idleDelay;

        if (_isMoving != shouldMove)
        {
            _isMoving = shouldMove;
            if (_isMoving)
            {
                _activeSince = DateTimeOffset.Now;
                PulseStoryboard.Begin();
            }
            else
            {
                _activeSince = null;
                ResetMovementSmoothing();
                StopPulse();
            }
        }

        if (_isMoving)
        {
            MoveMouseGently();
        }

        RefreshAll(idleFor);
    }

    private void MoveMouseGently()
    {
        _phase += MovementPhaseStep;
        var dx = Math.Sin(_phase) * HorizontalMovementPixels;
        var dy = Math.Cos(_phase * 0.8) * VerticalMovementPixels;
        var dxWithRemainder = dx + _movementRemainderX;
        var dyWithRemainder = dy + _movementRemainderY;
        var roundedDx = (int)Math.Round(dxWithRemainder);
        var roundedDy = (int)Math.Round(dyWithRemainder);

        _movementRemainderX = dxWithRemainder - roundedDx;
        _movementRemainderY = dyWithRemainder - roundedDy;

        if (roundedDx == 0 && roundedDy == 0)
        {
            return;
        }

        SendInput(1, new[]
        {
            new INPUT
            {
                type = 0,
                mi = new MOUSEINPUT
                {
                    dx = roundedDx,
                    dy = roundedDy,
                    dwFlags = MouseEventFMove
                }
            }
        }, Marshal.SizeOf<INPUT>());
    }

    private void ResetMovementSmoothing()
    {
        _movementRemainderX = 0;
        _movementRemainderY = 0;
    }

    private void StopPulse()
    {
        PulseStoryboard.Stop();
        PulseRing.Opacity = 0;
        PulseScale.ScaleX = 1.0;
        PulseScale.ScaleY = 1.0;
    }


    private void RefreshAll() => RefreshAll(DateTimeOffset.Now - _lastRealMouseInput);

    private void RefreshAll(TimeSpan idleFor)
    {
        double progress;
        Color color;

        if (!_isEnabled)
        {
            HeroText = "Aus";
            HeroSubtext = _hotkeyRegistered
                ? $"Drücke Start oder\n{ShortcutText}"
                : "Drücke Start, um zu beginnen";
            ToggleButtonText = "Starten";
            ToggleGlyph = "\uE768"; // Play
            color = Color.FromArgb(255, 140, 140, 140);
            progress = 0;
        }
        else if (_isMoving)
        {
            var elapsed = _activeSince.HasValue ? DateTimeOffset.Now - _activeSince.Value : TimeSpan.Zero;
            HeroText = FormatElapsed(elapsed);
            HeroSubtext = "Maus wird bewegt";
            ToggleButtonText = "Stoppen";
            ToggleGlyph = "\uE71A"; // Stop
            color = Color.FromArgb(255, 76, 187, 113);
            progress = 1.0;
        }
        else
        {
            var remaining = _idleDelay - idleFor;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            HeroText = $"{remaining.TotalSeconds:0.0}s";
            HeroSubtext = "Warten auf Inaktivität";
            ToggleButtonText = "Stoppen";
            ToggleGlyph = "\uE71A";
            var accent = (Color)Application.Current.Resources["SystemAccentColor"];
            color = accent;
            progress = Math.Clamp(idleFor.TotalMilliseconds / _idleDelay.TotalMilliseconds, 0, 1);
        }

        StatusBrush = new SolidColorBrush(color);
        UpdateArc(progress);

        Notify(nameof(StatusBrush));
        Notify(nameof(HeroText));
        Notify(nameof(HeroSubtext));
        Notify(nameof(ToggleButtonText));
        Notify(nameof(ToggleGlyph));
        Notify(nameof(ShortcutText));
    }

    private static string FormatElapsed(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes}:{t.Seconds:00}";
    }

    private void UpdateArc(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        if (progress <= 0.001)
        {
            ProgressArc.Data = null;
            return;
        }

        const double radius = 116.0;
        var center = new Point(124, 124);

        if (progress >= 0.999)
        {
            ProgressArc.Data = new EllipseGeometry { Center = center, RadiusX = radius, RadiusY = radius };
            return;
        }

        const double startAngle = -Math.PI / 2;
        var sweep = progress * 2 * Math.PI;
        var endAngle = startAngle + sweep;

        var startPt = new Point(
            center.X + radius * Math.Cos(startAngle),
            center.Y + radius * Math.Sin(startAngle));
        var endPt = new Point(
            center.X + radius * Math.Cos(endAngle),
            center.Y + radius * Math.Sin(endAngle));

        var figure = new Microsoft.UI.Xaml.Media.PathFigure
        {
            StartPoint = startPt,
            IsClosed = false
        };
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.ArcSegment
        {
            Point = endPt,
            Size = new Size(radius, radius),
            IsLargeArc = sweep > Math.PI,
            SweepDirection = SweepDirection.Clockwise,
            RotationAngle = 0
        });

        var geo = new Microsoft.UI.Xaml.Media.PathGeometry();
        geo.Figures.Add(figure);
        ProgressArc.Data = geo;
    }

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
        StopPulse();
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
    private struct POINT { public int x; public int y; }

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
    private struct INPUT { public int type; public MOUSEINPUT mi; }

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
