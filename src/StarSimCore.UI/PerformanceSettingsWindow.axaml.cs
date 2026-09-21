using Avalonia.Controls;
using Avalonia.Interactivity;
using StarSimCore.UI.ViewModels;

namespace StarSimCore.UI;

public partial class PerformanceSettingsWindow : Window
{
    public PerformanceSettingsWindow()
    {
        InitializeComponent();
    }

    public PerformanceSettingsWindow(MainWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
