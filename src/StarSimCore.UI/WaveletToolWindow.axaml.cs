using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StarSimCore.Application.Localization;
using StarSimCore.UI.ViewModels;

namespace StarSimCore.UI;

public partial class WaveletToolWindow : Window
{
    public WaveletToolWindow()
    {
        InitializeComponent();
    }

    public WaveletToolWindow(WaveletToolViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void ExportDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WaveletToolViewModel viewModel || !viewModel.CanExportDiagnostics) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["FilePicker.ExportWavelet"],
            AllowMultiple = false,
        });
        var path = folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
            await viewModel.ExportDiagnosticsAsync(path);
    }
}
