using CommunityToolkit.Mvvm.ComponentModel;
using RfidReaderMonitor.Readers;

namespace RfidReaderMonitor.ViewModels;

public sealed partial class ReaderItemViewModel : ObservableObject
{
    public ReaderItemViewModel(string name)
    {
        Name = name;
        Key = "name:" + name;
    }

    /// <summary>PC/SC 리더 이름.</summary>
    public string Name { get; }

    /// <summary>설정 저장 키. S/N 이 있으면 S/N, 없으면 name:이름.</summary>
    [ObservableProperty] private string _key;

    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _serial = "";
    [ObservableProperty] private string _serialSource = "";
    [ObservableProperty] private string _vendor = "";
    [ObservableProperty] private string _ifdType = "";
    [ObservableProperty] private string _ifdVersion = "";
    [ObservableProperty] private string _firmware = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StateText))] [NotifyPropertyChangedFor(nameof(IsOnline))]
    private PresenceState _state = PresenceState.Unknown;

    [ObservableProperty] private string _uid = "";
    [ObservableProperty] private string _uidLength = "";
    [ObservableProperty] private string _tech = "";
    [ObservableProperty] private string _atr = "";
    [ObservableProperty] private DateTimeOffset? _lastChange;
    [ObservableProperty] private int _appearCount;
    [ObservableProperty] private string _lastError = "";

    // 장치 매핑
    [ObservableProperty] private string _deviceInstanceId = "";
    [ObservableProperty] private string _portPath = "";
    [ObservableProperty] private string _driverService = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EscapeText))] private bool? _escapeEnabled;

    // ACS 에스케이프 결과
    [ObservableProperty] private string _pollingText = "";
    [ObservableProperty] private byte _pollingValue;
    [ObservableProperty] private string _piccParamText = "";
    [ObservableProperty] private string _ledBuzzerText = "";

    public string StateText => State.Ko();
    public bool IsOnline => State is not (PresenceState.Removed or PresenceState.Unavailable or PresenceState.Unknown);
    public string EscapeText => EscapeEnabled switch { true => "설정됨", false => "미설정 (재연결 필요할 수 있음)", null => "확인 불가" };
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : $"{Alias}  ({Name})";

    partial void OnAliasChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}
