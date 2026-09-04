using System.Windows;
using RfidReaderMonitor.ViewModels;

namespace RfidReaderMonitor.Views;

public partial class ModbusReaderDialog : Window
{
    public ModbusReaderDialog(ModbusReaderEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += ok =>
        {
            DialogResult = ok;
            Close();
        };
        Closed += (_, _) => vm.Dispose();
    }
}
