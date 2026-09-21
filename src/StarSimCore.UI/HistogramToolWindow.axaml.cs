using Avalonia.Controls;
using Avalonia.Interactivity;
using StarSimCore.UI.ViewModels;

namespace StarSimCore.UI;

public partial class HistogramToolWindow : Window
{
    public HistogramToolWindow()
    {
        InitializeComponent();
    }

    public HistogramToolWindow(MainWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
