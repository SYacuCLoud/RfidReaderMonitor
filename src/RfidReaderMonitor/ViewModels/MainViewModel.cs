using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfidReaderMonitor.Acs;
using RfidReaderMonitor.Core;
using RfidReaderMonitor.Output;
using RfidReaderMonitor.PcSc;
using RfidReaderMonitor.Readers;
using RfidReaderMonitor.Settings;
using RfidReaderMonitor.Sys;
using Serilog;

namespace RfidReaderMonitor.ViewModels;

public sealed partial class SinkStatusRow : ObservableObject
{
    public SinkStatusRow(string name) => Name = name;
    public string Name { get; }
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _lastError = "";
}

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxEvents = 5000;
    private const int MaxRaw = 3000;

    private readonly AppSettings _settings;
    private readonly PcscReaderProvider _provider;
    private readonly PresenceTracker _tracker;
    private readonly EventDispatcher _dispatcher;
    private readonly RawSignalLogger _rawLogger;
    private readonly Dispatcher _ui;
    private IReadOnlyList<UsbReaderDevice> _devices = Array.Empty<UsbReaderDevice>();
    private CancellationTokenSource? _testCts;

    public ObservableCollection<ReaderItemViewModel> Readers { get; } = new();
    public ObservableCollection<TagEvent> Events { get; } = new();
    public ObservableCollection<RawSignal> RawSignals { get; } = new();
    public ObservableCollection<SinkStatusRow> SinkStatuses { get; } = new();

    [ObservableProperty] private ReaderItemViewModel? _selectedReader;
    [ObservableProperty] private TagEvent? _selectedEvent;

    // 상단 대시보드
    [ObservableProperty] private string _dashReaders = "0 / 0";
    [ObservableProperty] private string _dashReadersSub = "온라인 / 전체";
    [ObservableProperty] private int _dashPresent;
    [ObservableProperty] private int _dashAppearToday;
    [ObservableProperty] private int _dashRemoveToday;
    [ObservableProperty] private int _dashUnknownToday;
    [ObservableProperty] private string _dashAvgDwell = "-";
    [ObservableProperty] private string _dashLastEvent = "-";
    [ObservableProperty] private string _dashLastEventSub = "이벤트 없음";
    [ObservableProperty] private string _dashSinks = "0";
    [ObservableProperty] private string _dashSinksSub = "출력 없음";
    [ObservableProperty] private bool _dashSinksError;

    private DateOnly _dashDay = DateOnly.FromDateTime(DateTime.Today);
    private long _dwellSum;
    private int _dwellCount;

    private void RefreshDashboardReaders()
    {
        var total = Readers.Count;
        var online = Readers.Count(r => r.IsOnline);
        DashReaders = $"{online} / {total}";
        DashPresent = Readers.Count(r => r.State is PresenceState.Present or PresenceState.InUse or PresenceState.Mute);
        ScheduleHeartbeatSoon();
    }

    private void RefreshDashboardEvent(TagEvent ev)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (today != _dashDay)
        {
            _dashDay = today;
            DashAppearToday = 0;
            DashRemoveToday = 0;
            DashUnknownToday = 0;
            _dwellSum = 0;
            _dwellCount = 0;
        }

        if (ev.Kind == TagEventKind.Appear)
        {
            DashAppearToday++;
            if (ev.Uid == PresenceTracker.UnknownUid) DashUnknownToday++;
        }
        else
        {
            DashRemoveToday++;
            if (ev.DwellMs is long d)
            {
                _dwellSum += d;
                _dwellCount++;
                DashAvgDwell = _dwellSum / _dwellCount >= 60_000
                    ? $"{_dwellSum / _dwellCount / 60_000.0:F1}분"
                    : $"{_dwellSum / _dwellCount / 1000.0:F1}초";
            }
        }

        DashLastEvent = ev.Time.ToString("HH:mm:ss");
        DashLastEventSub = $"{ev.KindText} · {ev.DisplayName} · {(ev.Uid == PresenceTracker.UnknownUid ? "UID 미확인" : ev.Uid)}";
    }

    private void RefreshDashboardSinks()
    {
        var total = SinkStatuses.Count;
        var errors = SinkStatuses.Count(s => !string.IsNullOrEmpty(s.LastError));
        DashSinks = total.ToString();
        DashSinksError = errors > 0;
        DashSinksSub = total == 0 ? "출력 없음"
            : errors > 0 ? $"오류 {errors} · " + string.Join(", ", SinkStatuses.Select(s => s.Name))
            : string.Join(", ", SinkStatuses.Select(s => s.Name));
    }

    // 하단 라이브 배너 (가장 최근 확정 이벤트)
    [ObservableProperty] private bool _bannerVisible;
    [ObservableProperty] private bool _bannerIsAppear;
    [ObservableProperty] private string _bannerAlias = "";
    [ObservableProperty] private string _bannerSerial = "";
    [ObservableProperty] private string _bannerReaderName = "";
    [ObservableProperty] private string _bannerUid = "";
    [ObservableProperty] private string _bannerTech = "";
    [ObservableProperty] private string _bannerKind = "";
    [ObservableProperty] private string _bannerDetail = "";
    [ObservableProperty] private DateTimeOffset? _bannerTime;
    /// <summary>값이 바뀔 때마다 등장 애니메이션이 한 번 재생된다.</summary>
    [ObservableProperty] private bool _appearFlash;
    /// <summary>값이 바뀔 때마다 제거 애니메이션이 한 번 재생된다.</summary>
    [ObservableProperty] private bool _removeFlash;

    private void UpdateBanner(TagEvent ev)
    {
        BannerVisible = true;
        BannerIsAppear = ev.Kind == TagEventKind.Appear;
        BannerAlias = string.IsNullOrWhiteSpace(ev.Alias) || ev.Alias == ev.ReaderName ? "" : ev.Alias;
        BannerSerial = string.IsNullOrWhiteSpace(ev.Serial) ? "S/N 없음" : ev.Serial;
        BannerReaderName = ev.ReaderName;
        BannerUid = ev.Uid == PresenceTracker.UnknownUid ? "UID 미확인" : ev.Uid;
        BannerTech = ev.Tech;
        BannerKind = ev.KindText;
        BannerTime = ev.Time;
        BannerDetail = ev.Kind == TagEventKind.Remove && ev.DwellMs is long d
            ? $"체류 {d / 1000.0:F1}초"
            : "";
        if (ev.Kind == TagEventKind.Appear) AppearFlash = !AppearFlash;
        else RemoveFlash = !RemoveFlash;
    }

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { StatusMessage = "복사할 값이 없습니다."; return; }
        try
        {
            Clipboard.SetText(text);
            StatusMessage = $"복사됨: {text}";
        }
        catch (Exception ex)
        {
            StatusMessage = "클립보드 복사 실패: " + ex.Message;
        }
    }
    [ObservableProperty] private string _serviceStatus = "초기화 중";
    [ObservableProperty] private bool _serviceHealthy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _scPnpText = "";
    [ObservableProperty] private bool _scPnpNeedsAction;
    [ObservableProperty] private bool _scPnpIsDisabled;
    [ObservableProperty] private string _scardServiceText = "";
    [ObservableProperty] private bool _isElevated;

    // 설정 바인딩
    [ObservableProperty] private int _removalDebounceMs;
    [ObservableProperty] private bool _reverseIso15693;
    [ObservableProperty] private bool _beepOnAppear;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _logRawEvents;
    [ObservableProperty] private string _logFolder = "";
    [ObservableProperty] private bool _csvEnabled;
    [ObservableProperty] private string _csvFolder = "";
    [ObservableProperty] private bool _tcpEnabled;
    [ObservableProperty] private int _tcpPort;
    [ObservableProperty] private bool _pipeEnabled;
    [ObservableProperty] private string _pipeName = "";
    [ObservableProperty] private bool _sqlEnabled;
    [ObservableProperty] private string _sqlConnectionString = "";
    [ObservableProperty] private string _sqlTable = "";
    [ObservableProperty] private bool _sqlAutoCreate;
    [ObservableProperty] private bool _tcpClientEnabled;
    [ObservableProperty] private string _tcpClientHost = "";
    [ObservableProperty] private int _tcpClientPort;
    [ObservableProperty] private int _heartbeatSec;
    [ObservableProperty] private bool _autostartEnabled;

    // 제조사 도구 (선택 리더의 제조사에 맞는 뷰모델, 없으면 null)
    private readonly AcsToolsViewModel _acsTools;
    [ObservableProperty] private object? _vendorTools;
    [ObservableProperty] private string _vendorToolsHeader = "제조사 도구";
    [ObservableProperty] private string _vendorToolsMessage = "리더를 선택하세요.";

    // 인식률 시험
    [ObservableProperty] private int _testDurationSec;
    [ObservableProperty] private int _testIntervalMs;
    [ObservableProperty] private bool _testRunning;
    [ObservableProperty] private string _testProgressText = "";
    [ObservableProperty] private string _testResultText = "";
    public ObservableCollection<string> TestHistory { get; } = new();

    public string SettingsPath => SettingsStore.SettingsPath;
    public string AppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    public MainViewModel(AppSettings settings, PcscReaderProvider provider, PresenceTracker tracker,
        EventDispatcher dispatcher, RawSignalLogger rawLogger, Dispatcher ui)
    {
        _settings = settings;
        _provider = provider;
        _tracker = tracker;
        _dispatcher = dispatcher;
        _rawLogger = rawLogger;
        _ui = ui;
        _acsTools = new AcsToolsViewModel(provider, msg => StatusMessage = msg);

        LoadSettingsToProperties();

        _provider.ReadersChanged += (_, e) => _ui.BeginInvoke(() => OnReadersChanged(e.Readers));
        _provider.PresenceChanged += (_, e) => { _tracker.OnPresence(e); _ui.BeginInvoke(() => OnPresence(e)); };
        _provider.StatusChanged += (_, e) => _ui.BeginInvoke(() =>
        {
            ServiceHealthy = e.Healthy;
            ServiceStatus = e.Message;
        });

        _tracker.Confirmed += OnConfirmed;
        _tracker.Raw += s =>
        {
            _rawLogger.Write(s);
            _ui.BeginInvoke(() =>
            {
                RawSignals.Insert(0, s);
                while (RawSignals.Count > MaxRaw) RawSignals.RemoveAt(RawSignals.Count - 1);
            });
        };

        _dispatcher.SinkError += (s, msg) => _ui.BeginInvoke(() =>
        {
            var row = SinkStatuses.FirstOrDefault(r => r.Name == s.Name);
            if (row is not null) row.LastError = $"{DateTime.Now:HH:mm:ss} {msg}";
            RefreshDashboardSinks();
        });
        _dispatcher.SinksChanged += () => _ui.BeginInvoke(RefreshSinkStatuses);
    }

    // ------------------------------------------------------------ 초기화

    public async Task InitializeAsync()
    {
        IsElevated = SystemChecks.IsElevated();
        CheckSystem();
        await ApplySinksAsync();
        _provider.Start();
        RestartHeartbeatTimer();
    }

    // ------------------------------------------------------------ 하트비트 (상태 메시지)

    private DispatcherTimer? _heartbeatTimer;
    private Timer? _heartbeatSoonTimer;

    private void RestartHeartbeatTimer()
    {
        _heartbeatTimer?.Stop();
        _heartbeatTimer = null;
        if (_settings.HeartbeatSec <= 0) return;
        _heartbeatTimer = new DispatcherTimer(TimeSpan.FromSeconds(_settings.HeartbeatSec), DispatcherPriority.Background,
            (_, _) => SendHeartbeat(), _ui);
        _heartbeatTimer.Start();
        SendHeartbeat();
    }

    /// <summary>리더 상태가 바뀌면 1초 안에 한 번 더 보낸다 (연속 변화는 하나로 합침).</summary>
    private void ScheduleHeartbeatSoon()
    {
        if (_settings.HeartbeatSec <= 0) return;
        _heartbeatSoonTimer?.Dispose();
        _heartbeatSoonTimer = new Timer(_ => _ui.BeginInvoke(SendHeartbeat), null, 1000, Timeout.Infinite);
    }

    private void SendHeartbeat()
    {
        if (_dispatcher.Sinks.Count == 0) return;
        var readers = Readers.Select(r => new HeartbeatReader(r.Name, r.Alias, r.Serial, r.StateText, r.Uid)).ToList();
        var hb = new HeartbeatMessage(DateTimeOffset.Now, Environment.MachineName, AppVersion, readers, DashAppearToday, DashRemoveToday);
        _dispatcher.PublishHeartbeat(hb);
    }

    private void LoadSettingsToProperties()
    {
        RemovalDebounceMs = _settings.RemovalDebounceMs;
        ReverseIso15693 = _settings.ReverseIso15693Uid;
        BeepOnAppear = _settings.BeepOnAppear;
        MinimizeToTray = _settings.MinimizeToTray;
        StartMinimized = _settings.StartMinimized;
        LogRawEvents = _settings.LogRawEvents;
        LogFolder = _settings.EffectiveLogFolder;
        CsvEnabled = _settings.Csv.Enabled;
        CsvFolder = _settings.EffectiveCsvFolder;
        TcpEnabled = _settings.Tcp.Enabled;
        TcpPort = _settings.Tcp.Port;
        PipeEnabled = _settings.Pipe.Enabled;
        PipeName = _settings.Pipe.Name;
        SqlEnabled = _settings.Sql.Enabled;
        SqlConnectionString = _settings.Sql.ConnectionString;
        SqlTable = _settings.Sql.Table;
        SqlAutoCreate = _settings.Sql.AutoCreateTable;
        TcpClientEnabled = _settings.TcpClient.Enabled;
        TcpClientHost = _settings.TcpClient.Host;
        TcpClientPort = _settings.TcpClient.Port;
        HeartbeatSec = _settings.HeartbeatSec;
        TestDurationSec = _settings.TestDurationSec;
        TestIntervalMs = _settings.TestIntervalMs;
        AutostartEnabled = SystemChecks.IsAutostartEnabled();

        _tracker.RemovalDebounceMs = RemovalDebounceMs;
        _tracker.ReverseIso15693 = ReverseIso15693;
        _rawLogger.Enabled = LogRawEvents;
    }

    partial void OnSelectedReaderChanged(ReaderItemViewModel? value) => UpdateVendorTools();

    /// <summary>선택 리더의 제조사에 맞는 도구 뷰모델을 고른다. 식별이 끝난 뒤에도 다시 불린다.</summary>
    private void UpdateVendorTools()
    {
        var r = SelectedReader;
        if (r is null)
        {
            VendorTools = null;
            VendorToolsHeader = "제조사 도구";
            VendorToolsMessage = "리더를 선택하세요.";
            return;
        }
        if (AcsToolsViewModel.Supports(r))
        {
            _acsTools.Reader = r;
            VendorTools = _acsTools;
            VendorToolsHeader = "제조사 도구 (ACS)";
            VendorToolsMessage = "";
            return;
        }
        VendorTools = null;
        VendorToolsHeader = "제조사 도구";
        VendorToolsMessage = string.IsNullOrWhiteSpace(r.Vendor)
            ? "리더 정보를 아직 읽지 못했습니다. 잠시 후 다시 선택하거나 '리더 정보 갱신'을 누르세요."
            : $"이 리더(제조사 {r.Vendor})에 맞는 제조사 도구가 없습니다. 표준 PC/SC 기능은 모두 사용할 수 있습니다.";
    }

    partial void OnRemovalDebounceMsChanged(int value) => _tracker.RemovalDebounceMs = Math.Max(0, value);
    partial void OnReverseIso15693Changed(bool value) => _tracker.ReverseIso15693 = value;
    partial void OnLogRawEventsChanged(bool value) => _rawLogger.Enabled = value;

    partial void OnAutostartEnabledChanged(bool value)
    {
        try
        {
            SystemChecks.SetAutostart(value, StartMinimized);
            StatusMessage = value ? "로그인 시 자동 시작 등록됨" : "자동 시작 해제됨";
        }
        catch (Exception ex)
        {
            StatusMessage = "자동 시작 설정 실패: " + ex.Message;
        }
    }

    // ------------------------------------------------------------ 리더 목록/상태

    private void OnReadersChanged(IReadOnlyList<string> names)
    {
        _devices = UsbDeviceMapper.EnumerateSmartCardReaders();

        foreach (var r in Readers.ToList())
        {
            if (!names.Contains(r.Name))
            {
                r.State = PresenceState.Removed;
                r.LastChange = DateTimeOffset.Now;
                _tracker.ResetReader(r.Name, "리더 제거");
                Readers.Remove(r);
                StatusMessage = $"리더 제거: {r.DisplayName}";
            }
        }

        foreach (var n in names)
        {
            var vm = Readers.FirstOrDefault(r => r.Name == n);
            if (vm is null)
            {
                vm = new ReaderItemViewModel(n);
                vm.PropertyChanged += (s, e) =>
                {
                    // 별명/메모는 입력하면 자동 저장 (저장 버튼은 즉시 저장용으로 유지)
                    if (e.PropertyName is nameof(ReaderItemViewModel.Alias) or nameof(ReaderItemViewModel.Note))
                        ScheduleProfileSave((ReaderItemViewModel)s!);
                };
                Readers.Add(vm);
                StatusMessage = $"리더 연결: {n}";
                _ = IdentifyAsync(vm);
            }
        }

        SelectedReader ??= Readers.FirstOrDefault();
        if (SelectedReader is not null && !Readers.Contains(SelectedReader)) SelectedReader = Readers.FirstOrDefault();
        RefreshDashboardReaders();
    }

    private async Task IdentifyAsync(ReaderItemViewModel vm)
    {
        try
        {
            var id = await Task.Run(() => _provider.Identify(vm.Name));
            vm.Vendor = id.Vendor ?? "";
            vm.IfdType = id.IfdType ?? "";
            vm.IfdVersion = id.IfdVersion ?? "";

            var dev = UsbDeviceMapper.Match(_devices, id.Serial, vm.Name);
            if (!string.IsNullOrWhiteSpace(id.Serial))
            {
                vm.Serial = id.Serial;
                vm.SerialSource = "WinSCard 속성";
            }
            else if (dev?.Serial is not null)
            {
                vm.Serial = dev.Serial;
                vm.SerialSource = "USB 장치 (WMI)";
            }
            else
            {
                vm.Serial = "";
                vm.SerialSource = dev?.PortPath is not null ? "시리얼 없음, USB 포트 경로로 대체" : "시리얼 없음";
            }

            if (dev is not null)
            {
                vm.DeviceInstanceId = dev.InstanceId;
                vm.PortPath = dev.PortPath ?? "";
                vm.DriverService = dev.DriverService ?? "";
                vm.EscapeEnabled = dev.EscapeEnabled;
            }

            vm.Key = !string.IsNullOrWhiteSpace(vm.Serial) ? vm.Serial
                   : !string.IsNullOrWhiteSpace(vm.PortPath) ? "port:" + vm.PortPath
                   : "name:" + vm.Name;

            var profile = _settings.GetOrCreate(vm.Key);
            vm.Alias = profile.Alias;
            vm.Note = profile.Note;
            profile.LastName = vm.Name;
            profile.LastSeen = DateTimeOffset.Now;
            SettingsStore.Save(_settings);

            if (vm == SelectedReader) UpdateVendorTools();
            if (vm.EscapeEnabled == true && AcsToolsViewModel.Supports(vm)) await _acsTools.ReadFirmwareAsync(vm);
        }
        catch (Exception ex)
        {
            vm.LastError = ex.Message;
            Log.Warning(ex, "리더 식별 실패 {Name}", vm.Name);
        }
    }

    private void OnPresence(ReaderPresenceEventArgs e)
    {
        var vm = Readers.FirstOrDefault(r => r.Name == e.ReaderName);
        if (vm is null) return;
        vm.State = e.State;
        vm.LastChange = e.Time;
        RefreshDashboardReaders();
        if (e.State == PresenceState.Empty)
        {
            vm.Uid = "";
            vm.UidLength = "";
            vm.Tech = "";
            vm.Atr = "";
        }
        else if (e.Atr.Length > 0)
        {
            vm.Atr = Hex.Of(e.Atr);
            vm.Tech = AtrParser.Parse(e.Atr).ToString();
        }
    }

    private void OnConfirmed(TrackerEvent t)
    {
        _ui.BeginInvoke(() =>
        {
            var vm = Readers.FirstOrDefault(r => r.Name == t.ReaderName);
            var alias = vm?.Alias?.Trim() ?? "";
            var serial = vm?.Serial ?? "";

            if (vm is not null)
            {
                if (t.Kind == TagEventKind.Appear)
                {
                    vm.Uid = t.Uid;
                    vm.UidLength = t.UidBytes.Length == 0 ? "" : UidFormatter.DescribeLength(t.UidBytes, t.Tech.Family);
                    vm.Tech = t.Tech.ToString();
                    vm.Atr = t.Atr;
                    vm.AppearCount++;
                    if (BeepOnAppear && vm.EscapeEnabled == true)
                        _ = Task.Run(() => { try { _provider.Escape(vm.Name, AcsEscape.Buzzer(0x05)); } catch { } });
                }
            }

            var ev = new TagEvent(t.Time, t.Kind, t.ReaderName, alias, serial, t.Uid, t.Tech.ToString(), t.Atr, t.DwellMs);
            Events.Insert(0, ev);
            while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
            UpdateBanner(ev);
            RefreshDashboardEvent(ev);
            _dispatcher.Publish(ev);
        });
    }

    // ------------------------------------------------------------ 명령: 리더

    [RelayCommand]
    private void Refresh()
    {
        _provider.Refresh();
        CheckSystem();
        StatusMessage = "리더 목록 재조회";
    }

    [RelayCommand]
    private async Task ReIdentify()
    {
        if (SelectedReader is null) return;
        _devices = UsbDeviceMapper.EnumerateSmartCardReaders();
        await IdentifyAsync(SelectedReader);
        StatusMessage = "리더 정보 갱신";
    }

    private Timer? _profileSaveTimer;
    private ReaderItemViewModel? _profileSaveTarget;

    /// <summary>별명/메모 입력 중 잦은 저장을 피하기 위한 짧은 지연 저장.</summary>
    private void ScheduleProfileSave(ReaderItemViewModel vm)
    {
        _profileSaveTarget = vm;
        _profileSaveTimer?.Dispose();
        _profileSaveTimer = new Timer(_ => _ui.BeginInvoke(() =>
        {
            var target = _profileSaveTarget;
            if (target is null) return;
            SaveProfile(target);
            StatusMessage = $"별명 자동 저장: {target.Key} → {target.Alias.Trim()}";
        }), null, 600, Timeout.Infinite);
    }

    private void SaveProfile(ReaderItemViewModel vm)
    {
        var p = _settings.GetOrCreate(vm.Key);
        p.Alias = vm.Alias.Trim();
        p.Note = vm.Note.Trim();
        p.LastName = vm.Name;
        p.LastSeen = DateTimeOffset.Now;
        SettingsStore.Save(_settings);
    }

    [RelayCommand]
    private void SaveAlias()
    {
        if (SelectedReader is null) return;
        _profileSaveTimer?.Dispose();
        _profileSaveTimer = null;
        SaveProfile(SelectedReader);
        StatusMessage = $"별명 저장: {SelectedReader.Key} → {SelectedReader.Alias.Trim()}";
    }

    [RelayCommand]
    private async Task ReadUidNow()
    {
        if (SelectedReader is null) return;
        var vm = SelectedReader;
        try
        {
            var r = await Task.Run(() => _provider.ReadTag(vm.Name));
            vm.Uid = UidFormatter.Format(r.Uid, r.Tech.Family, ReverseIso15693);
            vm.UidLength = UidFormatter.DescribeLength(r.Uid, r.Tech.Family);
            vm.Tech = r.Tech.ToString();
            vm.Atr = Hex.Of(r.Atr);
            vm.LastError = "";
            StatusMessage = $"UID {vm.Uid}";
        }
        catch (Exception ex)
        {
            vm.LastError = ex.Message;
            StatusMessage = "UID 읽기 실패: " + ex.Message;
        }
    }

    // ------------------------------------------------------------ 명령: 시스템 점검

    [RelayCommand]
    private void CheckSystem()
    {
        var pnp = SystemChecks.IsScPnpDisabled();
        ScPnpNeedsAction = pnp == false;
        ScPnpIsDisabled = pnp == true;
        ScPnpText = pnp switch
        {
            true => "스마트카드 PnP 꺼짐 (권장 상태)",
            false => "스마트카드 PnP 켜짐. 새 태그마다 Windows가 드라이버를 찾다가 실패 알림을 띄웁니다.",
            null => "정책 확인 불가"
        };
        ScardServiceText = "SCardSvr: " + (SystemChecks.SmartCardServiceState() ?? "확인 불가");
        StatusMessage = $"Windows 점검 완료 — {ScardServiceText}, 스마트카드 PnP {(pnp switch { true => "꺼짐", false => "켜짐(조치 권장)", null => "확인 불가" })}";
    }

    [RelayCommand]
    private void ApplyScPnp()
    {
        var ok = SystemChecks.ApplyScPnpDisable();
        CheckSystem();
        StatusMessage = ok ? "스마트카드 PnP 정책 적용됨" : "적용 취소 또는 실패. 관리자 권한으로 직접 실행: " + SystemChecks.ScPnpCommand;
    }

    [RelayCommand]
    private void RestoreScPnp()
    {
        var ok = SystemChecks.ApplyScPnpRestore();
        CheckSystem();
        StatusMessage = ok ? "스마트카드 PnP 정책 원복(Windows 기본, 켜짐)" : "원복 취소 또는 실패. 관리자 권한으로 직접 실행: " + SystemChecks.ScPnpRestoreCommand;
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.EffectiveLogFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", _settings.EffectiveLogFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    // ------------------------------------------------------------ 명령: 이벤트

    [RelayCommand]
    private void ClearEvents()
    {
        Events.Clear();
        RawSignals.Clear();
    }

    [RelayCommand]
    private void ExportEvents()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"rfid-events-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder();
        sb.AppendLine(TagEvent.CsvHeader);
        foreach (var e in Events.Reverse()) sb.AppendLine(e.ToCsv());
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        StatusMessage = $"내보내기 완료: {dlg.FileName}";
    }

    // ------------------------------------------------------------ 명령: 설정/싱크

    [RelayCommand]
    private async Task SaveSettings()
    {
        _settings.RemovalDebounceMs = Math.Max(0, RemovalDebounceMs);
        _settings.ReverseIso15693Uid = ReverseIso15693;
        _settings.BeepOnAppear = BeepOnAppear;
        _settings.MinimizeToTray = MinimizeToTray;
        _settings.StartMinimized = StartMinimized;
        _settings.LogRawEvents = LogRawEvents;
        _settings.LogFolder = LogFolder == SettingsStore.DefaultLogFolder ? "" : LogFolder.Trim();
        _settings.Csv.Enabled = CsvEnabled;
        _settings.Csv.Folder = CsvFolder.Trim();
        _settings.Tcp.Enabled = TcpEnabled;
        _settings.Tcp.Port = TcpPort;
        _settings.Pipe.Enabled = PipeEnabled;
        _settings.Pipe.Name = PipeName.Trim();
        _settings.Sql.Enabled = SqlEnabled;
        _settings.Sql.ConnectionString = SqlConnectionString.Trim();
        _settings.Sql.Table = SqlTable.Trim();
        _settings.Sql.AutoCreateTable = SqlAutoCreate;
        _settings.TcpClient.Enabled = TcpClientEnabled;
        _settings.TcpClient.Host = TcpClientHost.Trim();
        _settings.TcpClient.Port = TcpClientPort;
        _settings.HeartbeatSec = Math.Max(0, HeartbeatSec);
        _settings.TestDurationSec = TestDurationSec;
        _settings.TestIntervalMs = TestIntervalMs;
        SettingsStore.Save(_settings);
        if (AutostartEnabled) SystemChecks.SetAutostart(true, StartMinimized);
        await ApplySinksAsync();
        RestartHeartbeatTimer();
        StatusMessage = "설정 저장 및 출력 재구성 완료";
    }

    private async Task ApplySinksAsync()
    {
        var sinks = new List<IEventSink>();
        if (_settings.Csv.Enabled) sinks.Add(new CsvEventSink(_settings.EffectiveCsvFolder));
        if (_settings.Tcp.Enabled) sinks.Add(new TcpBroadcastSink(_settings.Tcp.Port));
        if (_settings.Pipe.Enabled && !string.IsNullOrWhiteSpace(_settings.Pipe.Name)) sinks.Add(new NamedPipeSink(_settings.Pipe.Name));
        if (_settings.TcpClient.Enabled && !string.IsNullOrWhiteSpace(_settings.TcpClient.Host))
            sinks.Add(new TcpClientSink(_settings.TcpClient.Host, _settings.TcpClient.Port, _settings.EffectiveQueueFolder));
        if (_settings.Sql.Enabled && !string.IsNullOrWhiteSpace(_settings.Sql.ConnectionString))
        {
            try { sinks.Add(new SqlServerSink(_settings.Sql.ConnectionString, _settings.Sql.Table, _settings.Sql.AutoCreateTable, _settings.EffectiveQueueFolder)); }
            catch (Exception ex) { StatusMessage = "SQL 싱크 구성 오류: " + ex.Message; }
        }

        SinkStatuses.Clear();
        foreach (var s in sinks)
        {
            var row = new SinkStatusRow(s.Name) { Status = "시작 중" };
            SinkStatuses.Add(row);
            s.StatusChanged += sink => _ui.BeginInvoke(() => row.Status = sink.Status);
        }
        await _dispatcher.ReconfigureAsync(sinks);
        RefreshSinkStatuses();
    }

    private void RefreshSinkStatuses()
    {
        foreach (var row in SinkStatuses)
        {
            var s = _dispatcher.Sinks.FirstOrDefault(x => x.Name == row.Name);
            row.Status = s?.Status ?? "시작 실패";
        }
        RefreshDashboardSinks();
    }

    // ------------------------------------------------------------ 명령: 인식률 시험

    [RelayCommand]
    private async Task StartTest()
    {
        if (SelectedReader is null || TestRunning) return;
        var vm = SelectedReader;
        _testCts = new CancellationTokenSource();
        TestRunning = true;
        TestResultText = "";
        var progress = new Progress<ReadRateProgress>(p =>
            TestProgressText = $"{p.ElapsedMs / 1000.0:F1}s  시도 {p.Attempts}  성공 {p.Successes}  현재 끊김 {p.CurrentGapMs}ms  최대 끊김 {p.MaxGapMs}ms  UID {p.LastUid}");
        try
        {
            var result = await ReadRateTester.RunAsync(_provider, vm.Name, TimeSpan.FromSeconds(Math.Max(1, TestDurationSec)),
                Math.Max(10, TestIntervalMs), ReverseIso15693, progress, _testCts.Token);
            TestResultText = result.Summary + (result.DistinctUids.Count > 0 ? "\nUID: " + string.Join(", ", result.DistinctUids) : "");
            TestHistory.Insert(0, $"{DateTime.Now:HH:mm:ss} [{vm.DisplayName}] {result.Summary}");
        }
        catch (Exception ex)
        {
            TestResultText = "시험 오류: " + ex.Message;
        }
        finally
        {
            TestRunning = false;
            _testCts.Dispose();
            _testCts = null;
        }
    }

    [RelayCommand]
    private void StopTest() => _testCts?.Cancel();

    // ------------------------------------------------------------ 종료

    public void PersistOnExit()
    {
        _settings.TestDurationSec = TestDurationSec;
        _settings.TestIntervalMs = TestIntervalMs;
        SettingsStore.Save(_settings);
    }
}
