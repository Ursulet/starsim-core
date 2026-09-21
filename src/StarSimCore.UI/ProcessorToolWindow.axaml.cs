using Avalonia.Controls;
using Avalonia.Interactivity;
using StarSimCore.UI.ViewModels;

namespace StarSimCore.UI;

public partial class ProcessorToolWindow : Window
{
    public ProcessorToolWindow()
    {
        InitializeComponent();
    }

    public ProcessorToolWindow(ProcessorModuleViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
