using CommunityToolkit.Mvvm.Input;

namespace StarSimCore.UI.ViewModels;

public sealed class HistoryStateViewModel
{
    public HistoryStateViewModel(
        int index,
        string title,
        string details,
        bool isCurrent,
        Action<int> select)
    {
        Index = index;
        Title = title;
        Details = details;
        IsCurrent = isCurrent;
        SelectCommand = new RelayCommand(() => select(Index));
    }

    public int Index { get; }
    public string Title { get; }
    public string Details { get; }
    public bool IsCurrent { get; }
    public IRelayCommand SelectCommand { get; }
}
