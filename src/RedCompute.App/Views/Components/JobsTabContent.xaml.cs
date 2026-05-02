using System.Windows;
using System.Windows.Controls;
using RedCompute.App.ViewModels;

namespace RedCompute.App.Views.Components;

public partial class JobsTabContent : UserControl
{
    public JobsTabContent()
    {
        InitializeComponent();
    }

    private void FriezeItemsControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && DataContext is MainViewModel vm)
            vm.JobsTab.FriezeAvailableWidth = e.NewSize.Width;
    }
}
