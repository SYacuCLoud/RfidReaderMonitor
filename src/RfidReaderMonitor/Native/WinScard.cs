using System.Runtime.InteropServices;

namespace RfidReaderMonitor.Native;

/// <summary>winscard.dll P/Invoke. 유니코드 API만 사용한다.</summary>
internal static class WinScard
{
    private const string Lib = "winscard.dll";

    public const uint SCARD_SCOPE_USER = 0;
    public const uint SCARD_SCOPE_SYSTEM = 2;

    public const uint SCARD_SHARE_EXCLUSIVE = 1;
    public const uint SCARD_SHARE_SHARED = 2;
    public const uint SCARD_SHARE_DIRECT = 3;

    public const uint SCARD_PROTOCOL_UNDEFINED = 0;
    public const uint SCARD_PROTOCOL_T0 = 1;
    public const uint SCARD_PROTOCOL_T1 = 2;
    public const uint SCARD_PROTOCOL_RAW = 0x10000;

    public const uint SCARD_LEAVE_CARD = 0;
    public const uint SCARD_RESET_CARD = 1;
    public const uint SCARD_UNPOWER_CARD = 2;

    public const uint INFINITE = 0xFFFFFFFF;

    // dwCurrentState / dwEventState
    public const uint SCARD_STATE_UNAWARE = 0x0000;
    public const uint SCARD_STATE_IGNORE = 0x0001;
    public const uint SCARD_STATE_CHANGED = 0x0002;
    public const uint SCARD_STATE_UNKNOWN = 0x0004;
    public const uint SCARD_STATE_UNAVAILABLE = 0x0008;
    public const uint SCARD_STATE_EMPTY = 0x0010;
    public const uint SCARD_STATE_PRESENT = 0x0020;
    public const uint SCARD_STATE_ATRMATCH = 0x0040;
    public const uint SCARD_STATE_EXCLUSIVE = 0x0080;
    public const uint SCARD_STATE_INUSE = 0x0100;
    public const uint SCARD_STATE_MUTE = 0x0200;
    public const uint SCARD_STATE_UNPOWERED = 0x0400;

    /// <summary>리더 추가/제거 알림용 가상 리더 이름.</summary>
    public const string PnpNotificationReader = "\\\\?PnP?\\Notification";

    // SCardGetAttrib ids
    public const uint SCARD_ATTR_VENDOR_NAME = 0x00010100;
    public const uint SCARD_ATTR_VENDOR_IFD_TYPE = 0x00010101;
    public const uint SCARD_ATTR_VENDOR_IFD_VERSION = 0x00010102;
    public const uint SCARD_ATTR_VENDOR_IFD_SERIAL_NO = 0x00010103;
    public const uint SCARD_ATTR_CHANNEL_ID = 0x00020110;
    public const uint SCARD_ATTR_ATR_STRING = 0x00090303;
    public const uint SCARD_ATTR_DEVICE_FRIENDLY_NAME = 0x7FFF0003;
    public const uint SCARD_ATTR_DEVICE_SYSTEM_NAME = 0x7FFF0004;

    /// <summary>CCID 에스케이프 IOCTL = SCARD_CTL_CODE(3500).</summary>
    public const uint IOCTL_CCID_ESCAPE = 0x00310000 | (3500 << 2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SCARD_READERSTATE
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string szReader;
        public IntPtr pvUserData;
        public uint dwCurrentState;
        public uint dwEventState;
        public uint cbAtr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)] public byte[] rgbAtr;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCARD_IO_REQUEST
    {
        public uint dwProtocol;
        public uint cbPciLength;
    }

    [DllImport(Lib)]
    public static extern uint SCardEstablishContext(uint dwScope, IntPtr pvReserved1, IntPtr pvReserved2, out IntPtr phContext);

    [DllImport(Lib)]
    public static extern uint SCardReleaseContext(IntPtr hContext);

    [DllImport(Lib)]
    public static extern uint SCardIsValidContext(IntPtr hContext);

    [DllImport(Lib)]
    public static extern uint SCardCancel(IntPtr hContext);

    [DllImport(Lib, CharSet = CharSet.Unicode, EntryPoint = "SCardListReadersW")]
    public static extern uint SCardListReaders(IntPtr hContext, string? mszGroups, byte[]? mszReaders, ref uint pcchReaders);

    [DllImport(Lib, CharSet = CharSet.Unicode, EntryPoint = "SCardGetStatusChangeW")]
    public static extern uint SCardGetStatusChange(IntPtr hContext, uint dwTimeout, [In, Out] SCARD_READERSTATE[] rgReaderStates, uint cReaders);

    [DllImport(Lib, CharSet = CharSet.Unicode, EntryPoint = "SCardConnectW")]
    public static extern uint SCardConnect(IntPtr hContext, string szReader, uint dwShareMode, uint dwPreferredProtocols, out IntPtr phCard, out uint pdwActiveProtocol);

    [DllImport(Lib)]
    public static extern uint SCardDisconnect(IntPtr hCard, uint dwDisposition);

    [DllImport(Lib)]
    public static extern uint SCardGetAttrib(IntPtr hCard, uint dwAttrId, byte[]? pbAttr, ref uint pcbAttrLen);

    [DllImport(Lib)]
    public static extern uint SCardTransmit(IntPtr hCard, ref SCARD_IO_REQUEST pioSendPci, byte[] pbSendBuffer, uint cbSendLength, IntPtr pioRecvPci, byte[] pbRecvBuffer, ref uint pcbRecvLength);

    [DllImport(Lib)]
    public static extern uint SCardControl(IntPtr hCard, uint dwControlCode, byte[] lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned);

    [DllImport(Lib, CharSet = CharSet.Unicode, EntryPoint = "SCardStatusW")]
    public static extern uint SCardStatus(IntPtr hCard, byte[]? szReaderName, ref uint pcchReaderLen, out uint pdwState, out uint pdwProtocol, byte[]? pbAtr, ref uint pcbAtrLen);
}
