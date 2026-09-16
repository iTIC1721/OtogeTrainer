using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtogeTrainer.Input;

namespace OtogeTrainer.App.Settings;

/// <summary>
/// 설정 창. 지금은 재생 중 동작 키만 있다. (D-8, FR-701 의 일부를 M1 로 당김)
/// <para>바꾸는 즉시 적용 · 저장된다. 확인 · 취소 버튼이 없다.</para>
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly PlaybackKeyMap _map;

    public SettingsViewModel(PlaybackKeyMap map)
    {
        _map = map;
        foreach (var action in KeyText.DisplayOrder)
            Bindings.Add(new KeyBindingItem(action));
        Refresh();
    }

    public ObservableCollection<KeyBindingItem> Bindings { get; } = [];

    /// <summary>키 입력을 기다리는 줄. 없으면 null.</summary>
    [ObservableProperty] private KeyBindingItem? _capturing;

    /// <summary>방금 한 변경에 대한 안내 (맞바꿈 · 지정 불가 등).</summary>
    [ObservableProperty] private string? _notice;

    public bool IsCapturing => Capturing is not null;

    partial void OnCapturingChanged(KeyBindingItem? oldValue, KeyBindingItem? newValue)
    {
        if (oldValue is not null) oldValue.IsCapturing = false;
        if (newValue is not null) newValue.IsCapturing = true;
        OnPropertyChanged(nameof(IsCapturing));
    }

    /// <summary>키 칸을 누르면 입력 대기. 같은 칸을 다시 누르면 취소.</summary>
    [RelayCommand]
    private void StartCapture(KeyBindingItem item)
    {
        Notice = null;
        Capturing = Capturing == item ? null : item;
    }

    public void CancelCapture() => Capturing = null;

    /// <summary>
    /// 입력 대기 중에 눌린 키를 받는다. 처리했으면 true.
    /// <para>ESC 는 지정할 수 없으므로 "취소" 로 쓴다.</para>
    /// </summary>
    public bool CompleteCapture(Key key)
    {
        if (Capturing is not { } item) return false;

        if (key == Key.Escape)
        {
            Capturing = null;
            Notice = "취소했습니다. ESC 는 일시정지 전용이라 지정할 수 없습니다.";
            return true;
        }

        if (!PlaybackKeyMap.IsBindable(key))
        {
            Notice = $"[{KeyText.Of(key)}] 지정할 수 없는 키입니다. 다른 키를 누르세요.";
            return true;   // 계속 입력 대기
        }

        var displaced = _map.Assign(item.Action, key);
        Capturing = null;
        Refresh();

        Notice = displaced is { } other
            ? $"[{KeyText.Of(key)}] 다른 동작이 쓰던 키라 서로 맞바꿨습니다.  {item.Label} = {KeyText.Of(key)}  ·  {KeyText.Label(other)} = {KeyText.Of(_map[other])}"
            : $"{item.Label} = {KeyText.Of(key)}";
        return true;
    }

    [RelayCommand]
    private void Unbind(KeyBindingItem item)
    {
        Capturing = null;
        _map.Unbind(item.Action);
        Refresh();
        Notice = $"[{item.Label}] 키를 해제했습니다. 재생 중에는 이 동작을 쓸 수 없습니다.";
    }

    [RelayCommand]
    private void ResetAll()
    {
        Capturing = null;
        _map.ResetToDefaults();
        Refresh();
        Notice = "기본 배치(F1 ~ F7)로 되돌렸습니다.";
    }

    private void Refresh()
    {
        foreach (var item in Bindings)
            item.Key = _map[item.Action];
    }
}

public sealed partial class KeyBindingItem(PlaybackAction action) : ObservableObject
{
    public PlaybackAction Action { get; } = action;
    public string Label { get; } = KeyText.Label(action);
    public string Description { get; } = KeyText.Description(action);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDisplay), nameof(IsUnbound), nameof(IsDefault))]
    private Key? _key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDisplay))]
    private bool _isCapturing;

    public string KeyDisplay => IsCapturing ? "키를 누르세요…" : KeyText.Of(Key);
    public bool IsUnbound => Key is null;
    public bool IsDefault => Key == PlaybackKeyMap.Defaults[Action];
}
