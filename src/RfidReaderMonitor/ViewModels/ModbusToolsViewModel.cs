using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfidReaderMonitor.Core;
using RfidReaderMonitor.Modbus;

namespace RfidReaderMonitor.ViewModels;

/// <summary>
/// 리더 도구 탭 (Modbus TCP 리더). 선택 리더의 레지스터 원값을 주기적으로 읽어 보여 준다.
/// 현장에서 Present 비트 위치와 UID 바이트 순서를 맞출 때 쓴다.
/// </summary>
public sealed partial class ModbusToolsViewModel : ObservableObject
{
    private readonly ModbusReaderProvider _modbus;
    private readonly Action<string> _editInSettings;
    private readonly Action<string> _status;
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private ReaderItemViewModel? _reader;

    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _presentMap = "";
    [ObservableProperty] private string _uidMap = "";
    [ObservableProperty] private string _pollText = "";

    [ObservableProperty] private string _linkText = "";
    [ObservableProperty] private string _linkColor = "#888888";
    [ObservableProperty] private string _lastPollText = "";
    [ObservableProperty] private string _responseText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _lastError = "";

    [ObservableProperty] private string _presentRawText = "";
    [ObservableProperty] private string _presentBitsText = "";
    [ObservableProperty] private bool _presentNow;
    [ObservableProperty] private string _uidWordsText = "";
    [ObservableProperty] private string _uidBytesText = "";
    [ObservableProperty] private string _uidHint = "";

    public ModbusToolsViewModel(ModbusReaderProvider modbus, Dispatcher ui, Action<string> editInSettings, Action<string> status)
    {
        _modbus = modbus;
        _editInSettings = editInSettings;
        _status = status;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Tick(), ui);
        _timer.Stop();
    }

    partial void OnReaderChanged(ReaderItemViewModel? value)
    {
        if (value is null) { _timer.Stop(); return; }
        LoadConfig(value.Name);
        Tick();
        _timer.Start();
    }

    /// <summary>탭이 안 보일 때 폴링을 멈추고 싶으면 호출. 지금은 리더가 바뀔 때만 켜고 끈다.</summary>
    public void SetActive(bool active)
    {
        if (active && Reader is not null) _timer.Start(); else _timer.Stop();
    }

    private void LoadConfig(string name)
    {
        var c = _modbus.ConfigOf(name);
        if (c is null)
        {
            Endpoint = PresentMap = UidMap = PollText = "";
            return;
        }
        Endpoint = $"{c.Host}:{c.Port}  Unit {c.UnitId}";
        PresentMap = ModbusRfidMap.DescribePresent(c);
        UidMap = ModbusRfidMap.DescribeUid(c);
        PollText = $"{c.PollMs} ms";
    }

    private void Tick()
    {
        var r = Reader;
        if (r is null) return;
        var d = _modbus.Diagnostics(r.Name);
        if (d is null)
        {
            LinkText = "실행 중이 아님 (설정에서 꺼져 있거나 적용 전)";
            LinkColor = "#888888";
            return;
        }

        LinkText = d.Connected ? "● 연결됨" : "○ 끊김, 재접속 대기";
        LinkColor = d.Connected ? "#2E7D32" : "#C77700";
        LastPollText = d.LastPoll is null ? "-" : $"{d.LastPoll:HH:mm:ss.fff}";
        ResponseText = d.LastPoll is null ? "-" : $"{d.LastResponseMs:F1} ms";
        CountText = $"폴링 {d.Requests:N0}회 · 실패 {d.Failures:N0}회";
        LastError = d.LastError;

        PresentNow = d.Present;
        PresentRawText = $"0x{d.PresentRaw:X4}  ({d.PresentRaw})";
        PresentBitsText = ModbusRfidMap.Bits16(d.PresentRaw);

        if (d.UidWords.Length == 0)
        {
            UidWordsText = d.Present ? "(읽지 못함)" : "(태그 없음)";
            UidBytesText = "";
            UidHint = "";
        }
        else
        {
            UidWordsText = string.Join(" ", d.UidWords.Select(w => w.ToString("X4")));
            UidBytesText = d.Uid.Length == 0 ? "모두 0" : Hex.Of(d.Uid);
            UidHint = ModbusRfidMap.UidOrderHint(d.Uid);
        }
    }

    [RelayCommand]
    private void Reconnect()
    {
        if (Reader is null) return;
        _modbus.Reconnect(Reader.Name);
        _status($"{Reader.DisplayName} 재접속 요청");
    }

    [RelayCommand]
    private void EditInSettings()
    {
        if (Reader is null) return;
        _editInSettings(Reader.Name);
    }

    [RelayCommand]
    private void RefreshConfig()
    {
        if (Reader is null) return;
        LoadConfig(Reader.Name);
        Tick();
    }
}
