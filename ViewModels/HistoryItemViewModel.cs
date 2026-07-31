using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using ColorPicker.Models;

namespace ColorPicker.ViewModels;

/// <summary>
/// One format row inside an expanded history card, e.g.
/// Label = "RGB" · Value = "0, 242, 204" plus its own copy command.
/// </summary>
public sealed class FormatEntry
{
    public FormatEntry(string label, string value, ICommand copyCommand)
    {
        Label = label;
        Value = value;
        CopyCommand = copyCommand;
    }

    public string Label { get; }

    public string Value { get; }

    /// <summary>Parameterless command that copies <see cref="Value"/> to the clipboard.</summary>
    public ICommand CopyCommand { get; }
}

/// <summary>
/// View model for one entry in the dashboard history list: a big swatch plus a
/// stacked list of every converted representation, each with its own
/// "Copy to Clipboard" button.
/// </summary>
public sealed class HistoryItemViewModel : ObservableObject
{
    private bool _isExpanded = true; // expanded by default: all six formats visible at a glance

    public HistoryItemViewModel(
        ColorInfo color,
        Action<string, string> copyValue,       // (value, label) → clipboard + status feedback
        Action<HistoryItemViewModel> delete)    // remove this card from history
    {
        Color = color;

        // Frozen brush: shared, thread-safe, cheap to paint on every frame.
        // Fully-qualified because our `Color` property would otherwise shadow the
        // System.Windows.Media.Color type name inside this class.
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        SwatchBrush = brush;

        CopyCommand = new RelayCommand(p =>
        {
            if (p is string value) copyValue(value, FindLabel(value));
        });

        DeleteCommand = new RelayCommand(_ => delete(this));

        Formats = new ObservableCollection<FormatEntry>
        {
            new("HEX", color.Hex, CopyCommand),
            new("RGB", color.Rgb, CopyCommand),
            new("RGBA", color.Rgba, CopyCommand),
            new("HSL", color.Hsl, CopyCommand),
            new("CMYK", color.Cmyk, CopyCommand),
            new("HEX RAW", color.HexRaw, CopyCommand),
        };
    }

    public ColorInfo Color { get; }

    public SolidColorBrush SwatchBrush { get; }

    public ObservableCollection<FormatEntry> Formats { get; }

    public ICommand CopyCommand { get; }

    public ICommand DeleteCommand { get; }

    /// <summary>Small caption under the HEX value: when and where it was picked.</summary>
    public string Meta => Color.X >= 0
        ? $"picked {Color.CapturedAt:HH:mm:ss} · ({Color.X}, {Color.Y})"
        : $"added manually · {Color.CapturedAt:HH:mm:ss}";

    /// <summary>Drives the expand/collapse chevron and the formats list visibility.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private string FindLabel(string value)
        => Formats.FirstOrDefault(f => f.Value == value)?.Label ?? "value";
}
