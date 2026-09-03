namespace RfidReaderMonitor.PcSc;

/// <summary>WinSCard 반환 코드와 한국어 설명.</summary>
public static class PcscError
{
    public const uint S_SUCCESS = 0x00000000;
    public const uint E_CANCELLED = 0x80100002;
    public const uint E_INVALID_HANDLE = 0x80100003;
    public const uint E_INVALID_PARAMETER = 0x80100004;
    public const uint E_INSUFFICIENT_BUFFER = 0x80100008;
    public const uint E_UNKNOWN_READER = 0x80100009;
    public const uint E_TIMEOUT = 0x8010000A;
    public const uint E_SHARING_VIOLATION = 0x8010000B;
    public const uint E_NO_SMARTCARD = 0x8010000C;
    public const uint E_UNKNOWN_CARD = 0x8010000D;
    public const uint E_PROTO_MISMATCH = 0x8010000F;
    public const uint E_NOT_READY = 0x80100010;
    public const uint E_SYSTEM_CANCELLED = 0x80100012;
    public const uint F_COMM_ERROR = 0x80100013;
    public const uint F_UNKNOWN_ERROR = 0x80100014;
    public const uint E_NOT_TRANSACTED = 0x80100016;
    public const uint E_READER_UNAVAILABLE = 0x80100017;
    public const uint E_NO_SERVICE = 0x8010001D;
    public const uint E_SERVICE_STOPPED = 0x8010001E;
    public const uint E_UNSUPPORTED_FEATURE = 0x80100022;
    public const uint E_NO_READERS_AVAILABLE = 0x8010002E;
    public const uint E_COMM_DATA_LOST = 0x8010002F;
    public const uint W_UNSUPPORTED_CARD = 0x80100065;
    public const uint W_UNRESPONSIVE_CARD = 0x80100066;
    public const uint W_UNPOWERED_CARD = 0x80100067;
    public const uint W_RESET_CARD = 0x80100068;
    public const uint W_REMOVED_CARD = 0x80100069;

    public static string Name(uint rc) => rc switch
    {
        S_SUCCESS => "SCARD_S_SUCCESS",
        E_CANCELLED => "SCARD_E_CANCELLED",
        E_INVALID_HANDLE => "SCARD_E_INVALID_HANDLE",
        E_INVALID_PARAMETER => "SCARD_E_INVALID_PARAMETER",
        E_INSUFFICIENT_BUFFER => "SCARD_E_INSUFFICIENT_BUFFER",
        E_UNKNOWN_READER => "SCARD_E_UNKNOWN_READER",
        E_TIMEOUT => "SCARD_E_TIMEOUT",
        E_SHARING_VIOLATION => "SCARD_E_SHARING_VIOLATION",
        E_NO_SMARTCARD => "SCARD_E_NO_SMARTCARD",
        E_UNKNOWN_CARD => "SCARD_E_UNKNOWN_CARD",
        E_PROTO_MISMATCH => "SCARD_E_PROTO_MISMATCH",
        E_NOT_READY => "SCARD_E_NOT_READY",
        E_SYSTEM_CANCELLED => "SCARD_E_SYSTEM_CANCELLED",
        F_COMM_ERROR => "SCARD_F_COMM_ERROR",
        F_UNKNOWN_ERROR => "SCARD_F_UNKNOWN_ERROR",
        E_NOT_TRANSACTED => "SCARD_E_NOT_TRANSACTED",
        E_READER_UNAVAILABLE => "SCARD_E_READER_UNAVAILABLE",
        E_NO_SERVICE => "SCARD_E_NO_SERVICE",
        E_SERVICE_STOPPED => "SCARD_E_SERVICE_STOPPED",
        E_UNSUPPORTED_FEATURE => "SCARD_E_UNSUPPORTED_FEATURE",
        E_NO_READERS_AVAILABLE => "SCARD_E_NO_READERS_AVAILABLE",
        E_COMM_DATA_LOST => "SCARD_E_COMM_DATA_LOST",
        W_UNSUPPORTED_CARD => "SCARD_W_UNSUPPORTED_CARD",
        W_UNRESPONSIVE_CARD => "SCARD_W_UNRESPONSIVE_CARD",
        W_UNPOWERED_CARD => "SCARD_W_UNPOWERED_CARD",
        W_RESET_CARD => "SCARD_W_RESET_CARD",
        W_REMOVED_CARD => "SCARD_W_REMOVED_CARD",
        _ => $"0x{rc:X8}"
    };

    public static string Describe(uint rc) => rc switch
    {
        S_SUCCESS => "성공",
        E_CANCELLED => "호출이 취소됨",
        E_INVALID_HANDLE => "컨텍스트/카드 핸들이 무효함 (서비스 재시작 가능성)",
        E_UNKNOWN_READER => "리더 이름을 찾을 수 없음 (리더가 제거됨)",
        E_TIMEOUT => "시간 초과",
        E_SHARING_VIOLATION => "다른 프로그램이 리더를 독점 사용 중",
        E_NO_SMARTCARD => "리더에 카드/태그가 없음",
        E_UNKNOWN_CARD => "알 수 없는 카드",
        E_PROTO_MISMATCH => "프로토콜 불일치",
        E_NOT_READY => "리더 준비 안 됨",
        F_COMM_ERROR => "리더 통신 오류",
        E_NOT_TRANSACTED => "명령이 전송되지 않음 (에스케이프 명령이면 EscapeCommandEnable 미설정 의심)",
        E_READER_UNAVAILABLE => "리더를 사용할 수 없음 (뽑힘 또는 드라이버 오류)",
        E_NO_SERVICE => "스마트카드 서비스(SCardSvr)가 실행 중이 아님",
        E_SERVICE_STOPPED => "스마트카드 서비스가 중지됨",
        E_UNSUPPORTED_FEATURE => "리더가 지원하지 않는 기능",
        E_NO_READERS_AVAILABLE => "연결된 리더가 없음",
        E_COMM_DATA_LOST => "통신 데이터 손실",
        W_UNSUPPORTED_CARD => "지원하지 않는 카드",
        W_UNRESPONSIVE_CARD => "카드가 응답하지 않음 (MUTE)",
        W_UNPOWERED_CARD => "카드 전원 없음",
        W_RESET_CARD => "카드가 리셋됨",
        W_REMOVED_CARD => "카드가 제거됨",
        _ => "알 수 없는 오류"
    };

    public static string Format(uint rc) => $"{Name(rc)} - {Describe(rc)}";
}

public sealed class PcscException : Exception
{
    public uint Code { get; }
    public string Operation { get; }

    public PcscException(string operation, uint code)
        : base($"{operation}: {PcscError.Format(code)}")
    {
        Operation = operation;
        Code = code;
    }

    public bool IsContextLost => Code is PcscError.E_INVALID_HANDLE or PcscError.E_NO_SERVICE or PcscError.E_SERVICE_STOPPED or PcscError.E_SYSTEM_CANCELLED;
}
