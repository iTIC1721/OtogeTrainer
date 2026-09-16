using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtogeTrainer.Core.Library;
using OtogeTrainer.Core.Playback;

namespace OtogeTrainer.App.Library;

/// <summary>폴더 관리 창. 등록 · 해제 · 폴더별 기본 오프셋. (FR-104 · FR-405)</summary>
public sealed partial class FoldersViewModel(IVideoLibrary library) : ObservableObject
{
    public ObservableCollection<FolderItem> Folders { get; } = [];

    [ObservableProperty] private string? _notice;

    public bool IsEmpty => Folders.Count == 0;

    /// <summary>창이 연결한다. 해제 전에 사용자에게 묻는다.</summary>
    public Func<FolderItem, bool>? ConfirmRemove { get; set; }

    /// <summary>창이 연결한다. 폴더 고르기 대화상자.</summary>
    public Func<string?>? PickFolder { get; set; }

    public async Task LoadAsync()
    {
        Folders.Clear();
        foreach (var folder in await library.GetFoldersAsync())
            Folders.Add(new FolderItem(folder));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task Add()
    {
        if (PickFolder?.Invoke() is not { } path) return;
        await AddPathAsync(path);
    }

    public async Task AddPathAsync(string path)
    {
        var existing = Folders.Any(f => string.Equals(f.Path, FolderRules.Normalize(path), StringComparison.OrdinalIgnoreCase));
        var folder = await library.AddFolderAsync(path);
        await LoadAsync();
        Notice = existing
            ? $"이미 등록된 폴더입니다. {folder.Path}"
            : $"등록했습니다. 창을 닫으면 폴더 안의 영상을 읽어 목록에 올립니다. {folder.Path}";
    }

    [RelayCommand]
    private async Task Remove(FolderItem item)
    {
        if (ConfirmRemove is not null && !ConfirmRemove(item)) return;
        await RemoveAsync(item);
    }

    public async Task RemoveAsync(FolderItem item)
    {
        var removed = await library.RemoveFolderAsync(item.Id);
        await LoadAsync();
        Notice = removed > 0
            ? $"등록을 해제했습니다. 한 번도 열지 않은 영상 {removed}개가 목록에서 빠졌습니다 (파일은 그대로)."
            : "등록을 해제했습니다. 목록의 영상은 그대로 둡니다 (파일도 그대로).";
    }

    [RelayCommand]
    private Task AdjustOffset(FolderOffsetStep step)
        => SetOffsetAsync(step.Folder, (step.Folder.DefaultOffsetMs ?? 0) + step.DeltaMs);

    [RelayCommand]
    private Task ClearOffset(FolderItem item) => SetOffsetAsync(item, null);

    public async Task SetOffsetAsync(FolderItem item, int? offsetMs)
    {
        var value = offsetMs is { } ms ? OffsetRules.Clamp(ms) : (int?)null;
        await library.SetFolderDefaultOffsetAsync(item.Id, value);
        item.DefaultOffsetMs = value;
        Notice = null;
    }
}

/// <summary>오프셋 버튼 한 개. 버튼마다 폴더와 증감량을 함께 넘기려고 묶는다.</summary>
public sealed record FolderOffsetStep(FolderItem Folder, int DeltaMs);

public sealed partial class FolderItem : ObservableObject
{
    public FolderItem(LibraryFolder folder)
    {
        Id = folder.Id;
        Path = folder.Path;
        _defaultOffsetMs = folder.DefaultOffsetMs;
        LastScanText = folder.LastScanAt is { } at ? $"마지막 확인 {at.ToLocalTime():yyyy-MM-dd HH:mm}" : "아직 읽지 않음";
        Steps = [new(this, -5), new(this, -1), new(this, 1), new(this, 5)];
    }

    public long Id { get; }
    public string Path { get; }
    public string LastScanText { get; }
    public bool Exists => System.IO.Directory.Exists(Path);
    public IReadOnlyList<FolderOffsetStep> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffsetText), nameof(HasOffset))]
    private int? _defaultOffsetMs;

    public bool HasOffset => DefaultOffsetMs is not null;

    public string OffsetText => DefaultOffsetMs switch
    {
        null => "지정 안 함",
        0 => "0 ms",
        > 0 => $"+{DefaultOffsetMs} ms",
        _ => $"{DefaultOffsetMs} ms",
    };
}
