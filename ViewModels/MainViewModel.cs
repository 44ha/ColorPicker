using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using ColorPicker.Models;
using ColorPicker.Services;
using Win32 = ColorPicker.Services.Win32Api;

namespace ColorPicker.ViewModels;

/// <summary>
/// Dashboard view model:
///   * starts / cancels picking mode,
///   * owns the picked-colour history (bounded, newest first),
///   * copies any format row to the clipboard,
///   * lets the user view + re-configure the global hotkey,
///   * accepts a manually typed hex value.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    // Registered hotkey ids (Win32 requires ids in0x0000–0xBFFF).
    private const int PickHotkeyId = 0x1001;
    private const int EscHotkeyId = 0x1002;

    // Oldest entries are dropped past this count so the list stays bounded.
    private const int MaxHistoryEntries = 40;

    private static readonly IReadOnlyList<string> KeyOptions = BuildKeyOptions();

    private readonly HotkeyService _hotkeyService;
    private readonly PickingController _pickingController;
    private readonly ClipboardService _clipboard;
    private readonly ColorConverterService _converter;

    // The hotkey currently registered with Windows (as opposed to the pending
    // settings the user may be editing in the Settings card).
    private Win32.HotkeyModifiers _activeModifiers =
        Win32.HotkeyModifiers.Control | Win32.HotkeyModifiers.Shift | Win32.HotkeyModifiers.NoRepeat;
    private uint _activeVirtualKey = (uint)'C';
    private string _activeKeyName = "C";

    private string _statusMessage = "Ready.";
    private string _hotkeyDisplay = string.Empty;
    private bool _isSettingsOpen;
    private string _hexInput = string.Empty;
    private bool _useControl = true;
    private bool _useShift = true;
    private bool _useAlt;
    private bool _useWin;
    private string _selectedKey = "C";
    private HistoryItemViewModel? _currentItem;

    public MainViewModel(
        HotkeyService hotkeyService,
        PickingController pickingController,
        ClipboardService clipboard,
        ColorConverterService converter)
    {
        _hotkeyService = hotkeyService;
        _pickingController = pickingController;
        _clipboard = clipboard;
        _converter = converter;

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _pickingController.StateChanged += OnPickingStateChanged;
        _pickingController.ColorPicked += OnColorPicked;

        History.CollectionChanged += (_, _) => RefreshHistoryState();

        HotkeyDisplay = FormatHotkey(_activeModifiers, _activeKeyName);

        PickColorCommand = new RelayCommand(TogglePicking);
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        ApplyHotkeyCommand = new RelayCommand(_ => ApplyHotkey());
        ClearHistoryCommand = new RelayCommand(_ => ClearHistory(), _ => History.Count > 0);
        AddFromHexCommand = new RelayCommand(_ => AddFromHex());
        CopyCurrentHexCommand = new RelayCommand(
            _ => CopyText(CurrentItem!.Color.Hex, "HEX"),
            _ => CurrentItem is not null);
    }

    // =====================================================================
    //  Bindable state
    // =====================================================================

    public ObservableCollection<HistoryItemViewModel> History { get; } = new();

    public IReadOnlyList<string> HotkeyKeyOptions => KeyOptions;

    /// <summary>The currently picked colour (drives the "current color" card), or null.</summary>
    public HistoryItemViewModel? CurrentItem
    {
        get => _currentItem;
        private set
        {
            if (SetProperty(ref _currentItem, value))
            {
                OnPropertyChanged(nameof(HasCurrentItem));
                OnPropertyChanged(nameof(CurrentBrush));
            }
        }
    }

    public bool HasCurrentItem => CurrentItem is not null;

    public SolidColorBrush? CurrentBrush => CurrentItem?.SwatchBrush;

    public bool IsHistoryEmpty => History.Count == 0;

    public string HistoryCount => History.Count == 1 ? "1 color" : $"{History.Count} colors";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Applied hotkey, e.g. "Ctrl + Shift + C" — shown in the header and hero text.</summary>
    public string HotkeyDisplay
    {
        get => _hotkeyDisplay;
        private set => SetProperty(ref _hotkeyDisplay, value);
    }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    /// <summary>Text typed into the "add from hex" box.</summary>
    public string HexInput
    {
        get => _hexInput;
        set => SetProperty(ref _hexInput, value);
    }

    // Pending hotkey settings (applied when the user clicks "Apply").
    public bool UseControl { get => _useControl; set => SetProperty(ref _useControl, value); }
    public bool UseShift { get => _useShift; set => SetProperty(ref _useShift, value); }
    public bool UseAlt { get => _useAlt; set => SetProperty(ref _useAlt, value); }
    public bool UseWin { get => _useWin; set => SetProperty(ref _useWin, value); }

    public string SelectedKey
    {
        get => _selectedKey;
        set => SetProperty(ref _selectedKey, value);
    }

    // =====================================================================
    //  Commands
    // =====================================================================

    public ICommand PickColorCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand ApplyHotkeyCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand AddFromHexCommand { get; }
    public ICommand CopyCurrentHexCommand { get; }

    // =====================================================================
    //  Lifecycle
    // =====================================================================

    /// <summary>Registers the default hotkey. Call once after the main window is shown.</summary>
    public void Initialize()
    {
        if (TryRegisterHotkey(PickHotkeyId, _activeModifiers, _activeVirtualKey))
        {
            StatusMessage = $"Ready — press {HotkeyDisplay} anywhere to pick a color.";
        }
        else
        {
            StatusMessage = $"Couldn't register {HotkeyDisplay} — it may be in use by another app. Change it in Settings.";
        }
    }

    // =====================================================================
    //  Picking flow
    // =====================================================================

    private void TogglePicking()
    {
        if (_pickingController.IsPicking)
        {
            _pickingController.Cancel();
            return;
        }

        try
        {
            _pickingController.Start();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Picking unavailable: {ex.Message}";
        }
    }

    private void OnPickingStateChanged(object? sender, EventArgs e)
    {
        if (_pickingController.IsPicking)
        {
            // While picking, reserve Esc globally so the user can bail out from
            // anywhere. Ignore failures (rare — some app may have reserved Esc).
            TryRegisterHotkey(EscHotkeyId, Win32.HotkeyModifiers.NoRepeat, 0x1B);
            StatusMessage = "Picking — move the cursor, left-click to copy. Right-click or Esc cancels.";
        }
        else
        {
            _hotkeyService.Unregister(EscHotkeyId);
        }
    }

    private void OnHotkeyPressed(int id)
    {
        if (id == EscHotkeyId)
        {
            _pickingController.Cancel();
            return;
        }

        // The same hotkey toggles picking on and off, system-wide.
        TogglePicking();
    }

    private void OnColorPicked(object? sender, ColorPickedEventArgs e)
    {
        AddToHistory(e.Color);

        StatusMessage = e.CopiedToClipboard
            ? $"Copied {e.Color.Hex} to the clipboard and saved it to history."
            : $"Saved {e.Color.Hex} to history — the clipboard was busy, use a Copy button on the card.";
    }

    // =====================================================================
    //  History helpers
    // =====================================================================

    private void AddToHistory(ColorInfo color)
    {
        var item = new HistoryItemViewModel(color, CopyText, RemoveItem);
        History.Insert(0, item); // newest first
        CurrentItem = item;

        while (History.Count > MaxHistoryEntries)
        {
            History.RemoveAt(History.Count - 1);
        }

        RefreshHistoryState();
    }

    private void RemoveItem(HistoryItemViewModel item)
    {
        History.Remove(item);
        if (ReferenceEquals(CurrentItem, item))
        {
            CurrentItem = History.FirstOrDefault();
        }

        RefreshHistoryState();
    }

    private void ClearHistory()
    {
        History.Clear();
        CurrentItem = null;
        StatusMessage = "History cleared.";
    }

    private void RefreshHistoryState()
    {
        OnPropertyChanged(nameof(HistoryCount));
        OnPropertyChanged(nameof(IsHistoryEmpty));
        OnPropertyChanged(nameof(HasCurrentItem));
    }

    // =====================================================================
    //  Clipboard
    // =====================================================================

    private void CopyText(string value, string label)
    {
        StatusMessage = _clipboard.TrySetText(value)
            ? $"Copied {label} — {value}"
            : "Couldn't reach the clipboard (another app may be holding it). Try again.";
    }

    private void AddFromHex()
    {
        if (string.IsNullOrWhiteSpace(HexInput))
        {
            return;
        }

        if (!_converter.TryParseHex(HexInput, out var color))
        {
            StatusMessage = $"'{HexInput.Trim()}' isn't a valid hex color — try something like #1A73E8.";
            return;
        }

        AddToHistory(color);
        CopyText(color.Hex, "HEX");
        HexInput = string.Empty;
    }

    // =====================================================================
    //  Hotkey settings
    // =====================================================================

    private void ApplyHotkey()
    {
        var modifiers = Win32.HotkeyModifiers.NoRepeat;
        if (UseControl) modifiers |= Win32.HotkeyModifiers.Control;
        if (UseShift) modifiers |= Win32.HotkeyModifiers.Shift;
        if (UseAlt) modifiers |= Win32.HotkeyModifiers.Alt;
        if (UseWin) modifiers |= Win32.HotkeyModifiers.Win;

        // A plain letter with no modifier would swallow that key everywhere — refuse.
        if ((modifiers & ~Win32.HotkeyModifiers.NoRepeat) == 0)
        {
            StatusMessage = "Pick at least one modifier (Ctrl, Shift, Alt or Win).";
            return;
        }

        uint virtualKey = VirtualKeyFromName(SelectedKey);
        if (virtualKey == 0)
        {
            StatusMessage = $"Unsupported key '{SelectedKey}'.";
            return;
        }

        _hotkeyService.Unregister(PickHotkeyId);

        if (TryRegisterHotkey(PickHotkeyId, modifiers, virtualKey))
        {
            _activeModifiers = modifiers;
            _activeVirtualKey = virtualKey;
            _activeKeyName = SelectedKey;
            HotkeyDisplay = FormatHotkey(modifiers, SelectedKey);
            StatusMessage = $"Hotkey updated — press {HotkeyDisplay} anywhere to pick a color.";
        }
        else
        {
            // Restore the previous hotkey so the app never ends up with none.
            TryRegisterHotkey(PickHotkeyId, _activeModifiers, _activeVirtualKey);
            StatusMessage = $"{FormatHotkey(modifiers, SelectedKey)} is already taken by another app — try a different combination.";
        }
    }

    private bool TryRegisterHotkey(int id, Win32.HotkeyModifiers modifiers, uint virtualKey)
    {
        try
        {
            return _hotkeyService.Register(id, modifiers, virtualKey);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatHotkey(Win32.HotkeyModifiers modifiers, string key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(Win32.HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(Win32.HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(Win32.HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(Win32.HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(key);
        return string.Join(" + ", parts);
    }

    /// <summary>Maps a display key name ("C", "7", "F5") to its Win32 virtual-key code.</summary>
    private static uint VirtualKeyFromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0;

        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) return c;
        }
        else if (name.Length is >= 2 and <= 3 && name[0] == 'F'
                 && int.TryParse(name[1..], out int functionKey)
                 && functionKey is >= 1 and <= 24)
        {
            return (uint)(0x70 + functionKey - 1); // VK_F1 = 0x70 …
        }

        return 0;
    }

    private static IReadOnlyList<string> BuildKeyOptions()
    {
        var keys = new List<string>();
        for (char c = 'A'; c <= 'Z'; c++) keys.Add(c.ToString());
        for (char c = '0'; c <= '9'; c++) keys.Add(c.ToString());
        for (int f = 1; f <= 12; f++) keys.Add($"F{f}");
        return keys;
    }
}
