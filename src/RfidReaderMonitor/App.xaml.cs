using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using RfidReaderMonitor.Core;
using RfidReaderMonitor.Output;
using RfidReaderMonitor.PcSc;
using RfidReaderMonitor.Settings;
using RfidReaderMonitor.ViewModels;
using RfidReaderMonitor.Views;
using Serilog;

namespace RfidReaderMonitor;

public partial class App : Application
{
    private const string MutexName = @"Local\RfidReaderMonitor.SingleInstance";
    private const string ShowEventName = @"Local\RfidReaderMonitor.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private PcscReaderProvider? _provider;
    private PresenceTracker? _tracker;
    private EventDispatcher? _dispatcher;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TaskbarIcon? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 단일 인스턴스
        _mutex = new Mutex(true, MutexName, out var isNew);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isNew)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        var settings = SettingsStore.Load();
        Directory.CreateDirectory(settings.EffectiveLogFolder);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(settings.EffectiveLogFolder, "app-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Information("시작 v{Version}", typeof(App).Assembly.GetName().Version);

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error(ex.Exception, "UI 미처리 예외");
            MessageBox.Show(ex.Exception.Message, "RFID Reader Monitor 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error(ex.Exception, "미관찰 Task 예외"); ex.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Fatal(ex.ExceptionObject as Exception, "치명적 예외");

        _provider = new PcscReaderProvider();
        var provider = _provider;
        _tracker = new PresenceTracker(name =>
        {
            try { return provider.ReadTag(name); }
            catch (Exception ex) { Log.Debug("UID 읽기 실패 {Reader}: {Msg}", name, ex.Message); return null; }
        });
        _dispatcher = new EventDispatcher();
        var rawLogger = new RawSignalLogger(Path.Combine(settings.EffectiveLogFolder, "raw"));

        _vm = new MainViewModel(settings, _provider, _tracker, _dispatcher, rawLogger, Dispatcher);
        _window = new MainWindow(_vm);
        MainWindow = _window;

        SetupTray();

        var startMinimized = settings.StartMinimized || e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startMinimized) _window.Show();

        // 두 번째 인스턴스가 보낸 "창 보이기" 신호 대기
        var showEvent = _showEvent;
        var t = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                try { Dispatcher.BeginInvoke(() => _window?.ShowAndActivate()); } catch { break; }
            }
        }) { IsBackground = true, Name = "ShowSignal" };
        t.Start();

        await _vm.InitializeAsync();
    }

    private void SetupTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        _tray = new TaskbarIcon
        {
            ToolTipText = "RFID Reader Monitor",
            Icon = iconStream is null ? null : new System.Drawing.Icon(iconStream),
            Visibility = Visibility.Visible
        };
        _tray.TrayMouseDoubleClick += (_, _) => _window?.ShowAndActivate();

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "열기" };
        open.Click += (_, _) => _window?.ShowAndActivate();
        var exit = new MenuItem { Header = "종료" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        _tray.ContextMenu = menu;
    }

    public void ExitApp()
    {
        if (_window is not null)
        {
            _window.ForceClose = true;
            _window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _vm?.PersistOnExit();
            _tray?.Dispose();
            _provider?.Stop();
            _tracker?.Dispose();
            if (_dispatcher is not null) _dispatcher.DisposeAsync().AsTask().Wait(3000);
            _provider?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "종료 정리 중 오류");
        }
        Log.Information("종료");
        Log.CloseAndFlush();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
