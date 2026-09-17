using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OtogeTrainer.Core.Playback;

namespace OtogeTrainer.App.Settings;

/// <summary>
/// 오프셋 프리셋 — 자주 쓰는 오프셋 값에 이름을 붙여 두고 영상에 한 번에 적용한다. (D-9)
/// <para>
/// 적용은 그 순간의 <b>복사</b>다. 나중에 프리셋 값을 고쳐도 이미 적용한 영상은 바뀌지 않는다 —
/// 폴더 기본 오프셋을 없앤 이유(상속이 관리하기 어려움)를 되풀이하지 않기 위해서다.
/// </para>
/// <para>앱 전체에 하나. 입력 가드의 키 배치처럼 설정 창 · 플레이어 · 셸이 같은 객체를 본다.</para>
/// </summary>
public sealed class OffsetPresetList
{
    public const int MaxCount = 8;
    public const int MaxNameLength = 20;

    public ObservableCollection<OffsetPreset> Items { get; } = [];

    /// <summary>추가 · 삭제 · 이름 · 값이 바뀌었다. 셸이 저장한다.</summary>
    public event EventHandler? Changed;

    public bool CanAdd => Items.Count < MaxCount;

    public OffsetPreset? Add(string? name, int offsetMs)
    {
        if (!CanAdd) return null;
        var preset = new OffsetPreset(NormalizeName(name, Items.Count + 1), OffsetRules.Clamp(offsetMs));
        Attach(preset);
        Items.Add(preset);
        Changed?.Invoke(this, EventArgs.Empty);
        return preset;
    }

    public void Remove(OffsetPreset preset)
    {
        if (!Items.Remove(preset)) return;
        preset.PropertyChanged -= OnPresetChanged;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string NormalizeName(string? name, int number)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length > MaxNameLength) trimmed = trimmed[..MaxNameLength].TrimEnd();
        return trimmed.Length == 0 ? $"프리셋 {number}" : trimmed;
    }

    public static OffsetPresetList FromSerializable(IEnumerable<OffsetPresetSettings>? saved)
    {
        var list = new OffsetPresetList();
        foreach (var item in (saved ?? []).Take(MaxCount))
        {
            var preset = new OffsetPreset(NormalizeName(item.Name, list.Items.Count + 1), OffsetRules.Clamp(item.OffsetMs));
            list.Attach(preset);
            list.Items.Add(preset);
        }
        return list;
    }

    public List<OffsetPresetSettings> ToSerializable()
        => Items.Select((p, i) => new OffsetPresetSettings { Name = NormalizeName(p.Name, i + 1), OffsetMs = p.OffsetMs }).ToList();

    private void Attach(OffsetPreset preset) => preset.PropertyChanged += OnPresetChanged;

    private void OnPresetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OffsetPreset.Name) or nameof(OffsetPreset.OffsetMs))
            Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class OffsetPreset(string name, int offsetMs) : ObservableObject
{
    [ObservableProperty] private string _name = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffsetText))]
    private int _offsetMs = offsetMs;

    public string OffsetText => OffsetMs switch
    {
        0 => "0 ms",
        > 0 => $"+{OffsetMs} ms",
        _ => $"{OffsetMs} ms",
    };

    partial void OnOffsetMsChanged(int value)
    {
        var clamped = OffsetRules.Clamp(value);
        if (clamped != value) OffsetMs = clamped;
    }
}

/// <summary><c>settings.json</c> 에 저장되는 모양.</summary>
public sealed class OffsetPresetSettings
{
    public string Name { get; set; } = "";
    public int OffsetMs { get; set; }
}
