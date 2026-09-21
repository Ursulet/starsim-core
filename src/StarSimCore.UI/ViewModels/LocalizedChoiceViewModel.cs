using CommunityToolkit.Mvvm.ComponentModel;
using StarSimCore.Application.Localization;

namespace StarSimCore.UI.ViewModels;

public sealed class LocalizedChoiceViewModel<T> : ObservableObject where T : notnull
{
    public LocalizedChoiceViewModel(T value, string resourceKey)
    {
        Value = value;
        ResourceKey = resourceKey;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    public T Value { get; }
    public string ResourceKey { get; }
    public string DisplayName => LocalizationService.Instance[ResourceKey];

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(DisplayName));

    public override string ToString() => DisplayName;
}
