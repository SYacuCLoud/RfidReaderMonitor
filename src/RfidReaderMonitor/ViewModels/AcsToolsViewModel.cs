using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfidReaderMonitor.Acs;
using RfidReaderMonitor.Core;
using RfidReaderMonitor.PcSc;
using RfidReaderMonitor.Readers;
using RfidReaderMonitor.Sys;

namespace RfidReaderMonitor.ViewModels;

/// <summary>
/// ACS 리더 전용 도구 (에스케이프 명령). 다른 제조사 도구를 추가할 때는
/// 같은 모양의 뷰모델 + UserControl 을 하나 더 만들고 MainViewModel.VendorTools 에서 골라 준다.
/// </summary>
public sealed partial class AcsToolsViewModel : ObservableObject
{
    private readonly IRfidReaderProvider _provider;
    private readonly Action<string> _status;

    public static bool Supports(ReaderItemViewModel? reader)
        => reader is not null && reader.Vendor.StartsWith("ACS", StringComparison.OrdinalIgnoreCase);

    public AcsToolsViewModel(IRfidReaderProvider provider, Action<string> status)
    {
        _provider = provider;
        _status = status;
    }

    /// <summary>대상 리더. MainViewModel 이 선택 변경 시 넣어 준다.</summary>
    [ObservableProperty] private ReaderItemViewModel? _reader;

    // 폴링 설정 편집
    [ObservableProperty] private bool _pollEnable = true;
    [ObservableProperty] private int _pollIntervalMs = 250;
    [ObservableProperty] private bool _pollAntennaOffIfNoPicc;
    [ObservableProperty] private bool _pollActivateOnDetect = true;

    // 사용자 에스케이프
    [ObservableProperty] private string _customEscapeHex = "E0 00 00 18 00";
    [ObservableProperty] private string _customEscapeResponse = "";

    private async Task<byte[]?> EscapeAsync(ReaderItemViewModel vm, byte[] cmd, string what)
    {
        try
        {
            var resp = await Task.Run(() => _provider.Escape(vm.Name, cmd));
            vm.LastError = "";
            return resp;
        }
        catch (PcscException ex) when (ex.Code is PcscError.E_NOT_TRANSACTED or PcscError.E_UNSUPPORTED_FEATURE or PcscError.E_INVALID_PARAMETER)
        {
            vm.LastError = $"{what}: 에스케이프 명령 거부됨. EscapeCommandEnable 설정과 리더 재연결을 확인하세요. ({PcscError.Name(ex.Code)})";
            _status(vm.LastError);
            return null;
        }
        catch (Exception ex)
        {
            vm.LastError = $"{what}: {ex.Message}";
            _status(vm.LastError);
            return null;
        }
    }

    /// <summary>식별 직후 자동 호출용. 펌웨어 문자열만 채운다.</summary>
    public async Task ReadFirmwareAsync(ReaderItemViewModel vm)
    {
        var resp = await EscapeAsync(vm, AcsEscape.GetFirmwareVersion(), "펌웨어 조회");
        if (resp is not null) vm.Firmware = AcsEscape.ExtractAscii(resp);
    }

    [RelayCommand]
    private async Task ReadFirmware()
    {
        if (Reader is null) return;
        var vm = Reader;
        await ReadFirmwareAsync(vm);
        var sn = await EscapeAsync(vm, AcsEscape.GetSerialNumber(), "S/N 조회");
        if (sn is not null)
        {
            var s = AcsEscape.ExtractAscii(sn);
            if (!string.IsNullOrWhiteSpace(s) && string.IsNullOrWhiteSpace(vm.Serial))
            {
                vm.Serial = s;
                vm.SerialSource = "ACS 에스케이프";
            }
            _status($"펌웨어 {vm.Firmware}, S/N {s}");
        }
    }

    [RelayCommand]
    private async Task Beep()
    {
        if (Reader is null) return;
        if (await EscapeAsync(Reader, AcsEscape.Buzzer(0x0A), "부저") is not null) _status("부저 100ms");
    }

    [RelayCommand]
    private async Task LedOn()
    {
        if (Reader is null) return;
        if (await EscapeAsync(Reader, AcsEscape.SetLed(0x0F), "LED") is not null) _status("LED 전체 ON");
    }

    [RelayCommand]
    private async Task LedOff()
    {
        if (Reader is null) return;
        if (await EscapeAsync(Reader, AcsEscape.SetLed(0x00), "LED") is not null) _status("LED OFF");
    }

