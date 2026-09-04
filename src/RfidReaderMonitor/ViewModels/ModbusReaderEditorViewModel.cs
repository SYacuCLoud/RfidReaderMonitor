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
        // Turck TBEN-S2-2RFID-4DXP 매뉴얼 7.2.8 "Modbus TCP — mapping" (2022/09). Idle 모드에서 TP 비트와 UID 가 입력 데이터에 실린다.
        // Read data Byte 0 이 레지스터 하위 바이트(비트 오프셋 0) → 바이트 스왑. E0 시작 여부는 실물 연결 테스트로 확인.
        Presets.Add(new ModbusPreset("Turck TBEN-S2-2RFID-4DXP — 채널 0 (HF, Idle 모드)", "TP = Holding 0x0002 비트 0, UID = Holding 0x000C~ (바이트 스왑). 매뉴얼 7.2.8", vm =>
        {
            vm.Port = 502; vm.UnitId = 1; vm.PollMs = 100;
            vm.PresentArea = ModbusArea.HoldingRegister; vm.PresentAddress = 0x0002; vm.PresentBit = 0;
            vm.UidArea = ModbusArea.HoldingRegister; vm.UidAddress = 0x000C; vm.UidBytes = 8;
            vm.UidSwapBytes = true; vm.UidReverseWords = false; vm.TagFamily = CardFamily.Iso15693;
            if (string.IsNullOrWhiteSpace(vm.Name)) vm.Name = "TBEN RFID Ch0";
        }));
        Presets.Add(new ModbusPreset("Turck TBEN-S2-2RFID-4DXP — 채널 1 (HF, Idle 모드)", "TP = Holding 0x004E 비트 0, UID = Holding 0x0058~ (바이트 스왑). 매뉴얼 7.2.8", vm =>
        {
            vm.Port = 502; vm.UnitId = 1; vm.PollMs = 100;
            vm.PresentArea = ModbusArea.HoldingRegister; vm.PresentAddress = 0x004E; vm.PresentBit = 0;
            vm.UidArea = ModbusArea.HoldingRegister; vm.UidAddress = 0x0058; vm.UidBytes = 8;
            vm.UidSwapBytes = true; vm.UidReverseWords = false; vm.TagFamily = CardFamily.Iso15693;
            if (string.IsNullOrWhiteSpace(vm.Name)) vm.Name = "TBEN RFID Ch1";
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

    // ---- 레지스터 스캐너: 맵을 모를 때. 태그 없는 상태를 기준으로 잡고, 태그를 놓았을 때 바뀐 레지스터/비트만 보여 준다.

    [ObservableProperty] private ModbusArea _scanArea = ModbusArea.HoldingRegister;
    [ObservableProperty] private int _scanStart;
    [ObservableProperty] private int _scanCount = 64;
    [ObservableProperty] private bool _scanLive;
    [ObservableProperty] private string _scanButtonText = "실시간 비교 시작";
    [ObservableProperty] private string _scanStatus = "1) 태그를 치운 상태에서 '기준 기록'을 누르고  2) '실시간 비교 시작' 후 태그를 놓으세요.";
    [ObservableProperty] private string _scanStatusColor = "#666666";
    [ObservableProperty] private ScanDiffRow? _selectedScanRow;
    public ObservableCollection<ScanDiffRow> ScanRows { get; } = new();

    private ushort[]? _scanBaseline;
    private ModbusArea _scanBaselineArea;
    private int _scanBaselineStart;
    private Thread? _scanThread;
    private volatile bool _scanStop;

    private bool ScanIsBits => ScanArea is ModbusArea.Coil or ModbusArea.DiscreteInput;

    private string? ValidateScan()
    {
        if (string.IsNullOrWhiteSpace(Host)) return "호스트를 입력하세요.";
        if (Port is < 1 or > 65535) return "포트는 1~65535 입니다.";
        if (ScanStart is < 0 or > 65535) return "스캔 시작 주소는 0~65535 입니다.";
        int max = ScanIsBits ? 4000 : 512;
        if (ScanCount < 1 || ScanCount > max) return $"스캔 개수는 1~{max} 입니다.";
        if (ScanStart + ScanCount > 65536) return "스캔 범위가 주소 상한을 넘습니다.";
        return null;
    }

    /// <summary>영역 종류에 맞게 요청 크기(레지스터 125, 비트 2000)로 나눠 읽어 ushort 배열로 합친다. 비트는 0/1.</summary>
    private ushort[] ReadRange(ModbusTcpClient client, ModbusArea area, int start, int count)
    {
        var result = new ushort[count];
        var unit = (byte)UnitId;
        int chunk = area is ModbusArea.Coil or ModbusArea.DiscreteInput ? 2000 : 125;
        for (int off = 0; off < count; off += chunk)
        {
            var n = (ushort)Math.Min(chunk, count - off);
            var addr = (ushort)(start + off);
            switch (area)
            {
                case ModbusArea.Coil:
                    { var b = client.ReadCoils(unit, addr, n); for (int i = 0; i < n; i++) result[off + i] = b[i] ? (ushort)1 : (ushort)0; break; }
                case ModbusArea.DiscreteInput:
                    { var b = client.ReadDiscreteInputs(unit, addr, n); for (int i = 0; i < n; i++) result[off + i] = b[i] ? (ushort)1 : (ushort)0; break; }
                case ModbusArea.HoldingRegister:
                    Array.Copy(client.ReadHoldingRegisters(unit, addr, n), 0, result, off, n); break;
                default:
                    Array.Copy(client.ReadInputRegisters(unit, addr, n), 0, result, off, n); break;
            }
        }
        return result;
    }

    [RelayCommand]
    private void CaptureBaseline()
    {
        var err = ValidateScan();
        if (err is not null) { ScanStatus = err; ScanStatusColor = "#B03A2E"; return; }
        var area = ScanArea; var start = ScanStart; var count = ScanCount;
        ScanStatus = "기준 읽는 중…";
        ScanStatusColor = "#666666";
        var host = Host.Trim(); var port = Port; var timeout = _timeoutMs;
        Task.Run(() =>
        {
            try
            {
                using var client = new ModbusTcpClient(host, port, timeout);
                client.Connect();
                var data = ReadRange(client, area, start, count);
                _ui.BeginInvoke(() =>
                {
                    _scanBaseline = data;
                    _scanBaselineArea = area;
                    _scanBaselineStart = start;
                    ScanRows.Clear();
                    int nonZero = data.Count(v => v != 0);
                    ScanStatus = $"기준 기록됨: {ModbusRfidMap.AreaKo(area)} {start}~{start + count - 1} ({count}개, 0 아닌 값 {nonZero}개). 이제 '실시간 비교 시작' 후 태그를 놓으세요.";
                    ScanStatusColor = "#2E7D32";
                });
            }
            catch (Exception ex)
            {
                _ui.BeginInvoke(() => { ScanStatus = "기준 읽기 실패: " + ex.Message; ScanStatusColor = "#B03A2E"; });
            }
        });
    }

    [RelayCommand]
    private void ToggleScanLive()
    {
        if (ScanLive) { StopScan(); return; }
        if (_scanBaseline is null) { ScanStatus = "먼저 태그가 없는 상태에서 '기준 기록'을 누르세요."; ScanStatusColor = "#B03A2E"; return; }
        if (ScanArea != _scanBaselineArea || ScanStart != _scanBaselineStart || ScanCount != _scanBaseline.Length)
        {
            ScanStatus = "스캔 영역·범위가 기준과 다릅니다. '기준 기록'을 다시 누르세요.";
            ScanStatusColor = "#B03A2E";
            return;
        }
        _scanStop = false;
        ScanLive = true;
        ScanButtonText = "실시간 비교 중지";
        ScanStatus = "비교 중… 태그를 놓으면 바뀐 항목이 아래에 나타납니다.";
        ScanStatusColor = "#C77700";
        _scanThread = new Thread(ScanLoop) { IsBackground = true, Name = "ModbusScan" };
        _scanThread.Start();
    }

    private void StopScan()
    {
        _scanStop = true;
        _scanThread?.Join(1500);
        _scanThread = null;
        if (ScanLive)
        {
            ScanLive = false;
            ScanButtonText = "실시간 비교 시작";
            if (ScanStatusColor == "#C77700") { ScanStatus = "비교 중지됨. 목록은 마지막 상태입니다."; ScanStatusColor = "#666666"; }
        }
    }

    private void ScanLoop()
    {
        var baseline = _scanBaseline!;
        var area = _scanBaselineArea;
        var start = _scanBaselineStart;
        var host = Host.Trim(); var port = Port; var timeout = _timeoutMs;
        ModbusTcpClient? client = null;
        while (!_scanStop)
        {
            try
            {
                if (client is null || !client.IsConnected)
                {
                    client?.Dispose();
                    client = new ModbusTcpClient(host, port, timeout);
                    client.Connect();
                }
                var now = ReadRange(client, area, start, baseline.Length);
                var rows = new List<ScanDiffRow>();
                bool bits = area is ModbusArea.Coil or ModbusArea.DiscreteInput;
                for (int i = 0; i < now.Length; i++)
                {
                    if (now[i] == baseline[i]) continue;
                    rows.Add(ScanDiffRow.Create(area, start + i, baseline[i], now[i], bits));
                }
                // UID 후보: 연속해서 0 → 0 아닌 값으로 바뀐 레지스터 묶음 (4워드 = 8B 가 전형)
                if (!bits)
                {
                    int run = 0;
                    for (int i = 0; i <= now.Length; i++)
                    {
                        bool appeared = i < now.Length && baseline[i] == 0 && now[i] != 0;
                        if (appeared) { run++; continue; }
                        if (run >= 3)
                        {
                            int first = start + i - run;
                            foreach (var r in rows.Where(r => r.Address >= first && r.Address < first + run))
                                r.Hint = r.Address == first ? $"UID 후보 시작 (연속 {run}워드 = {run * 2}B)" : "UID 후보 (연속)";
                        }
                        run = 0;
                    }
                }
                var summary = rows.Count == 0 ? "바뀐 항목 없음. 태그를 놓아 보세요." : $"바뀐 항목 {rows.Count}개";
                _ui.BeginInvoke(() =>
                {
                    var selected = SelectedScanRow?.Address;
                    ScanRows.Clear();
                    foreach (var r in rows) ScanRows.Add(r);
                    if (selected is not null) SelectedScanRow = ScanRows.FirstOrDefault(r => r.Address == selected);
                    ScanStatus = "비교 중… " + summary;
                    ScanStatusColor = rows.Count == 0 ? "#C77700" : "#2E7D32";
                });
            }
            catch (Exception ex)
            {
                client?.Dispose();
                client = null;
                var msg = ex.Message;
                _ui.BeginInvoke(() => { ScanStatus = "읽기 실패, 재시도: " + msg; ScanStatusColor = "#B03A2E"; });
                for (int i = 0; i < 10 && !_scanStop; i++) Thread.Sleep(100);
                continue;
            }
            for (int i = 0; i < 5 && !_scanStop; i++) Thread.Sleep(100);
        }
        client?.Dispose();
    }

    /// <summary>선택한 스캔 행을 Present 위치로 채운다. 레지스터면 0→1 로 바뀐 첫 비트를 고른다.</summary>
    [RelayCommand]
    private void UseRowAsPresent()
    {
        var r = SelectedScanRow;
        if (r is null) return;
        PresentArea = r.Area;
        PresentAddress = r.Address;
        PresentBit = r.IsBit ? 0 : (r.FirstRisenBit >= 0 ? r.FirstRisenBit : -1);
        ErrorText = "";
        ScanStatus = $"Present 위치를 {ModbusRfidMap.DescribePresent(ToSettings())} 로 채웠습니다.";
        ScanStatusColor = "#2E7D32";
    }

    /// <summary>선택한 스캔 행을 UID 시작 주소로 채운다. 레지스터 영역만.</summary>
    [RelayCommand]
    private void UseRowAsUid()
    {
        var r = SelectedScanRow;
        if (r is null) return;
        if (r.IsBit) { ScanStatus = "UID 는 레지스터 영역이어야 합니다. Holding/Input Register 로 스캔하세요."; ScanStatusColor = "#B03A2E"; return; }
        UidArea = r.Area;
        UidAddress = r.Address;
        ErrorText = "";
        ScanStatus = $"UID 위치를 {ModbusRfidMap.DescribeUid(ToSettings())} 로 채웠습니다. 연결 테스트에서 E0 위치를 보고 스왑/역순을 맞추세요.";
        ScanStatusColor = "#2E7D32";
    }

    public void Dispose()
    {
        StopTest();
        StopScan();
    }
}

/// <summary>스캐너 결과 한 줄: 기준과 달라진 레지스터 또는 비트.</summary>
public sealed partial class ScanDiffRow : ObservableObject
{
    public ModbusArea Area { get; init; }
    public int Address { get; init; }
    public ushort Before { get; init; }
    public ushort After { get; init; }
    public bool IsBit { get; init; }
    /// <summary>0→1 로 바뀐 비트 중 가장 낮은 것. 없으면 -1.</summary>
    public int FirstRisenBit { get; init; } = -1;

    public string AddressText => IsBit ? Address.ToString() : $"{Address} (0x{Address:X4})";
    public string BeforeText => IsBit ? Before.ToString() : $"0x{Before:X4}";
    public string AfterText => IsBit ? After.ToString() : $"0x{After:X4}";
    public string ChangedBitsText { get; init; } = "";
    [ObservableProperty] private string _hint = "";

    public static ScanDiffRow Create(ModbusArea area, int address, ushort before, ushort after, bool isBit)
    {
        int diff = before ^ after;
        int risen = after & ~before;
        int firstRisen = -1;
        var bitsChanged = new List<string>();
        for (int b = 15; b >= 0; b--)
        {
            if ((diff & (1 << b)) == 0) continue;
            bool up = (risen & (1 << b)) != 0;
            bitsChanged.Add($"b{b}{(up ? "↑" : "↓")}");
            if (up) firstRisen = b;
        }
        string hint = isBit
            ? (after != 0 && before == 0 ? "Present 후보 (0→1)" : "")
            : (bitsChanged.Count == 1 && firstRisen >= 0 ? $"Present 후보 (비트 {firstRisen} 0→1)" : "");
        return new ScanDiffRow
        {
            Area = area, Address = address, Before = before, After = after, IsBit = isBit,
            FirstRisenBit = firstRisen,
            ChangedBitsText = isBit ? "" : string.Join(" ", bitsChanged),
            Hint = hint
        };
    }
}
