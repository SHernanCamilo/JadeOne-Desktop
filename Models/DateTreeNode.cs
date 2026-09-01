using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SaraBI.Models;

public sealed class DateTreeNode : INotifyPropertyChanged
{
    private bool? _isChecked = true;
    private bool _isExpanded;
    private bool _suppress;

    public string Label { get; init; } = "";
    public int Level { get; init; }
    public DateTreeNode? Parent { get; set; }
    public List<FilterValueItem> Items { get; } = new();
    public List<DateTreeNode> Children { get; } = new();
    public ObservableCollection<DateTreeNode> FilteredChildren { get; } = new();
    public bool IsMatch { get; set; } = true;

    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value, fromParent: false);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IEnumerable<FilterValueItem> AllItems()
    {
        if (Items.Count > 0)
        {
            return Items;
        }

        return Children.SelectMany(c => c.AllItems());
    }

    public void SetChecked(bool? value, bool fromParent)
    {
        if (_suppress)
        {
            return;
        }

        _suppress = true;
        _isChecked = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));

        if (value is bool b)
        {
            foreach (var child in Children)
            {
                child.SetChecked(b, fromParent: true);
            }

            foreach (var item in Items)
            {
                item.IsChecked = b;
            }
        }

        _suppress = false;
        if (!fromParent)
        {
            Parent?.RefreshFromChildren();
        }
    }

    public void RefreshFromChildren()
    {
        bool? next;
        if (Children.Count > 0)
        {
            if (Children.All(c => c.IsChecked == true))
            {
                next = true;
            }
            else if (Children.All(c => c.IsChecked == false))
            {
                next = false;
            }
            else
            {
                next = null;
            }
        }
        else if (Items.Count > 0)
        {
            if (Items.All(i => i.IsChecked))
            {
                next = true;
            }
            else if (Items.All(i => !i.IsChecked))
            {
                next = false;
            }
            else
            {
                next = null;
            }
        }
        else
        {
            return;
        }

        if (_isChecked == next)
        {
            Parent?.RefreshFromChildren();
            return;
        }

        _suppress = true;
        _isChecked = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        _suppress = false;
        Parent?.RefreshFromChildren();
    }

    public bool ApplySearch(string query)
    {
        var q = query.Trim();
        var childMatch = false;
        FilteredChildren.Clear();
        foreach (var child in Children)
        {
            if (child.ApplySearch(q))
            {
                FilteredChildren.Add(child);
                childMatch = true;
            }
        }

        IsMatch = q.Length == 0
                  || Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                  || childMatch;
        if (IsMatch && q.Length > 0 && childMatch)
        {
            IsExpanded = true;
        }

        return IsMatch;
    }
}
