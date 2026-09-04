using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfidReaderMonitor.Core;
using RfidReaderMonitor.Modbus;
using RfidReaderMonitor.Settings;

namespace RfidReaderMonitor.ViewModels;

/// <summary>등록 대화상자의 프리셋. Apply 가 폼을 채운다.</summary>
public sealed record ModbusPreset(string Name, string Note, Action<ModbusReaderEditorViewModel>? Apply);

/// <summary>
/// Modbus TCP 리더 추가/편집 대화상자. 폼 + 연결 테스트.
/// 확인을 누르면 RequestClose(true) 가 나고, 호출자가 ToSettings() 로 값을 가져간다.
/// </summary>
public sealed partial class ModbusReaderEditorViewModel : ObservableObject, IDisposable
{
    public static ModbusArea[] Areas { get; } = Enum.GetValues<ModbusArea>();
    public static ModbusArea[] RegisterAreas { get; } = { ModbusArea.HoldingRegister, ModbusArea.InputRegister };
    public static CardFamily[] Families { get; } = { CardFamily.Iso15693, CardFamily.Iso14443A, CardFamily.Iso14443B, CardFamily.Unknown };

    private readonly Dispatcher _ui;
    private readonly IReadOnlyCollection<string> _takenNames;
    private readonly int _timeoutMs;

    public bool IsNew { get; }
    public string OriginalName { get; }
    public string Title => IsNew ? "Modbus TCP 리더 추가" : "Modbus TCP 리더 편집";

    public event Action<bool>? RequestClose;

