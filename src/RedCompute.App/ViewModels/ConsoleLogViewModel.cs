using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RedCompute.Core.Logging;

namespace RedCompute.App.ViewModels;

public partial class ConsoleLogViewModel : ObservableObject
{
    private const int MaxEntries = 2000;
    private readonly List<LogEntry> _allEntries = new();

    public ObservableCollection<LogEntry> Entries { get; } = new();
    public ObservableCollection<string> AvailableTags { get; } = new();

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string? _activeTagFilter;

    [ObservableProperty]
    private LogEntry? _selectedEntry;

    [ObservableProperty]
    private bool _isDetailPanelOpen;

    [ObservableProperty]
    private int _totalCount;

    public event EventHandler? EntryAdded;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnActiveTagFilterChanged(string? value) => ApplyFilter();

    partial void OnSelectedEntryChanged(LogEntry? value)
    {
        IsDetailPanelOpen = value != null;
    }

    public void AddEntry(LogEntry entry)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            _allEntries.Add(entry);

            if (!string.IsNullOrEmpty(entry.Tag) && !AvailableTags.Contains(entry.Tag))
                AvailableTags.Add(entry.Tag);

            if (_allEntries.Count > MaxEntries)
            {
                var removed = _allEntries[0];
                _allEntries.RemoveAt(0);
                Entries.Remove(removed);
            }

            TotalCount = _allEntries.Count;

            if (MatchesFilter(entry))
            {
                Entries.Add(entry);
                EntryAdded?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    [RelayCommand]
    private void ClearLog()
    {
        _allEntries.Clear();
        Entries.Clear();
        AvailableTags.Clear();
        SelectedEntry = null;
        TotalCount = 0;
    }

    [RelayCommand]
    private void CloseDetail()
    {
        SelectedEntry = null;
    }

    [RelayCommand]
    private void SetTagFilter(string? tag)
    {
        ActiveTagFilter = ActiveTagFilter == tag ? null : tag;
    }

    private void ApplyFilter()
    {
        Entries.Clear();
        foreach (var entry in _allEntries)
        {
            if (MatchesFilter(entry))
                Entries.Add(entry);
        }
        EntryAdded?.Invoke(this, EventArgs.Empty);
    }

    private bool MatchesFilter(LogEntry entry)
    {
        if (ActiveTagFilter != null && !string.Equals(entry.Tag, ActiveTagFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(SearchText))
        {
            if (!entry.FullMessage.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
                !entry.Tag.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
