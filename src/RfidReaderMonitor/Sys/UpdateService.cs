using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace RfidReaderMonitor.Sys;

public sealed record ReleaseInfo(
    string Tag,
    Version Version,
    string Name,
    string Notes,
    string HtmlUrl,
    DateTimeOffset? Published,
    string? ExeUrl,
    long ExeSize,
    string? ShaUrl)
{
    public bool HasExe => ExeUrl is not null;
    public bool HasChecksum => ShaUrl is not null;
}

/// <summary>
/// GitHub Releases 기반 수동 업데이트.
///  1. GetLatestAsync: 최신 릴리스 조회 (비인증, 공개 저장소)
///  2. DownloadAsync: 자산 exe 를 .new 로 내려받고 .sha256 과 대조
///  3. ApplyAndRestart: 종료 후 파일을 바꾸고 같은 인자로 다시 실행하는 스크립트 실행
/// </summary>
public static class UpdateService
{
    public const string Owner = "SYacuCLoud";
    public const string Repo = "RfidReaderMonitor";
    public const string ExeAssetName = "RfidReaderMonitor.exe";
    public const string ShaAssetName = "RfidReaderMonitor.exe.sha256";

    public static Version Current
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string CurrentExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RfidReaderMonitor", Current.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static async Task<ReleaseInfo> GetLatestAsync(CancellationToken ct)
    {
        using var http = CreateClient();
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag) ?? new Version(0, 0, 0);
        string? exeUrl = null, shaUrl = null;
        long exeSize = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (name.Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase)) { exeUrl = url; exeSize = a.GetProperty("size").GetInt64(); }
                else if (name.Equals(ShaAssetName, StringComparison.OrdinalIgnoreCase)) shaUrl = url;
            }
        }
        DateTimeOffset? published = root.TryGetProperty("published_at", out var p) && DateTimeOffset.TryParse(p.GetString(), out var dt) ? dt : null;

        return new ReleaseInfo(
            tag, version,
            root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
            published, exeUrl, exeSize, shaUrl);
    }

    public static Version? ParseVersion(string tag)
    {
        var t = tag.Trim();
        if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t[1..];
        var dash = t.IndexOf('-');
        if (dash > 0) t = t[..dash];
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>exe 자산을 destPath 로 내려받고 sha256 자산과 대조한다. 불일치면 파일을 지우고 예외.</summary>
    public static async Task DownloadAsync(ReleaseInfo r, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        if (r.ExeUrl is null) throw new InvalidOperationException("릴리스에 exe 자산이 없습니다.");
        if (r.ShaUrl is null) throw new InvalidOperationException("릴리스에 sha256 자산이 없어 무결성을 확인할 수 없습니다.");

        using var http = CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        var shaText = await http.GetStringAsync(r.ShaUrl, ct);
        var expected = shaText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant();
        if (expected is null || expected.Length != 64) throw new InvalidOperationException("sha256 파일 형식이 올바르지 않습니다.");

        using (var resp = await http.GetAsync(r.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? r.ExeSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
            }
        }

        string actual;
        await using (var fs = new FileStream(destPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        }
        if (actual != expected)
        {
            try { File.Delete(destPath); } catch { }
            throw new InvalidOperationException($"sha256 불일치. 내려받은 파일을 지웠습니다.\n기대: {expected}\n실제: {actual}");
        }
        Log.Information("업데이트 다운로드 완료 {Tag} → {Path}", r.Tag, destPath);
    }

    /// <summary>
    /// 현재 프로세스 종료를 기다린 뒤 exe 를 교체하고 같은 인자로 다시 실행하는 cmd 스크립트를 띄운다.
    /// 호출한 쪽은 곧바로 프로그램을 종료해야 한다.
    /// </summary>
    public static void ApplyAndRestart(string newExePath, IEnumerable<string> restartArgs)
    {
        var exe = CurrentExePath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        var bak = exe + ".bak";
        var pid = Environment.ProcessId;
        var args = string.Join(" ", restartArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        var script = Path.Combine(Path.GetTempPath(), "RfidReaderMonitor-update.cmd");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
        sb.AppendLine("if not errorlevel 1 (ping -n 2 127.0.0.1 >nul & goto wait)");
        sb.AppendLine($"if exist \"{bak}\" del /f /q \"{bak}\"");
        sb.AppendLine($"move /y \"{exe}\" \"{bak}\" >nul");
        sb.AppendLine("if errorlevel 1 goto fail");
        sb.AppendLine($"move /y \"{newExePath}\" \"{exe}\" >nul");
        sb.AppendLine("if errorlevel 1 goto restore");
        sb.AppendLine($"start \"\" \"{exe}\" {args}");
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":restore");
        sb.AppendLine($"move /y \"{bak}\" \"{exe}\" >nul");
        sb.AppendLine(":fail");
        sb.AppendLine($"start \"\" \"{exe}\" {args}");
        sb.AppendLine("exit /b 1");
        File.WriteAllText(script, sb.ToString(), Encoding.Default);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
        });
        Log.Information("업데이트 적용 스크립트 시작, 프로그램 종료 예정");
    }

    /// <summary>이전 업데이트가 남긴 .bak 정리.</summary>
    public static void CleanupBackup()
    {
        try
        {
            var bak = CurrentExePath + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
        }
        catch { }
    }
}
