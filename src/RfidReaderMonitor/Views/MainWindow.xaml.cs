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
        vm.ShowModbusEditor = editor => new ModbusReaderDialog(editor) { Owner = this }.ShowDialog() == true;
        vm.ConfirmDelete = name => MessageBox.Show(this, $"'{name}' 리더를 설정에서 지웁니다. 별명과 메모는 남지만 이벤트는 더 오지 않습니다.\n\n계속할까요?",
            "리더 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
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
