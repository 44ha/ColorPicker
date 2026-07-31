using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ColorPicker;

/// <summary>
/// Main dashboard window.
///
/// All behaviour lives in <see cref="ViewModels.MainViewModel"/>; the code-behind
/// only handles window-chrome interactions that cannot be expressed in XAML
/// (drag to move, caption buttons, and the maximized-window/taskbar quirk that
/// custom chrome introduces).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The minimize / maximize / close buttons live inside this Grid, so a
        // press on them bubbles up here as well. DragMove() would then capture
        // the mouse for the duration of the press, the button would never see
        // its MouseLeftButtonUp and its Click would never fire — a silent,
        // complete "nothing happens". Let buttons handle their own presses.
        if (IsInsideButton(e.OriginalSource)) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else if (WindowState == WindowState.Normal)
        {
            DragMove(); // lets the user drag the frameless-feeling window around
        }
    }

    /// <summary>
    /// Walks up from the hit-tested element (usually the Border inside a button
    /// template, which is not part of the logical tree) to see whether the press
    /// started on a button.
    /// </summary>
    private static bool IsInsideButton(object? originalSource)
    {
        DependencyObject? current = originalSource as DependencyObject;

        while (current is not null)
        {
            if (current is ButtonBase) return true;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    /// <summary>
    /// With WindowChrome, a maximized window normally covers the taskbar because
    /// its bounds are the full monitor. Capping MaxHeight to the work area fixes it.
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MaxHeight = SystemParameters.WorkArea.Height;
            MaxWidth = SystemParameters.WorkArea.Width;
        }
        else
        {
            MaxHeight = double.PositiveInfinity;
            MaxWidth = double.PositiveInfinity;
        }

        base.OnStateChanged(e);
    }
}
