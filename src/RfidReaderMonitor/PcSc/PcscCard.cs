using System.Text;
using RfidReaderMonitor.Native;

namespace RfidReaderMonitor.PcSc;

/// <summary>SCARDHANDLE 래퍼. 짧게 열고 바로 닫는 용도.</summary>
public sealed class PcscCard : IDisposable
{
    private IntPtr _card;

    public uint ActiveProtocol { get; }
    public string ReaderName { get; }

    internal PcscCard(IntPtr card, uint activeProtocol, string readerName)
    {
        _card = card;
        ActiveProtocol = activeProtocol;
        ReaderName = readerName;
    }

    public byte[]? GetAttrib(uint attrId)
    {
        uint len = 0;
        var rc = WinScard.SCardGetAttrib(_card, attrId, null, ref len);
        if (rc != PcscError.S_SUCCESS || len == 0) return null;
        var buf = new byte[len];
        rc = WinScard.SCardGetAttrib(_card, attrId, buf, ref len);
        if (rc != PcscError.S_SUCCESS) return null;
        Array.Resize(ref buf, (int)len);
        return buf;
    }

    public string? GetAttribString(uint attrId)
    {
        var b = GetAttrib(attrId);
        if (b is null) return null;
        var s = Encoding.ASCII.GetString(b).TrimEnd('\0', ' ');
        return s.Length == 0 ? null : s;
    }

    /// <summary>APDU 전송. 응답은 SW1 SW2 포함.</summary>
    public byte[] Transmit(byte[] apdu)
    {
        var pci = new WinScard.SCARD_IO_REQUEST
        {
            dwProtocol = ActiveProtocol == 0 ? WinScard.SCARD_PROTOCOL_T1 : ActiveProtocol,
            cbPciLength = 8
        };
        var recv = new byte[258];
        uint recvLen = (uint)recv.Length;
        var rc = WinScard.SCardTransmit(_card, ref pci, apdu, (uint)apdu.Length, IntPtr.Zero, recv, ref recvLen);
        if (rc != PcscError.S_SUCCESS) throw new PcscException("SCardTransmit", rc);
        Array.Resize(ref recv, (int)recvLen);
        return recv;
    }

    /// <summary>CCID 에스케이프 명령 (제조사 전용).</summary>
    public byte[] Escape(byte[] command)
    {
        var recv = new byte[300];
        var rc = WinScard.SCardControl(_card, WinScard.IOCTL_CCID_ESCAPE, command, (uint)command.Length, recv, (uint)recv.Length, out var returned);
        if (rc != PcscError.S_SUCCESS) throw new PcscException("SCardControl(Escape)", rc);
        Array.Resize(ref recv, (int)returned);
        return recv;
    }

    public (uint state, uint protocol, byte[] atr) Status()
    {
        uint nameLen = 0;
        uint atrLen = 36;
        var atr = new byte[36];
        var rc = WinScard.SCardStatus(_card, null, ref nameLen, out var state, out var proto, atr, ref atrLen);
        if (rc != PcscError.S_SUCCESS) throw new PcscException("SCardStatus", rc);
        Array.Resize(ref atr, (int)atrLen);
        return (state, proto, atr);
    }

    public void Dispose()
    {
        if (_card != IntPtr.Zero)
        {
            WinScard.SCardDisconnect(_card, WinScard.SCARD_LEAVE_CARD);
            _card = IntPtr.Zero;
        }
    }
}
