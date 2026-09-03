using System.Windows;
using RfidReaderMonitor.ViewModels;

namespace RfidReaderMonitor.Views;

public partial class CollectorWindow : Window
{
    public CollectorWindow(CollectorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
