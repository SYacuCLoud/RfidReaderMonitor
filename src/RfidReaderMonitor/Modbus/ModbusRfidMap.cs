using RfidReaderMonitor.Core;
using RfidReaderMonitor.Settings;

namespace RfidReaderMonitor.Modbus;

/// <summary>
/// 리더 설정(레지스터 맵)을 실제 읽기·해석으로 옮기는 공용 로직.
/// 폴링 프로바이더, 리더 도구 탭, 등록 대화상자의 연결 테스트가 모두 이것을 쓴다.
/// </summary>
public static class ModbusRfidMap
{
    public static (bool Present, ushort Raw) ReadPresent(ModbusTcpClient client, ModbusReaderSettings c)
    {
        var unit = (byte)c.UnitId;
        var addr = (ushort)c.PresentAddress;
        switch (c.PresentArea)
        {
            case ModbusArea.Coil:
                { var b = client.ReadCoils(unit, addr, 1)[0]; return (b, b ? (ushort)1 : (ushort)0); }
            case ModbusArea.DiscreteInput:
                { var b = client.ReadDiscreteInputs(unit, addr, 1)[0]; return (b, b ? (ushort)1 : (ushort)0); }
            default:
                {
                    var v = c.PresentArea == ModbusArea.HoldingRegister
                        ? client.ReadHoldingRegisters(unit, addr, 1)[0]
                        : client.ReadInputRegisters(unit, addr, 1)[0];
                    bool present = c.PresentBit < 0 ? v != 0 : (v & (1 << Math.Min(15, c.PresentBit))) != 0;
                    return (present, v);
                }
        }
    }

    public static ushort[] ReadUidWords(ModbusTcpClient client, ModbusReaderSettings c)
    {
        int bytes = Math.Clamp(c.UidBytes, 1, 32);
        var words = (ushort)((bytes + 1) / 2);
        var unit = (byte)c.UnitId;
        var addr = (ushort)c.UidAddress;
        return c.UidArea switch
        {
            ModbusArea.HoldingRegister => client.ReadHoldingRegisters(unit, addr, words),
            ModbusArea.InputRegister => client.ReadInputRegisters(unit, addr, words),
            _ => throw new InvalidOperationException("UID 영역은 레지스터여야 합니다.")
        };
    }

    /// <summary>워드 배열 → UID 바이트. 워드 역순을 먼저, 워드 안 바이트 스왑을 다음에 적용한다. 전부 0 이면 빈 배열.</summary>
    public static byte[] Decode(ModbusReaderSettings c, ushort[] regsIn)
    {
        int bytes = Math.Clamp(c.UidBytes, 1, 32);
        var regs = (ushort[])regsIn.Clone();
        if (c.UidReverseWords) Array.Reverse(regs);
        var buf = new byte[regs.Length * 2];
        for (int i = 0; i < regs.Length; i++)
        {
            byte hi = (byte)(regs[i] >> 8), lo = (byte)regs[i];
            if (c.UidSwapBytes) (hi, lo) = (lo, hi);
            buf[i * 2] = hi;
            buf[i * 2 + 1] = lo;
        }
        var uid = buf.AsSpan(0, Math.Min(bytes, buf.Length)).ToArray();
        return uid.All(b => b == 0) ? Array.Empty<byte>() : uid;
    }

    public static CardTech TechOf(CardFamily family) => family switch
    {
        CardFamily.Iso15693 => new CardTech(family, "ISO 15693", "산업용 HF 태그"),
        CardFamily.Iso14443A => new CardTech(family, "ISO 14443A", "산업용 HF 태그"),
        CardFamily.Iso14443B => new CardTech(family, "ISO 14443B", "산업용 HF 태그"),
        CardFamily.Unknown => new CardTech(family, "?", "Modbus 리더"),
        _ => new CardTech(family, family.ToString(), "산업용 태그")
    };

    /// <summary>ISO 15693 UID 는 E0 로 시작한다. 그 사실로 바이트 순서 설정을 힌트한다.</summary>
    public static string UidOrderHint(byte[] uid)
    {
        if (uid.Length != 8) return "";
        if (uid[0] == 0xE0) return "E0 로 시작 → ISO 15693 UID 순서가 맞습니다.";
        if (uid[7] == 0xE0) return "마지막 바이트가 E0 → '워드 역순'과 '바이트 스왑'을 함께 바꿔 보세요.";
        if (uid[1] == 0xE0) return "두 번째 바이트가 E0 → '바이트 스왑'을 바꿔 보세요.";
        if (uid[6] == 0xE0) return "일곱 번째 바이트가 E0 → '워드 역순'을 바꿔 보세요.";
        return "E0 가 첫 바이트가 아닙니다. ISO 15693 이 아니거나 UID 시작 주소가 다를 수 있습니다.";
    }

    /// <summary>16비트를 4자리씩 띄어 표시. 비트 15 가 왼쪽.</summary>
    public static string Bits16(ushort v)
        => Convert.ToString(v, 2).PadLeft(16, '0').Insert(12, " ").Insert(8, " ").Insert(4, " ");

    /// <summary>입력 검증. 문제가 없으면 null.</summary>
    public static string? Validate(ModbusReaderSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Host)) return "호스트를 입력하세요.";
        if (s.Port is < 1 or > 65535) return "포트는 1~65535 입니다.";
        if (s.UnitId is < 0 or > 255) return "Unit ID 는 0~255 입니다.";
        if (s.PollMs < 20) return "폴링 주기는 20ms 이상이어야 합니다.";
        if (s.PresentAddress is < 0 or > 65535 || s.UidAddress is < 0 or > 65535) return "주소는 0~65535 입니다.";
        if (s.PresentArea is ModbusArea.HoldingRegister or ModbusArea.InputRegister && s.PresentBit is < -1 or > 15)
            return "Present 비트는 0~15 (또는 -1 = 0 이 아니면 있음) 입니다.";
        if (s.UidArea is not (ModbusArea.HoldingRegister or ModbusArea.InputRegister)) return "UID 영역은 레지스터여야 합니다.";
        if (s.UidBytes is < 1 or > 32) return "UID 길이는 1~32 바이트입니다.";
        return null;
    }

    public static string DescribePresent(ModbusReaderSettings c) => c.PresentArea switch
    {
        ModbusArea.Coil => $"Coil {c.PresentAddress}",
        ModbusArea.DiscreteInput => $"Discrete Input {c.PresentAddress}",
        _ => $"{AreaKo(c.PresentArea)} {c.PresentAddress}" + (c.PresentBit < 0 ? " (0 아니면 있음)" : $" 비트 {c.PresentBit}")
    };

    public static string DescribeUid(ModbusReaderSettings c)
        => $"{AreaKo(c.UidArea)} {c.UidAddress}~{c.UidAddress + (c.UidBytes + 1) / 2 - 1} ({c.UidBytes}B"
           + (c.UidSwapBytes ? ", 바이트 스왑" : "") + (c.UidReverseWords ? ", 워드 역순" : "") + ")";

    public static string AreaKo(ModbusArea a) => a switch
    {
        ModbusArea.Coil => "Coil",
        ModbusArea.DiscreteInput => "Discrete Input",
        ModbusArea.HoldingRegister => "Holding Register",
        ModbusArea.InputRegister => "Input Register",
        _ => a.ToString()
    };
}
