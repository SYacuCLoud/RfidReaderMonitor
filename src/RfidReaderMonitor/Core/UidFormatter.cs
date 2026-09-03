namespace RfidReaderMonitor.Core;

public static class UidFormatter
{
    /// <summary>
    /// UID를 표시 문자열로 정규화한다.
    /// ISO 15693은 리더가 LSB 우선(역순)으로 돌려주는 경우가 있어 옵션으로 뒤집는다.
    /// </summary>
    public static string Format(byte[] uid, CardFamily family, bool reverseIso15693)
    {
        if (uid.Length == 0) return "?";
        var bytes = uid;
        if (reverseIso15693 && family == CardFamily.Iso15693)
        {
            bytes = (byte[])uid.Clone();
            Array.Reverse(bytes);
        }
        return Hex.Of(bytes);
    }

    public static string DescribeLength(byte[] uid, CardFamily family) => (family, uid.Length) switch
    {
        (CardFamily.Iso14443A, 4) => "4B single",
        (CardFamily.Iso14443A, 7) => "7B double",
        (CardFamily.Iso14443A, 10) => "10B triple",
        (CardFamily.Iso15693, 8) => "8B (E0..)",
        (CardFamily.FeliCa, 8) => "IDm 8B",
        (CardFamily.Iso14443B, 4) => "PUPI 4B",
        _ => $"{uid.Length}B"
    };
}
