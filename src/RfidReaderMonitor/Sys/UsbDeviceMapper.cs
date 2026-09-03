using System.Management;
using Microsoft.Win32;
using Serilog;

namespace RfidReaderMonitor.Sys;

/// <summary>PnP 스마트카드 리더 장치 정보. PC/SC 리더와 USB 시리얼을 이어 붙이는 데 쓴다.</summary>
public sealed record UsbReaderDevice(
    string InstanceId,
    string FriendlyName,
    string Status,
    string? ParentInstanceId,
    string? Serial,
    string? PortPath,
    string? DriverService,
    bool? EscapeEnabled)
{
    public bool IsPresent => string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase);
    public string EscapeText => EscapeEnabled switch { true => "설정됨", false => "미설정", null => "확인 불가" };
}

public static class UsbDeviceMapper
{
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";

    public static IReadOnlyList<UsbReaderDevice> EnumerateSmartCardReaders()
    {
        var list = new List<UsbReaderDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, Status, Service FROM Win32_PnPEntity WHERE PNPClass = 'SmartCardReader'");
            foreach (var o in searcher.Get())
            {
                var id = o["DeviceID"] as string ?? "";
                if (!id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
                var name = o["Name"] as string ?? "";
                var status = o["Status"] as string ?? "";
                var service = o["Service"] as string;

                var (parent, serial, port) = ResolveParent(id);
                var escape = ReadEscapeEnabled(id);
                list.Add(new UsbReaderDevice(id, name, status, parent, serial, port, service, escape));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WMI 스마트카드 리더 조회 실패");
        }
        return list;
    }

    /// <summary>
    /// USB\VID_xxxx&PID_yyyy&MI_nn\prefix&idx → 부모 USB\VID_xxxx&PID_yyyy\SERIAL (ParentIdPrefix 매칭).
    /// 복합 장치가 아니면 자기 인스턴스 이름이 시리얼(또는 포트 경로).
    /// </summary>
    private static (string? parent, string? serial, string? portPath) ResolveParent(string instanceId)
    {
        var parts = instanceId.Split('\\');
        if (parts.Length != 3) return (null, null, null);
        var hw = parts[1];
        var inst = parts[2];

        int mi = hw.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        if (mi < 0)
        {
            return inst.Contains('&') ? (instanceId, null, inst) : (instanceId, inst, null);
        }

        var parentHw = hw[..mi];
        var lastAmp = inst.LastIndexOf('&');
        var prefix = lastAmp > 0 ? inst[..lastAmp] : inst;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\USB\{parentHw}");
            if (key is null) return (null, null, null);
            foreach (var sub in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(sub);
                var pip = k?.GetValue("ParentIdPrefix") as string;
                if (pip is not null && string.Equals(pip, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var parentId = $@"USB\{parentHw}\{sub}";
                    return sub.Contains('&') ? (parentId, null, sub) : (parentId, sub, null);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ParentIdPrefix 조회 실패 {Id}", instanceId);
        }
        return (null, null, null);
    }

    public static bool? ReadEscapeEnabled(string instanceId)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{instanceId}\Device Parameters");
            if (k is null) return null;
            var v = k.GetValue("EscapeCommandEnable");
            return v is int i && i != 0;
        }
        catch
        {
            return null;
        }
    }

    public static string EscapeRegistryPath(string instanceId)
        => $@"HKLM\{EnumRoot}\{instanceId}\Device Parameters";

    /// <summary>PC/SC 시리얼로 장치를 찾는다. 시리얼이 없으면 연결된 장치가 하나일 때만 매핑.</summary>
    public static UsbReaderDevice? Match(IReadOnlyList<UsbReaderDevice> devices, string? serial, string pcscName)
    {
        var present = devices.Where(d => d.IsPresent).ToList();
        if (!string.IsNullOrWhiteSpace(serial))
        {
            var hit = present.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        // 이름 힌트: "ACR1552 1S CL Reader PICC" 가 PC/SC 이름에 포함되는 장치
        var byName = present.Where(d => pcscName.Contains(d.FriendlyName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        if (present.Count == 1) return present[0];
        return null;
    }
}
