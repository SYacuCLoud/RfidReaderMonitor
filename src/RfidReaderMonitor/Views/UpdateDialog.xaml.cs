using System.ComponentModel;
using System.Windows;
using RfidReaderMonitor.ViewModels;

namespace RfidReaderMonitor.Views;

public partial class UpdateDialog : Window
{
    private readonly UpdateViewModel _vm;

    public UpdateDialog(UpdateViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.CloseRequested += () => Dispatcher.BeginInvoke(Close);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 다운로드 중에는 창을 닫지 못하게 (취소 버튼으로 중단)
        if (_vm.Busy) e.Cancel = true;
        base.OnClosing(e);
    }
}
