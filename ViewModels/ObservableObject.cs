using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ColorPicker.ViewModels;

/// <summary>
/// Standard MVVM base class: raises <see cref="INotifyPropertyChanged"/> so XAML
/// bindings stay in sync with view-model state.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Assigns the backing field and notifies bindings when the value changed.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
