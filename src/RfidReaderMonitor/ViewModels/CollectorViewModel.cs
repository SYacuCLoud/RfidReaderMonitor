using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfidReaderMonitor.Core;
using RfidReaderMonitor.Output;
using RfidReaderMonitor.Settings;
using Serilog;

namespace RfidReaderMonitor.ViewModels;

public enum HostLink { Disconnected, Stale, Live }

/// <summary>수집 화면의 PC 타일 하나.</summary>
public sealed partial class HostTileViewModel : ObservableObject
{
    public HostTileViewModel(string host) => Host = host;

    public string Host { get; }
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _remote = "";
    [ObservableProperty] private DateTimeOffset? _lastHeartbeat;
    [ObservableProperty] private DateTimeOffset? _lastEventTime;
    [ObservableProperty] private string _lastEventText = "이벤트 없음";
    [ObservableProperty] private int _readerCount;
    [ObservableProperty] private int _onlineReaders;
    [ObservableProperty] private int _presentReaders;
    [ObservableProperty] private int _appearToday;
    [ObservableProperty] private int _removeToday;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LinkText))] private HostLink _link = HostLink.Disconnected;
    [ObservableProperty] private string _ageText = "";

    public ObservableCollection<HeartbeatReader> Readers { get; } = new();

    public string LinkText => Link switch
    {
        HostLink.Live => "연결됨",
        HostLink.Stale => "응답 지연",
        _ => "연결 끊김"
    };
}

