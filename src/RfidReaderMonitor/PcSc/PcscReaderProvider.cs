using RfidReaderMonitor.Core;
using RfidReaderMonitor.Native;
using RfidReaderMonitor.Readers;

namespace RfidReaderMonitor.PcSc;

/// <summary>PC/SC 기반 IRfidReaderProvider 구현.</summary>
public sealed class PcscReaderProvider : IRfidReaderProvider
{
    private static readonly byte[] GetUidApdu = { 0xFF, 0xCA, 0x00, 0x00, 0x00 };

    private readonly PcscMonitor _monitor = new();

    public event EventHandler<ReaderListChangedEventArgs>? ReadersChanged
    {
        add => _monitor.ReadersChanged += value;
        remove => _monitor.ReadersChanged -= value;
    }

    public event EventHandler<ReaderPresenceEventArgs>? PresenceChanged
    {
        add => _monitor.PresenceChanged += value;
        remove => _monitor.PresenceChanged -= value;
    }

    public event EventHandler<ProviderStatusEventArgs>? StatusChanged
    {
        add => _monitor.StatusChanged += value;
        remove => _monitor.StatusChanged -= value;
    }

    public IReadOnlyList<string> Readers => _monitor.Readers;

    public void Start() => _monitor.Start();
    public void Stop() => _monitor.Stop();
    public void Refresh() => _monitor.Refresh();

    public ReaderIdentity Identify(string readerName)
    {
        using var ctx = PcscContext.Establish();
        using var card = ctx.ConnectDirect(readerName);
        var ver = card.GetAttrib(WinScard.SCARD_ATTR_VENDOR_IFD_VERSION);
        string? verText = null;
        if (ver is { Length: 4 })
        {
            // DWORD: major(2) minor(1) build(1) 순서는 드라이버마다 다름 → 원시 표기
            verText = $"{ver[3]}.{ver[2]}.{(ver[1] << 8) | ver[0]}";
        }
        else if (ver is not null) verText = Hex.Of(ver);

        return new ReaderIdentity(
            card.GetAttribString(WinScard.SCARD_ATTR_VENDOR_NAME),
            card.GetAttribString(WinScard.SCARD_ATTR_VENDOR_IFD_TYPE),
            verText,
            card.GetAttribString(WinScard.SCARD_ATTR_VENDOR_IFD_SERIAL_NO),
            card.GetAttribString(WinScard.SCARD_ATTR_DEVICE_SYSTEM_NAME));
    }

    public TagReadResult ReadTag(string readerName)
    {
        using var ctx = PcscContext.Establish();
        using var card = ctx.ConnectShared(readerName);
        var (_, _, atr) = card.Status();
        var resp = card.Transmit(GetUidApdu);
        if (resp.Length < 2) throw new InvalidOperationException("응답이 너무 짧음");
        var sw1 = resp[^2];
        var sw2 = resp[^1];
        if (sw1 != 0x90 || sw2 != 0x00)
            throw new InvalidOperationException($"GET DATA(UID) 실패 SW={sw1:X2}{sw2:X2}");
        var uid = resp.AsSpan(0, resp.Length - 2).ToArray();
        if (uid.Length == 0) throw new InvalidOperationException("UID 길이 0");
        return new TagReadResult(uid, atr, AtrParser.Parse(atr));
    }

    public byte[] Escape(string readerName, byte[] command)
    {
        using var ctx = PcscContext.Establish();
        using var card = ctx.ConnectDirect(readerName);
        return card.Escape(command);
    }

    public void Dispose() => _monitor.Dispose();
}
