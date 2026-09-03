namespace RfidReaderMonitor.Core;

public enum CardFamily
{
    Unknown,
    Iso14443A,
    Iso14443B,
    Iso15693,
    FeliCa,
    Topaz,
    /// <summary>ISO 14443-4 (T=CL) 프로토콜 카드. DESFire, 스마트카드 등.</summary>
    Iso14443_4,
    Contact
}

public sealed record CardTech(CardFamily Family, string Standard, string CardName)
{
    public static readonly CardTech Unknown = new(CardFamily.Unknown, "?", "?");

    public override string ToString()
        => Family == CardFamily.Unknown ? "알 수 없음" : $"{Standard} / {CardName}";
}

/// <summary>PC/SC Part 3 비접촉 저장형 카드 ATR 해석.</summary>
public static class AtrParser
{
    // 3B 8F 80 01 80 4F 0C A0 00 00 03 06 [SS] [NN NN] 00 00 00 00 [TCK]
    private static readonly byte[] StoragePrefix = { 0x3B, 0x8F, 0x80, 0x01, 0x80, 0x4F, 0x0C, 0xA0, 0x00, 0x00, 0x03, 0x06 };

    public static CardTech Parse(byte[]? atr)
    {
        if (atr is null || atr.Length < 2) return CardTech.Unknown;

        if (atr.Length >= 20 && atr.AsSpan(0, StoragePrefix.Length).SequenceEqual(StoragePrefix))
        {
            byte ss = atr[12];
            int nn = (atr[13] << 8) | atr[14];
            var (family, standard) = Standard(ss);
            return new CardTech(family, standard, CardName(nn));
        }

        // T=CL 카드 (ISO 14443-4). ATR: 3B 8x 80 01 [historical] TCK
        if (atr[0] == 0x3B && (atr[1] & 0xF0) == 0x80 && atr.Length >= 4 && atr[2] == 0x80 && atr[3] == 0x01)
        {
            var hist = atr.AsSpan(4, Math.Max(0, atr.Length - 5));
            var name = hist.Length == 0 ? "ISO 14443-4 카드" : $"ISO 14443-4 카드 (hist {Hex.Of(hist)})";
            return new CardTech(CardFamily.Iso14443_4, "ISO 14443-4", name);
        }

        if (atr[0] == 0x3B || atr[0] == 0x3F)
            return new CardTech(CardFamily.Contact, "ISO 7816", "접촉식 카드");

        return CardTech.Unknown;
    }

    private static (CardFamily, string) Standard(byte ss) => ss switch
    {
        0x01 => (CardFamily.Iso14443A, "ISO 14443A-1"),
        0x02 => (CardFamily.Iso14443A, "ISO 14443A-2"),
        0x03 => (CardFamily.Iso14443A, "ISO 14443A-3"),
        0x05 => (CardFamily.Iso14443B, "ISO 14443B-1"),
        0x06 => (CardFamily.Iso14443B, "ISO 14443B-2"),
        0x07 => (CardFamily.Iso14443B, "ISO 14443B-3"),
        0x09 => (CardFamily.Iso15693, "ISO 15693-1"),
        0x0A => (CardFamily.Iso15693, "ISO 15693-2"),
        0x0B => (CardFamily.Iso15693, "ISO 15693-3"),
        0x0C => (CardFamily.Iso15693, "ISO 15693-4"),
        0x0D => (CardFamily.Contact, "ISO 7816-10 I2C"),
        0x0E => (CardFamily.Contact, "ISO 7816-10 Ext I2C"),
        0x0F => (CardFamily.Contact, "ISO 7816-10 2WBP"),
        0x10 => (CardFamily.Contact, "ISO 7816-10 3WBP"),
        0x11 => (CardFamily.FeliCa, "FeliCa"),
        0x40 => (CardFamily.Iso14443A, "Low frequency contactless"),
        _ => (CardFamily.Unknown, $"SS=0x{ss:X2}")
    };

    private static string CardName(int nn) => nn switch
    {
        0x0001 => "MIFARE Classic 1K",
        0x0002 => "MIFARE Classic 4K",
        0x0003 => "MIFARE Ultralight",
        0x0004 => "SLE55R_XXXX",
        0x0006 => "SR176",
        0x0007 => "SRI X4K",
        0x0008 => "AT88RF020",
        0x0009 => "AT88SC0204CRF",
        0x000A => "AT88SC0808CRF",
        0x000B => "AT88SC1616CRF",
        0x000C => "AT88SC3216CRF",
        0x000D => "AT88SC6416CRF",
        0x000E => "SRF55V10P",
        0x000F => "SRF55V02P",
        0x0010 => "SRF55V10S",
        0x0011 => "SRF55V02S",
        0x0012 => "TAG-IT",
        0x0013 => "LRI512",
        0x0014 => "ICODE SLI",
        0x0015 => "TEMPSENS",
        0x0016 => "I.CODE1",
        0x0017 => "PicoPass 2K",
        0x0018 => "PicoPass 2KS",
        0x0019 => "PicoPass 16K",
        0x001A => "PicoPass 16Ks",
        0x001B => "PicoPass 16K(8x2)",
        0x001C => "PicoPass 16KS(8x2)",
        0x001D => "PicoPass 32KS(16+16)",
        0x001E => "PicoPass 32KS(16+8x2)",
        0x001F => "PicoPass 32KS(8x2+16)",
        0x0020 => "PicoPass 32KS(8x2+8x2)",
        0x0021 => "LRI64",
        0x0022 => "I.CODE UID",
        0x0023 => "I.CODE EPC",
        0x0024 => "LRI12",
        0x0025 => "LRI128",
        0x0026 => "MIFARE Mini",
        0x0027 => "my-d move (SLE 66R01P)",
        0x0028 => "my-d NFC (SLE 66RxxP)",
        0x0029 => "my-d proximity 2 (SLE 66RxxS)",
        0x002A => "my-d proximity enhanced (SLE 55RxxE)",
        0x002B => "my-d light (SRF 55V01P)",
        0x002C => "PJM Stack Tag (SRF 66V10ST)",
        0x002D => "PJM Item Tag (SRF 66V10IT)",
        0x002E => "PJM Light (SRF 66V01ST)",
        0x002F => "Jewel Tag",
        0x0030 => "Topaz NFC Tag",
        0x0031 => "AT88SC0104CRF",
        0x0032 => "AT88SC0404CRF",
        0x0033 => "AT88RF01C",
        0x0034 => "AT88RF04C",
        0x0035 => "i-Code SL2",
        0x0036 => "MIFARE Plus SL1 2K",
        0x0037 => "MIFARE Plus SL1 4K",
        0x0038 => "MIFARE Plus SL2 2K",
        0x0039 => "MIFARE Plus SL2 4K",
        0x003A => "MIFARE Ultralight C",
        0x003B => "FeliCa",
        0x003C => "Melexis Sensor Tag (MLX90129)",
        0x003D => "MIFARE Ultralight EV1",
        _ => $"NN=0x{nn:X4}"
    };
}

public static class Hex
{
    public static string Of(ReadOnlySpan<byte> bytes, string sep = " ")
    {
        if (bytes.Length == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(sep);
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public static string Of(byte[]? bytes, string sep = " ") => bytes is null ? string.Empty : Of(bytes.AsSpan(), sep);

    public static byte[] Parse(string text)
    {
        var clean = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (clean.Length % 2 != 0) throw new FormatException("16진수 자릿수가 짝수가 아닙니다.");
        var out_ = new byte[clean.Length / 2];
        for (int i = 0; i < out_.Length; i++) out_[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return out_;
    }
}