/// <summary>수집 모드: 여러 감시 PC의 하트비트와 이벤트를 모아 보여 준다.</summary>
public sealed partial class CollectorViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxEvents = 5000;
    private readonly CollectorServer _server;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _ageTimer;
    private readonly Dictionary<string, string> _remoteToHost = new();
    private readonly string _csvFolder;
    private readonly object _csvLock = new();

    public int Port { get; }
    public int StaleAfterSec { get; set; } = 180;
    public string AppVersion => typeof(CollectorViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    public ObservableCollection<HostTileViewModel> Hosts { get; } = new();
    public ObservableCollection<TagEvent> Events { get; } = new();

    [ObservableProperty] private string _listenText = "";
    [ObservableProperty] private int _clientCount;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private TagEvent? _selectedEvent;
    [ObservableProperty] private int _liveHosts;
    [ObservableProperty] private int _totalPresent;
    [ObservableProperty] private int _appearToday;
    [ObservableProperty] private int _removeToday;
    [ObservableProperty] private string _lastEventText = "-";

    public CollectorViewModel(int port, Dispatcher ui)
    {
        Port = port;
        _ui = ui;
        _csvFolder = Path.Combine(SettingsStore.DefaultLogFolder, "collector");
        _server = new CollectorServer(port);
        _server.ClientConnected += r => _ui.BeginInvoke(() => { ClientCount = _server.ClientCount; StatusMessage = $"접속: {r}"; });
        _server.ClientDisconnected += (r, h) => _ui.BeginInvoke(() => OnDisconnected(r, h));
        _server.HeartbeatReceived += (hb, r) => _ui.BeginInvoke(() => OnHeartbeat(hb, r));
        _server.EventReceived += (ev, r) => { WriteCsv(ev); _ui.BeginInvoke(() => OnEvent(ev, r)); };
        _server.Error += m => _ui.BeginInvoke(() => StatusMessage = "서버 오류: " + m);

        _ageTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => RefreshAges(), ui);
    }

    public void Start()
    {
        try
        {
            _server.Start();
            ListenText = $"포트 {Port} 대기 중";
            StatusMessage = $"수집 서버 시작. 감시 PC의 설정 → TCP 클라이언트에 이 PC({Environment.MachineName}):{Port} 를 넣으세요.";
            _ageTimer.Start();
        }
        catch (Exception ex)
        {
            ListenText = $"포트 {Port} 열기 실패";
            StatusMessage = ex.Message;
            Log.Error(ex, "수집 서버 시작 실패");
        }
    }

    private HostTileViewModel GetTile(string host)
    {
        var t = Hosts.FirstOrDefault(h => string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase));
        if (t is null)
        {
            t = new HostTileViewModel(host);
            Hosts.Add(t);
        }
        return t;
    }

    private void OnHeartbeat(HeartbeatMessage hb, string remote)
    {
        _remoteToHost[remote] = hb.Host;
        var t = GetTile(hb.Host);
        t.Remote = remote;
        t.Version = hb.Version;
        t.LastHeartbeat = hb.Time;
        t.ReaderCount = hb.Readers.Count;
        t.OnlineReaders = hb.OnlineReaders;
        t.PresentReaders = hb.PresentReaders;
        t.AppearToday = hb.AppearToday;
        t.RemoveToday = hb.RemoveToday;
        t.Link = HostLink.Live;
        t.Readers.Clear();
        foreach (var r in hb.Readers) t.Readers.Add(r);
        ClientCount = _server.ClientCount;
        RefreshTotals();
    }

    private void OnEvent(TagEvent ev, string remote)
    {
        if (!string.IsNullOrEmpty(ev.Host)) _remoteToHost[remote] = ev.Host;
        var t = GetTile(string.IsNullOrEmpty(ev.Host) ? remote : ev.Host);
        t.Remote = remote;
        t.LastEventTime = ev.Time;
        t.LastEventText = $"{ev.Time:HH:mm:ss} {ev.KindText} · {ev.DisplayName} · {(ev.Uid == PresenceTracker.UnknownUid ? "UID 미확인" : ev.Uid)}";
        if (t.Link == HostLink.Disconnected) t.Link = HostLink.Live;
        if (ev.Kind == TagEventKind.Appear) t.AppearToday++; else t.RemoveToday++;

        Events.Insert(0, ev);
        while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
        LastEventText = $"{ev.Host} · {t.LastEventText}";
        RefreshTotals();
    }

    private void OnDisconnected(string remote, string? host)
    {
        ClientCount = _server.ClientCount;
        var h = host ?? (_remoteToHost.TryGetValue(remote, out var known) ? known : null);
        if (h is null) return;
        var t = Hosts.FirstOrDefault(x => string.Equals(x.Host, h, StringComparison.OrdinalIgnoreCase));
        if (t is not null && t.Remote == remote)
        {
            t.Link = HostLink.Disconnected;
            StatusMessage = $"연결 끊김: {h} ({remote})";
        }
        RefreshTotals();
    }

    private void RefreshAges()
    {
        var now = DateTimeOffset.Now;
        foreach (var t in Hosts)
        {
            if (t.LastHeartbeat is DateTimeOffset hb)
            {
                var age = now - hb;
                t.AgeText = age.TotalSeconds < 90 ? $"{(int)age.TotalSeconds}초 전" : $"{(int)age.TotalMinutes}분 전";
                if (t.Link == HostLink.Live && age.TotalSeconds > StaleAfterSec) t.Link = HostLink.Stale;
            }
        }
        RefreshTotals();
    }

    private void RefreshTotals()
    {
        LiveHosts = Hosts.Count(h => h.Link == HostLink.Live);
        TotalPresent = Hosts.Sum(h => h.PresentReaders);
        AppearToday = Hosts.Sum(h => h.AppearToday);
        RemoveToday = Hosts.Sum(h => h.RemoveToday);
    }

    private void WriteCsv(TagEvent e)
    {
        try
        {
            Directory.CreateDirectory(_csvFolder);
            var path = Path.Combine(_csvFolder, $"events-{e.Time:yyyyMMdd}.csv");
            lock (_csvLock)
            {
                var isNew = !File.Exists(path);
                using var w = new StreamWriter(path, true, new UTF8Encoding(true));
                if (isNew) w.WriteLine(TagEvent.CsvHeader);
                w.WriteLine(e.ToCsv());
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "수집 CSV 기록 실패");
        }
    }

    [RelayCommand]
    private void ClearEvents() => Events.Clear();

    [RelayCommand]
    private Task CheckUpdate() => UpdateFlow.RunAsync(msg => StatusMessage = msg);

    [RelayCommand]
    private void OpenCsvFolder()
    {
        try
        {
            Directory.CreateDirectory(_csvFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", _csvFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { System.Windows.Clipboard.SetText(text); StatusMessage = $"복사됨: {text}"; }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    public async ValueTask DisposeAsync()
    {
        _ageTimer.Stop();
        await _server.DisposeAsync();
    }
}
