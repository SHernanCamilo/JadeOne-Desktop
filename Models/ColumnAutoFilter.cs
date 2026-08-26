using System.ComponentModel;

namespace SaraBI.Models;

public sealed class ColumnAutoFilter
{
    public HashSet<string>? Selected { get; set; }
    public bool IncludeBlanks { get; set; } = true;
    public string? Contains { get; set; }
    public bool IsActive =>
        !string.IsNullOrEmpty(Contains)
        || (Selected is not null);
}

public sealed class FilterValueItem : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public string Value { get; init; } = "";
    public string Display { get; init; } = "";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
