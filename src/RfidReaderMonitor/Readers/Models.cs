using RfidReaderMonitor.Core;

namespace RfidReaderMonitor.Readers;

/// <summary>리더가 보고하는 원신호 상태.</summary>
public enum PresenceState
{
    Unknown,
    /// <summary>필드에 태그 없음.</summary>
    Empty,
    /// <summary>태그 있음, 접근 가능.</summary>
    Present,
    /// <summary>태그 있음, 다른 프로그램이 독점 사용 중.</summary>
    InUse,
    /// <summary>태그 있음, 응답 없음.</summary>
    Mute,
    /// <summary>리더가 응답하지 않음.</summary>
    Unavailable,
    /// <summary>리더가 시스템에서 사라짐.</summary>
    Removed
}

public static class PresenceStateText
{
    public static string Ko(this PresenceState s) => s switch
    {
        PresenceState.Empty => "EMPTY",
        PresenceState.Present => "PRESENT",
        PresenceState.InUse => "PRESENT (사용중)",
        PresenceState.Mute => "PRESENT (무응답)",
        PresenceState.Unavailable => "사용불가",
        PresenceState.Removed => "뽑힘",
        _ => "?"
    };
}

/// <summary>리더가 어떤 경로로 붙어 있는지. 화면이 종류별로 다른 정보를 보여 주는 기준.</summary>
public enum ReaderKind
{
    /// <summary>Windows PC/SC(WinSCard) 리더. 대개 USB CCID.</summary>
    PcSc,
    /// <summary>Modbus TCP 로 폴링하는 산업용 리더/IO-Link 마스터.</summary>
    Modbus
}

public static class ReaderKindText
{
    public static string Ko(this ReaderKind k) => k switch
    {
        ReaderKind.PcSc => "USB PC/SC",
        ReaderKind.Modbus => "Modbus TCP",
        _ => "?"
    };
}

public sealed record ReaderIdentity(
    string? Vendor,
    string? IfdType,
    string? IfdVersion,
    string? Serial,
    string? DeviceSystemName);

public sealed record TagReadResult(byte[] Uid, byte[] Atr, CardTech Tech);

public sealed class ReaderPresenceEventArgs : EventArgs
{
    public required string ReaderName { get; init; }
    public required PresenceState State { get; init; }
    public required byte[] Atr { get; init; }
    public required uint RawFlags { get; init; }
    public required DateTimeOffset Time { get; init; }
    /// <summary>원신호 표에 보일 원값 글자. null 이면 ATR hex 를 쓴다 (PC/SC). Modbus 는 레지스터 값.</summary>
    public string? RawText { get; init; }
}

public sealed class ReaderListChangedEventArgs : EventArgs
{
    public required IReadOnlyList<string> Readers { get; init; }
}

public sealed class ProviderStatusEventArgs : EventArgs
{
    public required bool Healthy { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// 리더 하부 구현 추상화. 지금은 PC/SC, 나중에 IO-Link 헤드 등으로 교체 가능.
/// 동기 메서드는 호출 스레드에서 블로킹된다 (UI에서는 Task.Run으로 호출).
/// </summary>
public interface IRfidReaderProvider : IDisposable
{
    event EventHandler<ReaderListChangedEventArgs>? ReadersChanged;
    event EventHandler<ReaderPresenceEventArgs>? PresenceChanged;
    event EventHandler<ProviderStatusEventArgs>? StatusChanged;

    IReadOnlyList<string> Readers { get; }

    void Start();
    void Stop();

    /// <summary>리더 목록 재조회 또는 재접속을 강제한다.</summary>
    void Refresh();

    /// <summary>리더의 연결 종류. 목록에 없는 이름이면 예외.</summary>
    ReaderKind KindOf(string readerName);

    /// <summary>제조사, 모델, 펌웨어 버전, S/N 등. 카드 유무 무관.</summary>
    ReaderIdentity Identify(string readerName);

    /// <summary>현재 필드의 태그 UID 읽기. 태그가 없으면 예외.</summary>
    TagReadResult ReadTag(string readerName);

    /// <summary>제조사 에스케이프 명령. 지원하지 않으면 예외.</summary>
    byte[] Escape(string readerName, byte[] command);
}
