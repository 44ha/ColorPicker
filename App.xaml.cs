using System.Windows;
using ColorPicker.Services;
using ColorPicker.ViewModels;

namespace ColorPicker;

/// <summary>
/// Application composition root.
///
/// Services are created here (in a fixed order) and handed to the view model,
/// and every Win32 resource they own — global hotkey, message window, the
/// transient picking overlay — is deterministically released in <see cref="OnExit"/>.
/// No global input hooks are installed anywhere: hotkeys come from RegisterHotKey
/// and clicks are captured by the overlay window only while picking runs.
/// </summary>
public partial class App : Application
{
    private HotkeyService? _hotkeyService;
    private PickingOverlayService? _pickingOverlay;
    private PickingController? _pickingController;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --- services -----------------------------------------------------
        var converter = new ColorConverterService();
        var clipboard = new ClipboardService();
        var pixelCapture = new PixelCaptureService();

        _hotkeyService = new HotkeyService();       // RegisterHotKey + message window
        _pickingOverlay = new PickingOverlayService(); // transparent full-screen window, created only while picking

        _pickingController = new PickingController(
            _pickingOverlay,
            pixelCapture,
            clipboard,
            converter);

        // --- view model + main window -------------------------------------
        var viewModel = new MainViewModel(_hotkeyService, _pickingController, clipboard, converter);

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        // Register the system-wide hotkey once a window (message pump owner) exists.
        viewModel.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Tear down in reverse order: stop picking (destroys the overlay and
        // closes the widget), then the hotkey + message window, then the overlay
        // service itself as a safety net.
        _pickingController?.Dispose();
        _hotkeyService?.Dispose();
        _pickingOverlay?.Dispose();

        base.OnExit(e);
    }
}
