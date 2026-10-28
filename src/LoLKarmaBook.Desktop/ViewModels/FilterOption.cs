namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>Simple selectable option for ComboBox filters (value + Chinese label).</summary>
public sealed class FilterOption<T>
{
    public FilterOption(T? value, string label)
    {
        Value = value;
        Label = label;
    }

    public T? Value { get; }

    public string Label { get; }

    public override string ToString() => Label;
}
