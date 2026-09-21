using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using StarSimCore.UI.ViewModels;

namespace StarSimCore.UI;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
    }

    public HelpWindow(HelpWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    public void SelectTab(int index)
    {
        if (DataContext is HelpWindowViewModel viewModel)
            viewModel.SelectedTabIndex = index;
    }

    private void OpenAssociationWebsite_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://starsim.ro/") { UseShellExecute = true });
        }
        catch
        {
            // The visible URL remains available if Windows has no browser association.
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
