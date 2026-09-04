using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfidReaderMonitor.Core;
using RfidReaderMonitor.Modbus;
using RfidReaderMonitor.Settings;

namespace RfidReaderMonitor.ViewModels;

/// <summary>
/// 개발자 탭. 지금은 Modbus TCP RFID 리더 시뮬레이터 하나.
/// 같은 PC 의 ModbusReaderProvider 가 127.0.0.1 로 붙어 산업용 리더 흐름을 실물 없이 시험한다.
/// </summary>
public sealed partial class DevToolsViewModel : ObservableObject, IDisposable
{
    private const int MaxLog = 300;
    private readonly ModbusRfidSimulator _sim = new();
    private readonly Dispatcher _ui;
    private readonly Action<ModbusReaderSettings> _addReaderRow;
    private readonly DispatcherTimer _autoTimer;
    private readonly Random _rng = new();

    [ObservableProperty] private int _simPort = 5020;
    [ObservableProperty] private bool _simRunning;
    [ObservableProperty] private string _simButtonText = "시뮬레이터 시작";
    [ObservableProperty] private string _simStatusText = "꺼짐";

    [ObservableProperty] private bool _tagPresent;
    [ObservableProperty] private string _tagButtonText = "태그 놓기";
    [ObservableProperty] private string _uidHex = "E0 04 01 00 50 A1 B2 C3";
    [ObservableProperty] private string _uidError = "";

    [ObservableProperty] private int _presentBitAddress;
    [ObservableProperty] private int _presentRegisterAddress = 10;
    [ObservableProperty] private int _presentRegisterBit;
    [ObservableProperty] private int _uidAddress;
    [ObservableProperty] private int _uidBytes = 8;
    [ObservableProperty] private bool _swapBytes;
    [ObservableProperty] private bool _reverseWords;

    [ObservableProperty] private bool _autoToggle;
    [ObservableProperty] private int _autoPeriodMs = 1500;
    [ObservableProperty] private bool _autoChangeUid;

    public ObservableCollection<string> Logs { get; } = new();

