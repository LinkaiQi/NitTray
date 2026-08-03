using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NitTray.Services.Native;

namespace NitTray;

// Transient brightness overlay shown when a global shortcut changes brightness —
// the main window normally sits in the tray, so without this the keys would have no
// visible effect. The window never takes focus and is click-through, and it is
// reused (hidden, not closed) for every step.
public partial class BrightnessOsdWindow : Window
{
    private static readonly TimeSpan VisibleFor = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(220);

    // Gap above the taskbar, roughly matching Windows' own volume/brightness overlay.
    private const double BottomMargin = 56;

    private readonly DispatcherTimer _hideTimer;

    public BrightnessOsdWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = VisibleFor };
        _hideTimer.Tick += (_, _) => BeginFadeOut();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Keep the overlay out of Alt+Tab, off the activation path, and transparent
        // to the mouse so it can never interrupt what the user is doing.
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = User32Native.GetWindowLong(hwnd, User32Native.GWL_EXSTYLE);
            _ = User32Native.SetWindowLong(
                hwnd,
                User32Native.GWL_EXSTYLE,
                style | User32Native.WS_EX_NOACTIVATE
                      | User32Native.WS_EX_TOOLWINDOW
                      | User32Native.WS_EX_TRANSPARENT);
        }
        catch
        {
            // Cosmetic only — the overlay still works with the default styles.
        }
    }

    public void ShowLevel(int percent, string caption)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        CaptionText.Text = caption;
        PercentText.Text = $"{clamped}%";
        PercentText.Visibility = Visibility.Visible;
        LevelBar.Visibility = Visibility.Visible;
        LevelBar.Value = clamped;
        Present();
    }

    // Used when a shortcut fires with no controllable display connected, so the key
    // press still gives feedback instead of appearing to do nothing.
    public void ShowMessage(string message)
    {
        CaptionText.Text = message;
        PercentText.Visibility = Visibility.Collapsed;
        LevelBar.Visibility = Visibility.Collapsed;
        Present();
    }

    private void Present()
    {
        _hideTimer.Stop();

        // Drop any in-flight fade before touching Opacity, or the animation would
        // keep overriding the local value.
        BeginAnimation(OpacityProperty, null);

        // Shown transparent first: the size is only measurable once the window has a
        // handle, and this avoids a flash at whatever position Windows picks.
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        // Positioned on the primary monitor's work area (like Windows' own overlay);
        // SystemParameters.WorkArea and ActualWidth share one DIP space, so this stays
        // correct under per-monitor DPI without any manual conversion.
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + ((area.Width - ActualWidth) / 2);
        Top = area.Bottom - ActualHeight - BottomMargin;

        Opacity = 1;
        _hideTimer.Start();
    }

    private void BeginFadeOut()
    {
        _hideTimer.Stop();

        var fade = new DoubleAnimation(0, FadeOut) { FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) =>
        {
            if (Opacity <= 0.01)
            {
                Hide();
            }
        };
        BeginAnimation(OpacityProperty, fade);
    }
}
