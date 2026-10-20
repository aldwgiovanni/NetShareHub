using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetShareHub.Desktop.Logging;

namespace NetShareHub.Desktop.ViewModels;

public sealed partial class LogViewModel : ObservableObject
{
    private const int Capacity = 500;

    private readonly LogStore _store;

    public LogViewModel(LogStore store)
    {
        _store = store;
        Entries = new ObservableCollection<LogEntry>(store.Snapshot().Reverse());
        store.EntryAdded += entry => Dispatcher.UIThread.Post(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > Capacity)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }
        });
    }

    /// <summary>Plus récent en premier.</summary>
    public ObservableCollection<LogEntry> Entries { get; }

    [RelayCommand]
    private void Clear() => Entries.Clear();

    public string AllText() => string.Join(Environment.NewLine, Entries.Reverse().Select(e => e.ToString()));
}
