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

    /// <summary>제조사, 모델, 펌웨어 버전, S/N 등. 카드 유무 무관.</summary>
    ReaderIdentity Identify(string readerName);

    /// <summary>현재 필드의 태그 UID 읽기. 태그가 없으면 예외.</summary>
    TagReadResult ReadTag(string readerName);

    /// <summary>제조사 에스케이프 명령. 지원하지 않으면 예외.</summary>
    byte[] Escape(string readerName, byte[] command);
}
