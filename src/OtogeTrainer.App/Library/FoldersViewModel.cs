using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtogeTrainer.Core.Library;

namespace OtogeTrainer.App.Library;

/// <summary>녹화 폴더 관리 창. 등록 · 해제. (FR-104)</summary>
public sealed partial class FoldersViewModel(IVideoLibrary library) : ObservableObject
{
    public ObservableCollection<FolderItem> Folders { get; } = [];

    [ObservableProperty] private string? _notice;

    public bool IsEmpty => Folders.Count == 0;

    /// <summary>창이 연결한다. 해제 전에 사용자에게 묻는다.</summary>
    public Func<FolderItem, bool>? ConfirmRemove { get; set; }

    /// <summary>창이 연결한다. 폴더 고르기 대화상자.</summary>
    public Func<string?>? PickFolder { get; set; }

    /// <summary>창이 연결한다. (디렉토리 구조, 지금 대상) → 고른 곳. 취소면 null.</summary>
    public Func<DirectoryTree, long?, DirectoryPick?>? PickTargetDirectory { get; set; }

    public DirectoryTree Tree { get; private set; } = new([]);

    public async Task LoadAsync()
    {
        Tree = new DirectoryTree((await library.GetDirectoriesAsync()).Select(e => e.Directory));
        Folders.Clear();
        foreach (var folder in await library.GetFoldersAsync())
            Folders.Add(new FolderItem(folder, Tree));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>새 영상을 넣을 디렉토리 바꾸기. 이미 들어온 영상은 옮기지 않는다. (D-10)</summary>
    [RelayCommand]
    private async Task ChangeTarget(FolderItem item)
    {
        if (PickTargetDirectory?.Invoke(Tree, item.TargetDirectoryId) is not { } pick) return;
        await SetTargetAsync(item, pick.DirectoryId);
    }

    public async Task SetTargetAsync(FolderItem item, long? directoryId)
    {
        await library.SetFolderTargetDirectoryAsync(item.Id, directoryId);
        await LoadAsync();
        Notice = $"이제 이 폴더에서 새로 찾은 영상은 '{Tree.DisplayPath(directoryId)}' 에 들어갑니다. 이미 들어온 영상은 그대로 둡니다.";
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
}

public sealed partial class FolderItem : ObservableObject
{
    public FolderItem(LibraryFolder folder, DirectoryTree tree)
    {
        Id = folder.Id;
        Path = folder.Path;
        TargetDirectoryId = folder.TargetDirectoryId;
        TargetText = tree.DisplayPath(folder.TargetDirectoryId);
        LastScanText = folder.LastScanAt is { } at ? $"마지막 확인 {at.ToLocalTime():yyyy-MM-dd HH:mm}" : "아직 읽지 않음";
    }

    public long Id { get; }
    public string Path { get; }
    public string LastScanText { get; }
    public long? TargetDirectoryId { get; }

    /// <summary>새 영상이 들어갈 곳의 경로.</summary>
    public string TargetText { get; }
    public bool Exists => System.IO.Directory.Exists(Path);
}
