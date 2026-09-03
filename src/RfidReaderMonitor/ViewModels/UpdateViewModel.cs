using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfidReaderMonitor.Sys;
using RfidReaderMonitor.Views;
using Serilog;

namespace RfidReaderMonitor.ViewModels;

/// <summary>업데이트 대화창 뷰모델.</summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly ReleaseInfo _release;
    private readonly IReadOnlyList<string> _restartArgs;
    private CancellationTokenSource? _cts;

    public UpdateViewModel(ReleaseInfo release, IReadOnlyList<string> restartArgs)
    {
        _release = release;
        _restartArgs = restartArgs;
        CurrentText = "v" + UpdateService.Current.ToString(3);
        NewText = release.Tag;
        Title = string.IsNullOrWhiteSpace(release.Name) ? release.Tag : release.Name;
        Notes = string.IsNullOrWhiteSpace(release.Notes) ? "(릴리스 노트 없음)" : release.Notes;
        SizeText = release.ExeSize > 0 ? $"{release.ExeSize / 1024.0 / 1024.0:F1} MB" : "";
        PublishedText = release.Published?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
        if (!release.HasExe) Error = "이 릴리스에는 실행 파일 자산이 없습니다.";
        else if (!release.HasChecksum) Error = "이 릴리스에는 sha256 자산이 없어 무결성 확인이 불가능합니다. 업데이트를 막습니다.";
    }

    public string CurrentText { get; }
    public string NewText { get; }
    public string Title { get; }
    public string Notes { get; }
    public string SizeText { get; }
    public string PublishedText { get; }
    public string HtmlUrl => _release.HtmlUrl;
    public bool CanUpdate => _release.HasExe && _release.HasChecksum && !Busy && !Applied;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUpdate))] private bool _busy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUpdate))] private bool _applied;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _error = "";

    public event Action? CloseRequested;

    [RelayCommand]
    private async Task Update()
    {
        if (!CanUpdate) return;
        Busy = true;
        Error = "";
        _cts = new CancellationTokenSource();
        var newPath = UpdateService.CurrentExePath + ".new";
        try
        {
            ProgressText = "다운로드 중…";
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                ProgressText = $"다운로드 중… {p * 100:F0}%";
            });
            await UpdateService.DownloadAsync(_release, newPath, progress, _cts.Token);
            ProgressText = "검증 완료. 프로그램을 다시 시작합니다…";
            UpdateService.ApplyAndRestart(newPath, _restartArgs);
            Applied = true;
            CloseRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            ProgressText = "취소됨";
            try { System.IO.File.Delete(newPath); } catch { }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "업데이트 실패");
            Error = ex.Message;
            ProgressText = "";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        if (!Busy) CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        try { Process.Start(new ProcessStartInfo(HtmlUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Error = ex.Message; }
    }
}

/// <summary>"업데이트 확인" 버튼의 공통 흐름. 감시 모드와 수집 모드가 함께 쓴다.</summary>
public static class UpdateFlow
{
    private static bool _running;

    public static async Task RunAsync(Action<string> status)
    {
        if (_running) return;
        _running = true;
        try
        {
            status("업데이트 확인 중…");
            ReleaseInfo release;
            try
            {
                release = await UpdateService.GetLatestAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                status("업데이트 확인 실패: " + ex.Message);
                return;
            }

            if (release.Version <= UpdateService.Current)
            {
                status($"현재 최신 버전입니다 (v{UpdateService.Current.ToString(3)}, 최신 릴리스 {release.Tag})");
                return;
            }

            var app = (App)Application.Current;
            var vm = new UpdateViewModel(release, app.RestartArgs);
            var dlg = new UpdateDialog(vm) { Owner = Application.Current.MainWindow?.IsVisible == true ? Application.Current.MainWindow : null };
            dlg.ShowDialog();

            if (vm.Applied)
            {
                status("업데이트 적용을 위해 종료합니다…");
                app.ExitApp();
            }
            else
            {
                status($"업데이트 보류 ({release.Tag} 사용 가능)");
            }
        }
        finally
        {
            _running = false;
        }
    }
}
