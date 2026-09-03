using System.Diagnostics;
using RfidReaderMonitor.Readers;

namespace RfidReaderMonitor.Core;

public sealed record ReadRateProgress(int Attempts, int Successes, long ElapsedMs, long CurrentGapMs, long MaxGapMs, string LastUid);

public sealed record ReadRateResult(
    string ReaderName,
    int Attempts,
    int Successes,
    double SuccessRatePercent,
    long MaxGapMs,
    double AvgLatencyMs,
    long MaxLatencyMs,
    IReadOnlyList<string> DistinctUids,
    long ElapsedMs)
{
    public string Summary =>
        $"시도 {Attempts}회, 성공 {Successes}회 ({SuccessRatePercent:F1}%), 최대 끊김 {MaxGapMs}ms, 평균 응답 {AvgLatencyMs:F0}ms, 최대 응답 {MaxLatencyMs}ms, UID {DistinctUids.Count}종";
}

/// <summary>
/// 태그를 정위치에 두고 일정 시간 동안 반복 읽기 → 인식률과 최대 끊김 시간을 측정.
/// 읽기 거리와 상판 두께 조정을 수치로 판단하기 위한 도구.
/// </summary>
public static class ReadRateTester
{
    public static async Task<ReadRateResult> RunAsync(
        IRfidReaderProvider provider,
        string readerName,
        TimeSpan duration,
        int intervalMs,
        bool reverseIso15693,
        IProgress<ReadRateProgress>? progress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int attempts = 0, successes = 0;
        long maxGap = 0, gapStart = -1, latencySum = 0, maxLatency = 0;
        var uids = new HashSet<string>();
        string lastUid = "";

        while (sw.Elapsed < duration && !ct.IsCancellationRequested)
        {
            attempts++;
            var t0 = sw.ElapsedMilliseconds;
            TagReadResult? r = null;
            try { r = await Task.Run(() => provider.ReadTag(readerName), ct); }
            catch (OperationCanceledException) { break; }
            catch { r = null; }
            var t1 = sw.ElapsedMilliseconds;

            if (r is not null)
            {
                successes++;
                var lat = t1 - t0;
                latencySum += lat;
                if (lat > maxLatency) maxLatency = lat;
                lastUid = UidFormatter.Format(r.Uid, r.Tech.Family, reverseIso15693);
                uids.Add(lastUid);
                if (gapStart >= 0)
                {
                    var gap = t1 - gapStart;
                    if (gap > maxGap) maxGap = gap;
                    gapStart = -1;
                }
            }
            else if (gapStart < 0)
            {
                gapStart = t0;
            }

            var currentGap = gapStart >= 0 ? t1 - gapStart : 0;
            progress?.Report(new ReadRateProgress(attempts, successes, t1, currentGap, Math.Max(maxGap, currentGap), lastUid));

            var rest = intervalMs - (int)(sw.ElapsedMilliseconds - t0);
            if (rest > 0)
            {
                try { await Task.Delay(rest, ct); } catch (OperationCanceledException) { break; }
            }
        }

        if (gapStart >= 0) maxGap = Math.Max(maxGap, sw.ElapsedMilliseconds - gapStart);

        return new ReadRateResult(
            readerName, attempts, successes,
            attempts == 0 ? 0 : successes * 100.0 / attempts,
            maxGap,
            successes == 0 ? 0 : (double)latencySum / successes,
            maxLatency,
            uids.ToList(),
            sw.ElapsedMilliseconds);
    }
}
