using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RedCompute.App.ViewModels;

namespace RedCompute.App.Views.Components;

public partial class ConsoleTabContent : UserControl
{
    private bool _autoScroll = true;

    public ConsoleTabContent()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConsoleLogViewModel vm)
        {
            vm.EntryAdded += (_, _) =>
            {
                if (_autoScroll && LogListView.Items.Count > 0)
                    LogListView.ScrollIntoView(LogListView.Items[^1]);
            };
        }

        var scrollViewer = GetScrollViewer(LogListView);
        if (scrollViewer != null)
            scrollViewer.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer sv)
            _autoScroll = sv.VerticalOffset >= sv.ScrollableHeight - 20;
    }

    private void TagChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is string tag
            && DataContext is ConsoleLogViewModel vm)
        {
            vm.SetTagFilterCommand.Execute(tag);
        }
    }

    private static ScrollViewer? GetScrollViewer(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is ScrollViewer sv) return sv;
            var result = GetScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }
}