    // ---- 폼
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private int _unitId = 1;
    [ObservableProperty] private int _pollMs = 100;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PresentIsRegister))] private ModbusArea _presentArea = ModbusArea.DiscreteInput;
    [ObservableProperty] private int _presentAddress;
    [ObservableProperty] private int _presentBit;
    [ObservableProperty] private ModbusArea _uidArea = ModbusArea.InputRegister;
    [ObservableProperty] private int _uidAddress;
    [ObservableProperty] private int _uidBytes = 8;
    [ObservableProperty] private bool _uidSwapBytes;
    [ObservableProperty] private bool _uidReverseWords;
    [ObservableProperty] private CardFamily _tagFamily = CardFamily.Iso15693;
    [ObservableProperty] private bool _enabled = true;

    [ObservableProperty] private string _errorText = "";
    public bool PresentIsRegister => PresentArea is ModbusArea.HoldingRegister or ModbusArea.InputRegister;

    // ---- 프리셋
    public ObservableCollection<ModbusPreset> Presets { get; } = new();
    [ObservableProperty] private ModbusPreset? _selectedPreset;

    // ---- 연결 테스트
    private Thread? _testThread;
    private volatile bool _testStop;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _testButtonText = "연결 테스트 시작";
    [ObservableProperty] private string _testLinkText = "테스트를 시작하면 입력한 값으로 바로 읽어 봅니다.";
    [ObservableProperty] private string _testLinkColor = "#888888";
    [ObservableProperty] private bool _testPresent;
    [ObservableProperty] private string _testPresentRaw = "";
    [ObservableProperty] private string _testBits = "";
    [ObservableProperty] private string _testUidWords = "";
    [ObservableProperty] private string _testUidBytes = "";
    [ObservableProperty] private string _testHint = "";
    [ObservableProperty] private string _testResponse = "";

    public ModbusReaderEditorViewModel(ModbusReaderSettings initial, bool isNew, IReadOnlyCollection<string> takenNames, int timeoutMs, Dispatcher ui)
    {
        _ui = ui;
        _takenNames = takenNames;
        _timeoutMs = timeoutMs;
        IsNew = isNew;
        OriginalName = initial.EffectiveName;
        Load(initial);

        Presets.Add(new ModbusPreset("직접 입력", "", null));
        Presets.Add(new ModbusPreset("시뮬레이터 (개발자 탭) — Discrete Input 방식", "이 앱의 개발자 탭 시뮬레이터 기본값", vm =>
        {
            vm.Host = "127.0.0.1"; vm.Port = 5020; vm.UnitId = 1; vm.PollMs = 100;
            vm.PresentArea = ModbusArea.DiscreteInput; vm.PresentAddress = 0; vm.PresentBit = 0;
            vm.UidArea = ModbusArea.InputRegister; vm.UidAddress = 0; vm.UidBytes = 8;
            vm.UidSwapBytes = false; vm.UidReverseWords = false; vm.TagFamily = CardFamily.Iso15693;
            if (string.IsNullOrWhiteSpace(vm.Name) || vm.Name.StartsWith("SIM", StringComparison.OrdinalIgnoreCase)) vm.Name = "SIM DI";
        }));
        Presets.Add(new ModbusPreset("시뮬레이터 (개발자 탭) — 레지스터 비트 방식", "Holding Register 10 비트 0", vm =>
        {
            vm.Host = "127.0.0.1"; vm.Port = 5020; vm.UnitId = 1; vm.PollMs = 100;
            vm.PresentArea = ModbusArea.HoldingRegister; vm.PresentAddress = 10; vm.PresentBit = 0;
            vm.UidArea = ModbusArea.HoldingRegister; vm.UidAddress = 0; vm.UidBytes = 8;
            vm.UidSwapBytes = false; vm.UidReverseWords = false; vm.TagFamily = CardFamily.Iso15693;
            if (string.IsNullOrWhiteSpace(vm.Name) || vm.Name.StartsWith("SIM", StringComparison.OrdinalIgnoreCase)) vm.Name = "SIM REG";
        }));
        Presets.Add(new ModbusPreset("HF 리더 일반형 — Discrete Input + Input Register", "Present 비트 하나, UID 8B. 주소는 장치 문서대로 고치세요", vm =>
        {
            vm.PresentArea = ModbusArea.DiscreteInput; vm.PresentBit = 0;
            vm.UidArea = ModbusArea.InputRegister; vm.UidBytes = 8; vm.TagFamily = CardFamily.Iso15693;
        }));
        Presets.Add(new ModbusPreset("HF 리더 일반형 — 상태 레지스터 비트 + Holding Register", "Present 가 상태 워드의 한 비트일 때", vm =>
        {
            vm.PresentArea = ModbusArea.HoldingRegister; vm.PresentBit = 0;
            vm.UidArea = ModbusArea.HoldingRegister; vm.UidBytes = 8; vm.TagFamily = CardFamily.Iso15693;
        }));
        _selectedPreset = Presets[0];
    }

    private void Load(ModbusReaderSettings s)
    {
        Enabled = s.Enabled; Name = s.Name; Host = s.Host; Port = s.Port; UnitId = s.UnitId; PollMs = s.PollMs;
        PresentArea = s.PresentArea; PresentAddress = s.PresentAddress; PresentBit = s.PresentBit;
        UidArea = s.UidArea; UidAddress = s.UidAddress; UidBytes = s.UidBytes;
        UidSwapBytes = s.UidSwapBytes; UidReverseWords = s.UidReverseWords; TagFamily = s.TagFamily;
    }

    public ModbusReaderSettings ToSettings() => new()
    {
        Enabled = Enabled, Name = Name.Trim(), Host = Host.Trim(), Port = Port, UnitId = UnitId, PollMs = PollMs,
        PresentArea = PresentArea, PresentAddress = PresentAddress, PresentBit = PresentBit,
        UidArea = UidArea, UidAddress = UidAddress, UidBytes = UidBytes,
        UidSwapBytes = UidSwapBytes, UidReverseWords = UidReverseWords, TagFamily = TagFamily
    };

    partial void OnSelectedPresetChanged(ModbusPreset? value)
    {
        if (value?.Apply is null) return;
        value.Apply(this);
        ErrorText = "";
    }

    private string? Validate()
    {
        var s = ToSettings();
        var err = ModbusRfidMap.Validate(s);
        if (err is not null) return err;
        var name = s.EffectiveName;
        if (!string.Equals(name, OriginalName, StringComparison.OrdinalIgnoreCase) || IsNew)
            if (_takenNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return $"이미 있는 리더 이름입니다: {name}";
        return null;
    }

    [RelayCommand]
    private void Ok()
    {
        var err = Validate();
        if (err is not null) { ErrorText = err; return; }
        StopTest();
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        StopTest();
        RequestClose?.Invoke(false);
    }

    // ---- 연결 테스트: 폼의 현재 값으로 0.3초마다 읽는다. 저장 전에 주소·바이트 순서를 맞추는 용도.

    [RelayCommand]
    private void ToggleTest()
    {
        if (IsTesting) { StopTest(); return; }
        var err = ModbusRfidMap.Validate(ToSettings());
        if (err is not null) { ErrorText = err; return; }
        ErrorText = "";
        _testStop = false;
        IsTesting = true;
        TestButtonText = "테스트 중지";
        TestLinkText = "접속 중…";
        TestLinkColor = "#C77700";
        _testThread = new Thread(TestLoop) { IsBackground = true, Name = "ModbusEditorTest" };
        _testThread.Start();
    }

    private void StopTest()
    {
        _testStop = true;
        _testThread?.Join(1500);
        _testThread = null;
        if (IsTesting)
        {
            IsTesting = false;
            TestButtonText = "연결 테스트 시작";
            TestLinkText = "테스트 중지됨";
            TestLinkColor = "#888888";
        }
    }

    private void TestLoop()
    {
        ModbusTcpClient? client = null;
        string? lastHost = null;
        int lastPort = -1;
        var sw = new System.Diagnostics.Stopwatch();
        while (!_testStop)
        {
            ModbusReaderSettings s;
            try { s = _ui.Invoke(ToSettings); } catch { break; }
            try
            {
                if (client is null || s.Host != lastHost || s.Port != lastPort || !client.IsConnected)
                {
                    client?.Dispose();
                    client = new ModbusTcpClient(s.Host, s.Port, _timeoutMs);
                    client.Connect();
                    lastHost = s.Host; lastPort = s.Port;
                }
                sw.Restart();
                var (present, raw) = ModbusRfidMap.ReadPresent(client, s);
                var words = ModbusRfidMap.ReadUidWords(client, s);
                sw.Stop();
                var uid = ModbusRfidMap.Decode(s, words);
                var ms = sw.Elapsed.TotalMilliseconds;
                _ui.BeginInvoke(() =>
                {
                    TestLinkText = $"● {s.Host}:{s.Port} 연결됨";
                    TestLinkColor = "#2E7D32";
                    TestResponse = $"응답 {ms:F1} ms";
                    TestPresent = present;
                    TestPresentRaw = s.PresentArea is ModbusArea.Coil or ModbusArea.DiscreteInput ? (present ? "1" : "0") : $"0x{raw:X4}";
                    TestBits = s.PresentArea is ModbusArea.Coil or ModbusArea.DiscreteInput ? "" : ModbusRfidMap.Bits16(raw);
                    TestUidWords = string.Join(" ", words.Select(w => w.ToString("X4")));
                    TestUidBytes = uid.Length == 0 ? "(모두 0)" : Hex.Of(uid);
                    TestHint = uid.Length == 0 ? (present ? "Present 는 켜졌는데 UID 가 0 입니다. UID 시작 주소를 확인하세요." : "") : ModbusRfidMap.UidOrderHint(uid);
                });
            }
            catch (Exception ex)
            {
                client?.Dispose();
                client = null;
                var msg = ex.Message;
                _ui.BeginInvoke(() =>
                {
                    TestLinkText = "○ " + msg;
                    TestLinkColor = "#B03A2E";
                    TestResponse = "";
                });
                for (int i = 0; i < 10 && !_testStop; i++) Thread.Sleep(100);
                continue;
            }
            for (int i = 0; i < 3 && !_testStop; i++) Thread.Sleep(100);
        }
        client?.Dispose();
    }

    public void Dispose() => StopTest();
}
