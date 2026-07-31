using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ColorPicker.Models;
using ColorPicker.Services;

namespace ColorPicker;

/// <summary>
/// The "Copied!" toast: shown for a moment near the cursor after the HEX value
/// has landed on the clipboard, then faded out and closed automatically.
/// </summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan VisibleFor = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(260);

    public ToastWindow(ColorInfo color)
    {
        InitializeComponent();
        HexText.Text = color.Hex;
    }

    /// <summary>Moves the toast next to the given physical screen coordinates.</summary>
    public void MoveTo(int screenX, int screenY)
        => ScreenPlacement.PlaceNear(this, screenX, screenY, offsetX: 26, offsetY: -14);

    /// <summary>Fades the toast in, holds it briefly, fades it out and closes it.</summary>
    public void PlayAndClose()
    {
        var fadeIn = new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(120));
        var fadeOut = new DoubleAnimation(1d, 0d, FadeTime)
        {
            BeginTime = VisibleFor,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(fadeIn, Card);
        Storyboard.SetTarget(fadeOut, Card);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(OpacityProperty));
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));

        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeIn);
        storyboard.Children.Add(fadeOut);
        storyboard.Completed += (_, _) => Close();
        storyboard.Begin();

        // Safety net: never leave a zombie window behind if the animation is skipped
        // (e.g. the window was hidden) and Completed never fires.
        var closeTimer = new DispatcherTimer { Interval = VisibleFor + FadeTime + TimeSpan.FromMilliseconds(500) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            if (IsLoaded) Close();
        };
        closeTimer.Start();
    }
}
