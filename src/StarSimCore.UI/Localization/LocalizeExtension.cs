using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using StarSimCore.Application.Localization;

namespace StarSimCore.UI.Localization;

public sealed class LocalizeExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocalizeExtension()
    {
    }

    public LocalizeExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding(nameof(LocalizationService.CurrentLanguage))
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
            Converter = new LocalizedStringConverter(Key),
        };
    }

    private sealed class LocalizedStringConverter(string key) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            LocalizationService.Instance[key];

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }
}
