using System.ComponentModel;

namespace SaraBI.Models;

public enum TextFilterOperator
{
    None,
    Contains,
    Equals,
    NotEquals,
    StartsWith,
    EndsWith,
    NotContains,
}

public sealed class ColumnAutoFilter
{
    public HashSet<string>? Selected { get; set; }
    public bool IncludeBlanks { get; set; } = true;
    public TextFilterOperator TextOperator { get; set; }
    public string? TextValue { get; set; }

    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    public bool IsActive =>
        (!string.IsNullOrEmpty(TextValue) && TextOperator != TextFilterOperator.None)
        || Selected is not null
        || DateFrom is not null
        || DateTo is not null;
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
