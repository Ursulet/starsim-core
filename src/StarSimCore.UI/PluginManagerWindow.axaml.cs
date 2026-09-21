using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StarSimCore.Application.Localization;
using StarSimCore.UI.ViewModels;

namespace StarSimCore.UI;

public partial class PluginManagerWindow : Window
{
    public PluginManagerWindow()
    {
        InitializeComponent();
    }

    public PluginManagerWindow(PluginManagerViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    public void SelectDeveloperGuide()
    {
        if (DataContext is PluginManagerViewModel viewModel)
            viewModel.SelectDeveloperGuide();
    }

    public async Task BeginInstallAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["FilePicker.InstallPlugin"],
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(LocalizationService.Instance["FilePicker.PluginDll"])
                    {
                        Patterns = ["*.dll"],
                    },
                ],
            });
        var path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        if (path is not null && DataContext is PluginManagerViewModel viewModel)
            viewModel.InstallFile(path);
    }

    private async void InstallPlugin_Click(object? sender, RoutedEventArgs e) =>
        await BeginInstallAsync();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
