using System.Text;
using RfidReaderMonitor.Native;

namespace RfidReaderMonitor.PcSc;

/// <summary>SCARDCONTEXT 래퍼.</summary>
public sealed class PcscContext : IDisposable
{
    private IntPtr _handle;

    public IntPtr Handle => _handle;
    public bool IsValid => _handle != IntPtr.Zero && WinScard.SCardIsValidContext(_handle) == PcscError.S_SUCCESS;

    public static PcscContext Establish()
    {
        var rc = WinScard.SCardEstablishContext(WinScard.SCARD_SCOPE_SYSTEM, IntPtr.Zero, IntPtr.Zero, out var h);
        if (rc != PcscError.S_SUCCESS) throw new PcscException("SCardEstablishContext", rc);
        return new PcscContext { _handle = h };
    }

    public IReadOnlyList<string> ListReaders()
    {
        uint len = 0;
        var rc = WinScard.SCardListReaders(_handle, null, null, ref len);
        if (rc == PcscError.E_NO_READERS_AVAILABLE) return Array.Empty<string>();
        if (rc != PcscError.S_SUCCESS) throw new PcscException("SCardListReaders", rc);

        var buf = new byte[len * 2];
        rc = WinScard.SCardListReaders(_handle, null, buf, ref len);
        if (rc == PcscError.E_NO_READERS_AVAILABLE) return Array.Empty<string>();
        if (rc != PcscError.S_SUCCESS) throw new PcscException("SCardListReaders", rc);

        var multi = Encoding.Unicode.GetString(buf, 0, (int)len * 2);
        return multi.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public void Cancel()
    {
        if (_handle != IntPtr.Zero) WinScard.SCardCancel(_handle);
    }

    public PcscCard Connect(string reader, uint shareMode, uint protocols)
    {
        var rc = WinScard.SCardConnect(_handle, reader, shareMode, protocols, out var card, out var active);
        if (rc != PcscError.S_SUCCESS) throw new PcscException("SCardConnect", rc);
        return new PcscCard(card, active, reader);
    }

    /// <summary>카드 유무와 무관하게 리더 자체에 접근 (속성 조회, 에스케이프).</summary>
    public PcscCard ConnectDirect(string reader)
        => Connect(reader, WinScard.SCARD_SHARE_DIRECT, WinScard.SCARD_PROTOCOL_UNDEFINED);

    /// <summary>카드가 있을 때 공유 모드로 접근 (APDU).</summary>
    public PcscCard ConnectShared(string reader)
        => Connect(reader, WinScard.SCARD_SHARE_SHARED, WinScard.SCARD_PROTOCOL_T0 | WinScard.SCARD_PROTOCOL_T1);

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            WinScard.SCardReleaseContext(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
