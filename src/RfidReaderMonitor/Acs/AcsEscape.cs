using System.Text;

namespace RfidReaderMonitor.Acs;

/// <summary>
/// ACS ACR1252U/ACR1552U 계열 에스케이프 명령.
/// 명령: E0 00 00 [CMD] [LEN] [DATA]. 응답: E1 00 00 00 [LEN] [DATA].
/// 드라이버 레지스트리에 EscapeCommandEnable=1 이 있어야 통과한다.
/// </summary>
public static class AcsEscape
{
    public static byte[] GetFirmwareVersion() => new byte[] { 0xE0, 0x00, 0x00, 0x18, 0x00 };
    public static byte[] GetSerialNumber() => new byte[] { 0xE0, 0x00, 0x00, 0x33, 0x00 };

    public static byte[] ReadLed() => new byte[] { 0xE0, 0x00, 0x00, 0x29, 0x00 };
    public static byte[] SetLed(byte state) => new byte[] { 0xE0, 0x00, 0x00, 0x29, 0x01, state };

    /// <summary>부저. duration 단위 10ms.</summary>
    public static byte[] Buzzer(byte durationUnits10Ms) => new byte[] { 0xE0, 0x00, 0x00, 0x28, 0x01, durationUnits10Ms };

    public static byte[] ReadAutoPolling() => new byte[] { 0xE0, 0x00, 0x00, 0x23, 0x00 };
    public static byte[] SetAutoPolling(byte value) => new byte[] { 0xE0, 0x00, 0x00, 0x23, 0x01, value };

    public static byte[] ReadLedBuzzerBehaviour() => new byte[] { 0xE0, 0x00, 0x00, 0x21, 0x00 };
    public static byte[] SetLedBuzzerBehaviour(byte value) => new byte[] { 0xE0, 0x00, 0x00, 0x21, 0x01, value };

    public static byte[] ReadPiccOperatingParameter() => new byte[] { 0xE0, 0x00, 0x00, 0x20, 0x00 };
    public static byte[] SetPiccOperatingParameter(byte value) => new byte[] { 0xE0, 0x00, 0x00, 0x20, 0x01, value };

    /// <summary>응답에서 데이터 부분만 추출. 형식이 다르면 원본 반환.</summary>
    public static byte[] ExtractData(byte[] response)
    {
        if (response.Length >= 5 && response[0] == 0xE1)
        {
            int len = response[4];
            if (5 + len <= response.Length) return response.AsSpan(5, len).ToArray();
        }
        return response;
    }

    public static string ExtractAscii(byte[] response)
        => Encoding.ASCII.GetString(ExtractData(response)).TrimEnd('\0', ' ');

    // --- Auto PICC polling 비트 (ACR1252U 매뉴얼 기준, ACR1552U 동일 계열) ---
    public const byte Poll_Enable = 0x01;
    public const byte Poll_AntennaOffIfNoPicc = 0x02;
    public const byte Poll_AntennaOffIfPiccInactive = 0x04;
    public const byte Poll_ActivatePiccOnDetect = 0x08;
    public const byte Poll_IntervalMask = 0x30;
    public const byte Poll_TestMode = 0x40;
    public const byte Poll_EnforceIso14443A4 = 0x80;

    public static int PollIntervalMs(byte v) => ((v & Poll_IntervalMask) >> 4) switch
    {
        0 => 250, 1 => 500, 2 => 1000, _ => 2500
    };

    public static byte WithPollInterval(byte v, int ms)
    {
        int code = ms switch { <= 250 => 0, <= 500 => 1, <= 1000 => 2, _ => 3 };
        return (byte)((v & ~Poll_IntervalMask) | (code << 4));
    }

    public static string DescribeAutoPolling(byte v)
    {
        var parts = new List<string>();
        parts.Add((v & Poll_Enable) != 0 ? "자동 폴링 ON" : "자동 폴링 OFF");
        parts.Add($"주기 {PollIntervalMs(v)}ms");
        if ((v & Poll_AntennaOffIfNoPicc) != 0) parts.Add("태그 없으면 안테나 OFF");
        if ((v & Poll_AntennaOffIfPiccInactive) != 0) parts.Add("태그 비활성 시 안테나 OFF");
        if ((v & Poll_ActivatePiccOnDetect) != 0) parts.Add("감지 시 PICC 활성화");
        if ((v & Poll_TestMode) != 0) parts.Add("테스트 모드");
        if ((v & Poll_EnforceIso14443A4) != 0) parts.Add("14443A-4 강제");
        return $"0x{v:X2}: " + string.Join(", ", parts);
    }

    // --- PICC operating parameter 비트 ---
    public const byte Picc_Iso14443A = 0x01;
    public const byte Picc_Iso14443B = 0x02;
    public const byte Picc_FeliCa212 = 0x04;
    public const byte Picc_FeliCa424 = 0x08;
    public const byte Picc_Topaz = 0x10;
    public const byte Picc_Iso15693 = 0x20;   // ACR1552U 계열 추정값. 실물로 확인 필요.

    public static string DescribePiccParameter(byte v)
    {
        var parts = new List<string>();
        if ((v & Picc_Iso14443A) != 0) parts.Add("14443A");
        if ((v & Picc_Iso14443B) != 0) parts.Add("14443B");
        if ((v & Picc_FeliCa212) != 0) parts.Add("FeliCa212");
        if ((v & Picc_FeliCa424) != 0) parts.Add("FeliCa424");
        if ((v & Picc_Topaz) != 0) parts.Add("Topaz");
        if ((v & Picc_Iso15693) != 0) parts.Add("15693(추정)");
        if ((v & 0xC0) != 0) parts.Add($"기타 0x{v & 0xC0:X2}");
        return $"0x{v:X2}: " + (parts.Count == 0 ? "없음" : string.Join(", ", parts));
    }

    // --- LED/Buzzer default behaviour 비트 (ACR1252U) ---
    public static string DescribeLedBuzzerBehaviour(byte v)
    {
        var parts = new List<string>();
        if ((v & 0x01) != 0) parts.Add("ICC 활성 상태 LED");
        if ((v & 0x02) != 0) parts.Add("PICC 폴링 상태 LED");
        if ((v & 0x04) != 0) parts.Add("PICC 활성 상태 LED");
        if ((v & 0x08) != 0) parts.Add("카드 삽입/제거 이벤트 부저");
        if ((v & 0x10) != 0) parts.Add("카드 동작 깜빡임 LED");
        if ((v & 0x20) != 0) parts.Add("PICC 활성 시 부저");
        if ((v & 0xC0) != 0) parts.Add($"기타 0x{v & 0xC0:X2}");
        return $"0x{v:X2}: " + (parts.Count == 0 ? "없음" : string.Join(", ", parts));
    }
}