    public DevToolsViewModel(Dispatcher ui, Action<ModbusReaderSettings> addReaderRow)
    {
        _ui = ui;
        _addReaderRow = addReaderRow;
        _sim.Log += line => _ui.BeginInvoke(() =>
        {
            Logs.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} {line}");
            while (Logs.Count > MaxLog) Logs.RemoveAt(Logs.Count - 1);
        });
        _sim.Changed += () => _ui.BeginInvoke(RefreshStatus);
        _autoTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(_autoPeriodMs), DispatcherPriority.Background, (_, _) => AutoTick(), ui);
        _autoTimer.Stop();
        ApplyMap();
        ApplyUid();
    }

    // ---- 시뮬레이터 시작/중지

    [RelayCommand]
    private void ToggleSim()
    {
        try
        {
            if (_sim.Running) _sim.Stop();
            else
            {
                if (SimPort is < 1 or > 65535) { SimStatusText = "포트는 1~65535"; return; }
                ApplyMap();
                ApplyUid();
                _sim.Start(SimPort);
            }
        }
        catch (Exception ex)
        {
            SimStatusText = "시작 실패: " + ex.Message;
            Logs.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} 시작 실패: {ex.Message}");
        }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        SimRunning = _sim.Running;
        SimButtonText = SimRunning ? "시뮬레이터 중지" : "시뮬레이터 시작";
        SimStatusText = SimRunning
            ? $"● 0.0.0.0:{_sim.Port} 대기 중 · 클라이언트 {_sim.ClientCount} · 요청 {_sim.RequestCount}"
            : "꺼짐";
    }

    /// <summary>요청 수는 이벤트 없이 늘어나므로 자동 타이머와 태그 토글 때 함께 갱신한다.</summary>
    private void AutoTick()
    {
        if (AutoToggle)
        {
            if (AutoChangeUid && !TagPresent) RandomUid();
            TagPresent = !TagPresent;
        }
        RefreshStatus();
    }

    // ---- 태그

    [RelayCommand]
    private void ToggleTag() => TagPresent = !TagPresent;

    partial void OnTagPresentChanged(bool value)
    {
        _sim.Present = value;
        TagButtonText = value ? "태그 빼기" : "태그 놓기";
        Logs.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} 태그 {(value ? "놓음" : "뺌")} UID {UidHex}");
        RefreshStatus();
    }

    [RelayCommand]
    private void RandomUid()
    {
        var b = new byte[Math.Clamp(UidBytes, 1, 32)];
        _rng.NextBytes(b);
        if (b.Length == 8) b[0] = 0xE0; // ISO 15693 모양
        UidHex = Hex.Of(b);
    }

    partial void OnUidHexChanged(string value) => ApplyUid();

    private void ApplyUid()
    {
        try
        {
            var clean = new string(UidHex.Where(Uri.IsHexDigit).ToArray());
            if (clean.Length % 2 != 0) throw new FormatException("hex 자릿수가 홀수");
            var bytes = Convert.FromHexString(clean);
            _sim.Uid = bytes;
            UidError = bytes.Length == 0 ? "UID 비어 있음" : bytes.Length != UidBytes ? $"{bytes.Length}B (맵은 {UidBytes}B)" : "";
        }
        catch (Exception ex)
        {
            UidError = ex.Message;
        }
    }

    // ---- 레지스터 맵

    partial void OnPresentBitAddressChanged(int value) => ApplyMap();
    partial void OnPresentRegisterAddressChanged(int value) => ApplyMap();
    partial void OnPresentRegisterBitChanged(int value) => ApplyMap();
    partial void OnUidAddressChanged(int value) => ApplyMap();
    partial void OnUidBytesChanged(int value) { ApplyMap(); ApplyUid(); }
    partial void OnSwapBytesChanged(bool value) => ApplyMap();
    partial void OnReverseWordsChanged(bool value) => ApplyMap();

    private void ApplyMap()
    {
        _sim.PresentBitAddress = PresentBitAddress;
        _sim.PresentRegisterAddress = PresentRegisterAddress;
        _sim.PresentRegisterBit = PresentRegisterBit;
        _sim.UidAddress = UidAddress;
        _sim.UidBytes = UidBytes;
        _sim.SwapBytes = SwapBytes;
        _sim.ReverseWords = ReverseWords;
    }

    // ---- 자동 반복

    partial void OnAutoToggleChanged(bool value)
    {
        _autoTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, AutoPeriodMs));
        if (value) _autoTimer.Start(); else _autoTimer.Stop();
    }

    partial void OnAutoPeriodMsChanged(int value) => _autoTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, value));

    // ---- 이 시뮬레이터를 리더로 등록 (등록 대화상자를 시뮬레이터 값으로 채워 연다)

    /// <summary>Present 를 Discrete Input 비트로 읽는 리더.</summary>
    [RelayCommand]
    private void AddReaderRowDiscrete() => _addReaderRow(new ModbusReaderSettings
    {
        Name = $"SIM DI :{SimPort}", Host = "127.0.0.1", Port = SimPort, UnitId = 1, PollMs = 100,
        PresentArea = ModbusArea.DiscreteInput, PresentAddress = PresentBitAddress, PresentBit = 0,
        UidArea = ModbusArea.InputRegister, UidAddress = UidAddress, UidBytes = UidBytes,
        UidSwapBytes = SwapBytes, UidReverseWords = ReverseWords, TagFamily = CardFamily.Iso15693
    });

    /// <summary>Present 를 Holding Register 의 한 비트로 읽는 리더.</summary>
    [RelayCommand]
    private void AddReaderRowRegister() => _addReaderRow(new ModbusReaderSettings
    {
        Name = $"SIM REG :{SimPort}", Host = "127.0.0.1", Port = SimPort, UnitId = 1, PollMs = 100,
        PresentArea = ModbusArea.HoldingRegister, PresentAddress = PresentRegisterAddress, PresentBit = PresentRegisterBit,
        UidArea = ModbusArea.HoldingRegister, UidAddress = UidAddress, UidBytes = UidBytes,
        UidSwapBytes = SwapBytes, UidReverseWords = ReverseWords, TagFamily = CardFamily.Iso15693
    });

    [RelayCommand]
    private void ClearLog() => Logs.Clear();

    public void Dispose()
    {
        _autoTimer.Stop();
        _sim.Dispose();
    }
}
