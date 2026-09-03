using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using Microsoft.Win32;
using Serilog;

namespace RfidReaderMonitor.Sys;

/// <summary>Windows 쪽 점검 항목과 관리자 권한 적용 도우미.</summary>
public static class SystemChecks
{
    private const string ScPnpKey = @"SOFTWARE\Policies\Microsoft\Windows\ScPnP";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AppRunName = "RfidReaderMonitor";

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>true: 정책으로 꺼짐(권장), false: 켜져 있음(기본), null: 확인 불가.</summary>
    public static bool? IsScPnpDisabled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ScPnpKey);
            var v = k?.GetValue("EnableScPnP");
            if (v is null) return false;
            return v is int i && i == 0;
        }
        catch { return null; }
    }

    public static string ScPnpCommand =>
        @"reg add ""HKLM\SOFTWARE\Policies\Microsoft\Windows\ScPnP"" /v EnableScPnP /t REG_DWORD /d 0 /f";

    public static bool ApplyScPnpDisable()
        => RunElevated("reg.exe", @"add ""HKLM\SOFTWARE\Policies\Microsoft\Windows\ScPnP"" /v EnableScPnP /t REG_DWORD /d 0 /f");

    /// <summary>정책 값을 지워 Windows 기본(켜짐)으로 되돌린다.</summary>
    public static string ScPnpRestoreCommand =>
        @"reg delete ""HKLM\SOFTWARE\Policies\Microsoft\Windows\ScPnP"" /v EnableScPnP /f";

    public static bool ApplyScPnpRestore()
        => RunElevated("reg.exe", @"delete ""HKLM\SOFTWARE\Policies\Microsoft\Windows\ScPnP"" /v EnableScPnP /f");

    public static string EscapeEnableCommand(string instanceId)
        => $@"reg add ""{UsbDeviceMapper.EscapeRegistryPath(instanceId)}"" /v EscapeCommandEnable /t REG_DWORD /d 1 /f";

    public static bool ApplyEscapeEnable(string instanceId)
        => RunElevated("reg.exe", $@"add ""{UsbDeviceMapper.EscapeRegistryPath(instanceId)}"" /v EscapeCommandEnable /t REG_DWORD /d 1 /f");

    public static string? SmartCardServiceState()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT State, StartMode FROM Win32_Service WHERE Name = 'SCardSvr'");
            foreach (var o in s.Get())
                return $"{o["State"]} ({o["StartMode"]})";
            return "없음";
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SCardSvr 조회 실패");
            return null;
        }
    }

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(AppRunName) is string;
        }
        catch { return false; }
    }

    public static void SetAutostart(bool enable, bool minimized)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable)
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var cmd = $"\"{exe}\"" + (minimized ? " --minimized" : "");
            k.SetValue(AppRunName, cmd);
        }
        else
        {
            k.DeleteValue(AppRunName, false);
        }
    }

    /// <summary>UAC 승격으로 명령 실행. 사용자가 UAC 창에서 직접 승인한다.</summary>
    private static bool RunElevated(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(30_000);
            Log.Information("승격 실행 {File} {Args} → exit {Code}", file, args, p.ExitCode);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "승격 실행 취소/실패");
            return false;
        }
    }
}