    [RelayCommand]
    private async Task ReadPolling()
    {
        if (Reader is null) return;
        var vm = Reader;
        var resp = await EscapeAsync(vm, AcsEscape.ReadAutoPolling(), "폴링 설정 조회");
        if (resp is not null)
        {
            var d = AcsEscape.ExtractData(resp);
            if (d.Length >= 1)
            {
                vm.PollingValue = d[0];
                vm.PollingText = AcsEscape.DescribeAutoPolling(d[0]);
                PollEnable = (d[0] & AcsEscape.Poll_Enable) != 0;
                PollIntervalMs = AcsEscape.PollIntervalMs(d[0]);
                PollAntennaOffIfNoPicc = (d[0] & AcsEscape.Poll_AntennaOffIfNoPicc) != 0;
                PollActivateOnDetect = (d[0] & AcsEscape.Poll_ActivatePiccOnDetect) != 0;
            }
            else vm.PollingText = "응답 " + Hex.Of(resp);
        }
        var p = await EscapeAsync(vm, AcsEscape.ReadPiccOperatingParameter(), "PICC 파라미터 조회");
        if (p is not null)
        {
            var d = AcsEscape.ExtractData(p);
            vm.PiccParamText = d.Length >= 1 ? AcsEscape.DescribePiccParameter(d[0]) : "응답 " + Hex.Of(p);
        }
        var b = await EscapeAsync(vm, AcsEscape.ReadLedBuzzerBehaviour(), "LED/부저 기본동작 조회");
        if (b is not null)
        {
            var d = AcsEscape.ExtractData(b);
            vm.LedBuzzerText = d.Length >= 1 ? AcsEscape.DescribeLedBuzzerBehaviour(d[0]) : "응답 " + Hex.Of(b);
        }
    }

    [RelayCommand]
    private async Task ApplyPolling()
    {
        if (Reader is null) return;
        var vm = Reader;
        byte v = vm.PollingValue;
        v = (byte)(PollEnable ? v | AcsEscape.Poll_Enable : v & ~AcsEscape.Poll_Enable);
        v = (byte)(PollAntennaOffIfNoPicc ? v | AcsEscape.Poll_AntennaOffIfNoPicc : v & ~AcsEscape.Poll_AntennaOffIfNoPicc);
        v = (byte)(PollActivateOnDetect ? v | AcsEscape.Poll_ActivatePiccOnDetect : v & ~AcsEscape.Poll_ActivatePiccOnDetect);
        v = AcsEscape.WithPollInterval(v, PollIntervalMs);
        var resp = await EscapeAsync(vm, AcsEscape.SetAutoPolling(v), "폴링 설정 적용");
        if (resp is not null)
        {
            var d = AcsEscape.ExtractData(resp);
            if (d.Length >= 1)
            {
                vm.PollingValue = d[0];
                vm.PollingText = AcsEscape.DescribeAutoPolling(d[0]);
            }
            _status("폴링 설정 적용: " + vm.PollingText);
        }
    }

    [RelayCommand]
    private async Task SendCustomEscape()
    {
        if (Reader is null) return;
        try
        {
            var cmd = Hex.Parse(CustomEscapeHex);
            var resp = await EscapeAsync(Reader, cmd, "사용자 에스케이프");
            CustomEscapeResponse = resp is null ? Reader.LastError : $"{Hex.Of(resp)}   |   \"{AcsEscape.ExtractAscii(resp)}\"";
        }
        catch (FormatException ex)
        {
            CustomEscapeResponse = ex.Message;
        }
    }

    [RelayCommand]
    private void EnableEscape()
    {
        if (Reader is null || string.IsNullOrEmpty(Reader.DeviceInstanceId))
        {
            _status("장치 인스턴스를 찾지 못해 레지스트리 경로를 만들 수 없습니다.");
            return;
        }
        var vm = Reader;
        var ok = SystemChecks.ApplyEscapeEnable(vm.DeviceInstanceId);
        vm.EscapeEnabled = UsbDeviceMapper.ReadEscapeEnabled(vm.DeviceInstanceId);
        _status(ok
            ? "EscapeCommandEnable=1 기록됨. 리더를 뽑았다 다시 꽂으면 적용됩니다."
            : "적용 취소 또는 실패. 관리자 권한으로 다음 명령을 직접 실행하세요: " + SystemChecks.EscapeEnableCommand(vm.DeviceInstanceId));
    }
}
