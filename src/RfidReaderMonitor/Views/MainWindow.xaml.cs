using System.ComponentModel;
using System.Windows;
using RfidReaderMonitor.ViewModels;

namespace RfidReaderMonitor.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    public bool ForceClose { get; set; }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ForceClose && _vm.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }
}
