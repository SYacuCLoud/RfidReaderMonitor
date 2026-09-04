using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace RfidReaderMonitor.Mqtt;

/// <summary>
/// settings.json 에 비밀번호를 평문으로 두지 않기 위한 DPAPI(현재 Windows 사용자) 래퍼.
/// 다른 사용자 계정이나 다른 PC 로 설정 파일을 옮기면 풀리지 않는다. 그 경우 빈 문자열을 돌려준다.
/// </summary>
public static class Secret
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RfidReaderMonitor.mqtt.v1");

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "비밀번호 보호 실패");
            return "";
        }
    }

    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Log.Warning("보호된 비밀번호를 풀 수 없음 (다른 사용자/PC 에서 만든 설정?): {Msg}", ex.Message);
            return "";
        }
    }
}
