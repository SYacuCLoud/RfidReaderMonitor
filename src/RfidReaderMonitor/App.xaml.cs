using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private const string CollectorMutexName = @"Local\RfidReaderMonitor.Collector.SingleInstance";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private PcscReaderProvider? _provider;
    private PresenceTracker? _tracker;
    private EventDispatcher? _dispatcher;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TaskbarIcon? _tray;
    private CollectorViewModel? _collector;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = SettingsStore.Load();
        bool collectorMode = e.Args.Any(a => a.Equals("--collector", StringComparison.OrdinalIgnoreCase));

        Directory.CreateDirectory(settings.EffectiveLogFolder);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(settings.EffectiveLogFolder, collectorMode ? "collector-.log" : "app-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Information("시작 v{Version} {Mode}", typeof(App).Assembly.GetName().Version, collectorMode ? "(수집 모드)" : "");

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error(ex.Exception, "UI 미처리 예외");
            MessageBox.Show(ex.Exception.Message, "RFID Reader Monitor 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error(ex.Exception, "미관찰 Task 예외"); ex.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Fatal(ex.ExceptionObject as Exception, "치명적 예외");

        if (collectorMode)
        {
            StartCollector(e.Args, settings);
            return;
        }

        // 단일 인스턴스 (감시 모드)
        _mutex = new Mutex(true, MutexName, out var isNew);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isNew)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

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

    /// <summary>--collector [포트]: 여러 감시 PC가 보내는 이벤트·하트비트를 받아 보여 주는 모드.</summary>
    private void StartCollector(string[] args, AppSettings settings)
    {
        int port = settings.CollectorPort;
        int idx = Array.FindIndex(args, a => a.Equals("--collector", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var p)) port = p;

        _mutex = new Mutex(true, CollectorMutexName, out var isNew);
        if (!isNew)
        {
            MessageBox.Show("수집 모드가 이미 실행 중입니다.", "RFID Reader Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _collector = new CollectorViewModel(port, Dispatcher);
        var win = new CollectorWindow(_collector);
        MainWindow = win;
        win.Closed += (_, _) => Shutdown();
        win.Show();
        _collector.Start();
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
            if (_dispatcher is not null) _dispatcher.DisposeAsync().AsTask().Wait(5000);
            _provider?.Dispose();
            if (_collector is not null) _collector.DisposeAsync().AsTask().Wait(3000);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "종료 정리 중 오류");
        }
        Log.Information("종료");
        Log.CloseAndFlush();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
