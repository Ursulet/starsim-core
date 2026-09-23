using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StarSimCore.Application.Localization;
using StarSimCore.UI.ViewModels;

namespace StarSimCore.UI;

public partial class BatchProcessingWindow : Window
{
    public BatchProcessingWindow()
    {
        InitializeComponent();
    }

    public BatchProcessingWindow(BatchProcessingViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private async void SelectSourceFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = LocalizationService.Instance["FilePicker.BatchSourceFolder"],
                AllowMultiple = false,
            });
        var path = folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
        if (path is not null && DataContext is BatchProcessingViewModel viewModel)
            viewModel.SetSourceFolder(path);
    }

    private async void SelectDestinationFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = LocalizationService.Instance["FilePicker.BatchDestinationFolder"],
                AllowMultiple = false,
            });
        var path = folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
        if (path is not null && DataContext is BatchProcessingViewModel viewModel)
            viewModel.SetDestinationFolder(path);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not BatchProcessingViewModel { IsRunning: true } viewModel) return;
        if (viewModel.CancelBatchCommand.CanExecute(null))
            viewModel.CancelBatchCommand.Execute(null);
        if (e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
            e.Cancel = true;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (DataContext is IDisposable disposable) disposable.Dispose();
    }
}
