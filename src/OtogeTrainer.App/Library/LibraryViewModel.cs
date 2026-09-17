using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtogeTrainer.Core.Library;
using OtogeTrainer.Core.Playback;

namespace OtogeTrainer.App.Library;

/// <summary>
/// 라이브러리(홈) 화면. 디렉토리를 오가며 영상을 찾고, 검색 · 태그 · 정렬로 좁힌다.
/// (FR-102 · FR-104 · FR-106 · FR-107 · D-10)
/// <para>
/// 검색 · 태그 필터가 없으면 <b>지금 디렉토리 바로 안</b>의 디렉토리와 영상을, 있으면
/// <b>지금 디렉토리와 그 아래 전체</b>에서 찾은 영상을 보여 준다.
/// </para>
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    public const int ListLimit = 500;

    /// <summary>검색어를 치는 동안 매 글자 조회하지 않도록 기다리는 시간.</summary>
    public static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IVideoLibrary _library;
    private readonly AppPaths _paths;
    private readonly TimeProvider _clock;
    private readonly LibraryScanner _scanner;

    private CancellationTokenSource? _searchDelay;
    private Task? _scanTask;
    private bool _rescanRequested;
    private int _refreshGeneration;
    private bool _suppressSearchRefresh;
    private bool _suppressDirectoryRefresh;

    public LibraryViewModel(IVideoLibrary library, AppPaths paths, TimeProvider? clock = null)
    {
        _library = library;
        _paths = paths;
        _clock = clock ?? TimeProvider.System;
        _scanner = new LibraryScanner(library, _clock);

        Sorts =
        [
            new SortOption(VideoSort.RecentlyPlayed, "최근 재생") { IsSelected = true },
            new SortOption(VideoSort.RecentlyAdded, "최근 추가"),
            new SortOption(VideoSort.Title, "제목"),
            new SortOption(VideoSort.MostPlayed, "많이 재생"),
        ];
    }

    public ObservableCollection<VideoCardItem> Videos { get; } = [];
    public ObservableCollection<DirectoryCardItem> Directories { get; } = [];
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    /// <summary>지금 보고 있는 디렉토리. null 이면 최상위.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    private long? _currentDirectoryId;

    /// <summary>디렉토리 안인데 아무것도 없다.</summary>
    [ObservableProperty] private bool _isDirectoryEmpty;

    public bool CanGoUp => CurrentDirectoryId is not null;

    /// <summary>마지막으로 읽은 디렉토리 구조.</summary>
    public DirectoryTree Tree { get; private set; } = new([]);
    public ObservableCollection<TagChip> Tags { get; } = [];
    public IReadOnlyList<SortOption> Sorts { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string? _selectedTag;
    [ObservableProperty] private VideoSort _sort = VideoSort.RecentlyPlayed;

    /// <summary>라이브러리에 영상이 하나도 없다 — 첫 실행 안내를 띄운다.</summary>
    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>영상은 있지만 검색 · 필터 결과가 없다.</summary>
    [ObservableProperty] private bool _hasNoResults;

    [ObservableProperty] private string _listHeader = "";
    [ObservableProperty] private string? _scanStatus;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private int _folderCount;

    public bool IsFiltered => !string.IsNullOrWhiteSpace(SearchText) || SelectedTag is not null;

    /// <summary>셸이 연결한다. 파일 경로를 받아 플레이어로 연다.</summary>
    public Func<string, Task>? OpenPath { get; set; }

    /// <summary>셸이 연결한다. 알림 배너에 메시지를 띄운다.</summary>
    public Action<string>? Notify { get; set; }

    /// <summary>셸이 연결한다. 태그 편집 창을 띄우고, 저장하면 새 태그 목록을 돌려준다.</summary>
    public Func<VideoCardItem, IReadOnlyList<string>, IReadOnlyList<string>?>? EditTagsDialog { get; set; }

    /// <summary>셸이 연결한다. 폴더 관리 창을 띄운다.</summary>
    public Action? FoldersDialog { get; set; }

    /// <summary>셸이 연결한다. (제목, 안내, 처음 값, 검사) → 입력한 이름 또는 취소 시 null.</summary>
    public Func<string, string, string, Func<string, string?>, string?>? PromptName { get; set; }

    /// <summary>셸이 연결한다. 확인 질문 → 예/아니오.</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>셸이 연결한다. (제목, 안내, 지금 위치, 고를 수 없는 디렉토리) → 고른 곳. 취소면 null.</summary>
    public Func<string, string, long?, IReadOnlySet<long>?, DirectoryPick?>? PickDirectory { get; set; }

    // ── 목록 ─────────────────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        var generation = ++_refreshGeneration;

        var entries = await _library.GetDirectoriesAsync();
        var tree = new DirectoryTree(entries.Select(e => e.Directory));
        if (!tree.Contains(CurrentDirectoryId))
        {
            // 보고 있던 디렉토리가 지워졌다 (다른 경로로). 최상위로
            _suppressDirectoryRefresh = true;   // 여기서 바로 이어서 읽으므로 변경 알림의 조회는 건너뛴다
            CurrentDirectoryId = null;
            _suppressDirectoryRefresh = false;
        }

        var filtered = IsFiltered;
        var query = new VideoQuery(SearchText, SelectedTag, Sort, ListLimit,
            new DirectoryFilter(CurrentDirectoryId, IncludeSubdirectories: filtered));
        var items = await _library.QueryAsync(query);
        var folders = await _library.GetFoldersAsync();
        var tags = await _library.GetTagsAsync();
        var anyVideo = items.Count > 0 || (await _library.QueryAsync(new VideoQuery(Limit: 1))).Count > 0;
        if (generation != _refreshGeneration) return;   // 그사이 검색어가 또 바뀌었다

        Tree = tree;
        var now = _clock.GetUtcNow();
        Videos.Clear();
        foreach (var item in items)
        {
            // 검색 결과는 여러 디렉토리에서 오므로 어디 있는지 적어 준다
            var location = filtered && item.Video.DirectoryId != CurrentDirectoryId ? tree.DisplayPath(item.Video.DirectoryId) : "";
            Videos.Add(new VideoCardItem(item, _paths.ThumbnailFor(item.Video.Fingerprint), now, location));
        }

        Directories.Clear();
        if (!filtered)
        {
            var direct = entries.ToDictionary(e => e.Directory.Id, e => e.VideoCount);
            foreach (var dir in tree.Children(CurrentDirectoryId))
            {
                var subtree = tree.SelfAndDescendants(dir.Id);
                Directories.Add(new DirectoryCardItem(
                    dir.Id, dir.Name, subtree.Sum(id => direct.GetValueOrDefault(id)), tree.Children(dir.Id).Count));
            }
        }

        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem(null, "라이브러리", CurrentDirectoryId is null));
        foreach (var dir in tree.PathTo(CurrentDirectoryId))
            Breadcrumbs.Add(new BreadcrumbItem(dir.Id, dir.Name, dir.Id == CurrentDirectoryId));

        // 선택한 태그가 더는 어떤 영상에도 없으면 필터를 푼다
        if (SelectedTag is { } selected && !tags.Any(t => string.Equals(t.Name, selected, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedTag = null;
            return;   // OnSelectedTagChanged 가 다시 조회한다
        }

        Tags.Clear();
        foreach (var tag in tags)
        {
            Tags.Add(new TagChip(tag.Name, tag.Count)
            {
                IsSelected = string.Equals(tag.Name, SelectedTag, StringComparison.OrdinalIgnoreCase),
            });
        }

        FolderCount = folders.Count;
        IsEmpty = !anyVideo && tree.All.Count == 0;
        HasNoResults = !IsEmpty && filtered && Videos.Count == 0;
        IsDirectoryEmpty = !IsEmpty && !filtered && Videos.Count == 0 && Directories.Count == 0;
        ListHeader = filtered
            ? CurrentDirectoryId is null ? $"검색 결과 {Videos.Count}" : $"검색 결과 {Videos.Count} · 이 디렉토리와 그 아래"
            : Directories.Count > 0 ? $"디렉토리 {Directories.Count} · 영상 {Videos.Count}" : $"영상 {Videos.Count}";
        OnPropertyChanged(nameof(IsFiltered));
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDelay?.Cancel();
        if (_suppressSearchRefresh) return;
        var cts = _searchDelay = new CancellationTokenSource();
        _ = DebouncedRefreshAsync(cts.Token);
    }

    private async Task DebouncedRefreshAsync(CancellationToken ct)
    {
        try { await Task.Delay(SearchDebounce, ct); }
        catch (TaskCanceledException) { return; }
        await RefreshAsync();
    }

    partial void OnSelectedTagChanged(string? value) => _ = RefreshAsync();

    partial void OnCurrentDirectoryIdChanged(long? value)
    {
        if (!_suppressDirectoryRefresh) _ = RefreshAsync();
    }

    partial void OnSortChanged(VideoSort value)
    {
        foreach (var option in Sorts) option.IsSelected = option.Sort == value;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void SelectSort(SortOption option) => Sort = option.Sort;

    /// <summary>태그 칩: 누르면 그 태그로 거르고, 다시 누르면 푼다.</summary>
    [RelayCommand]
    private void ToggleTag(TagChip chip)
        => SelectedTag = string.Equals(SelectedTag, chip.Name, StringComparison.OrdinalIgnoreCase) ? null : chip.Name;

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressSearchRefresh = true;   // 아래에서 바로 조회하므로 디바운스 조회는 건너뛴다
        SearchText = "";
        _suppressSearchRefresh = false;
        if (SelectedTag is null) _ = RefreshAsync();
        else SelectedTag = null;
    }

    [RelayCommand]
    private async Task OpenVideo(VideoCardItem item)
    {
        if (item.IsMissing)
        {
            // 다시 찾기(FR-105)는 아직. 지금은 무엇이 문제인지만 알린다.
            Notify?.Invoke($"파일을 찾을 수 없습니다 — 옮겨졌거나 삭제됐습니다.\n{item.Path}");
            return;
        }

        if (OpenPath is not null) await OpenPath(item.Path);
    }

    [RelayCommand]
    private async Task EditTags(VideoCardItem item)
    {
        if (EditTagsDialog?.Invoke(item, Tags.Select(t => t.Name).ToList()) is not { } tags) return;
        await SetTagsAsync(item, tags);
    }

    /// <summary>태그를 저장하고 목록을 다시 읽는다. 대화상자 없이 부를 수 있게 따로 둔다.</summary>
    public async Task SetTagsAsync(VideoCardItem item, IEnumerable<string> tags)
    {
        await _library.SetVideoTagsAsync(item.VideoId, tags);
        await RefreshAsync();
    }

    // ── 디렉토리 (D-10) ──────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenDirectory(DirectoryCardItem item) => CurrentDirectoryId = item.Id;

    [RelayCommand]
    private void NavigateTo(BreadcrumbItem item) => CurrentDirectoryId = item.Id;

    /// <summary>한 단계 위로. 최상위면 아무 일도 없다. (Backspace)</summary>
    [RelayCommand]
    public void GoUp()
    {
        if (CurrentDirectoryId is { } id) CurrentDirectoryId = Tree.Find(id)?.ParentId;
    }

    /// <summary>이 이름을 이 곳에 쓸 수 있는가. 문제가 있으면 안내 문구.</summary>
    public string? ValidateDirectoryName(long? parentId, string name, long? exceptId = null)
    {
        string normalized;
        try { normalized = DirectoryRules.NormalizeName(name); }
        catch (LibraryRuleException ex) { return ex.Message; }

        return Tree.Children(parentId).Any(d => d.Id != exceptId && string.Equals(d.Name, normalized, StringComparison.OrdinalIgnoreCase))
            ? $"이 곳에 이미 '{normalized}' 이(가) 있습니다."
            : null;
    }

    [RelayCommand]
    private async Task CreateDirectory()
    {
        var parent = CurrentDirectoryId;
        var initial = DirectoryRules.UniqueName(DirectoryRules.DefaultNewName, Tree.Children(parent).Select(d => d.Name));
        var name = PromptName?.Invoke(
            "새 디렉토리", $"{Tree.DisplayPath(parent)} 안에 만듭니다. 실제 파일이나 폴더는 만들지 않습니다.",
            initial, n => ValidateDirectoryName(parent, n));
        if (name is not null) await CreateDirectoryAsync(name);
    }

    public Task<bool> CreateDirectoryAsync(string name)
        => RunRuleAsync(() => _library.CreateDirectoryAsync(CurrentDirectoryId, name));

    [RelayCommand]
    private async Task RenameDirectory(DirectoryCardItem item)
    {
        var parent = Tree.Find(item.Id)?.ParentId;
        var name = PromptName?.Invoke("이름 바꾸기", $"'{item.Name}' 의 새 이름", item.Name,
            n => ValidateDirectoryName(parent, n, exceptId: item.Id));
        if (name is not null) await RenameDirectoryAsync(item, name);
    }

    public Task<bool> RenameDirectoryAsync(DirectoryCardItem item, string name)
        => RunRuleAsync(() => _library.RenameDirectoryAsync(item.Id, name));

    [RelayCommand]
    private async Task DeleteDirectory(DirectoryCardItem item)
    {
        var parentPath = Tree.DisplayPath(Tree.Find(item.Id)?.ParentId);
        var ok = Confirm?.Invoke(
            $"'{item.Name}' 디렉토리를 삭제할까요?\n\n" +
            $"안에 있는 영상 {item.VideoCount}개와 하위 디렉토리는 '{parentPath}' 로 올라갑니다.\n" +
            "영상 기록과 실제 파일은 지워지지 않습니다.") ?? true;
        if (ok) await DeleteDirectoryAsync(item);
    }

    public Task<bool> DeleteDirectoryAsync(DirectoryCardItem item)
        => RunRuleAsync(() => _library.DeleteDirectoryAsync(item.Id));

    [RelayCommand]
    private async Task MoveDirectoryTo(DirectoryCardItem item)
    {
        var pick = PickDirectory?.Invoke(
            "디렉토리 옮기기", $"'{item.Name}' 을(를) 어디로 옮길까요? 안의 영상과 디렉토리가 함께 옮겨집니다.",
            Tree.Find(item.Id)?.ParentId, Tree.SelfAndDescendants(item.Id));
        if (pick is not null) await MoveDirectoryAsync(item.Id, pick.DirectoryId);
    }

    public Task<bool> MoveDirectoryAsync(long directoryId, long? newParentId)
        => RunRuleAsync(() => _library.MoveDirectoryAsync(directoryId, newParentId));

    [RelayCommand]
    private async Task MoveVideoTo(VideoCardItem item)
    {
        var pick = PickDirectory?.Invoke(
            "디렉토리로 이동", $"'{item.Title}' 을(를) 어디로 옮길까요? 실제 파일 위치는 바뀌지 않습니다.",
            item.DirectoryId, null);
        if (pick is not null) await MoveVideoAsync(item.VideoId, pick.DirectoryId);
    }

    public Task<bool> MoveVideoAsync(long videoId, long? directoryId)
        => RunRuleAsync(() => _library.MoveVideosAsync([videoId], directoryId));

    /// <summary>규칙 위반은 알림 배너로 보여 주고 false. 성공하면 목록을 다시 읽는다.</summary>
    private async Task<bool> RunRuleAsync(Func<Task> action)
    {
        try
        {
            await action();
            await RefreshAsync();
            return true;
        }
        catch (LibraryRuleException ex)
        {
            Notify?.Invoke(ex.Message);
            await RefreshAsync();
            return false;
        }
    }

    // ── 폴더 · 스캔 (FR-104) ─────────────────────────────────────────────────

    [RelayCommand]
    private async Task ManageFolders()
    {
        FoldersDialog?.Invoke();
        await RefreshAsync();       // 폴더를 해제해 빠진 영상을 반영
        await ScanFoldersAsync();   // 새로 추가한 폴더를 읽는다
    }

    /// <summary>
    /// 등록된 폴더를 모두 훑는다. 이미 도는 중이면 끝난 뒤 한 번 더 돈다.
    /// 아는 파일은 건너뛰므로 두 번째부터는 금방 끝난다.
    /// </summary>
    public Task ScanFoldersAsync()
    {
        if (_scanTask is { IsCompleted: false })
        {
            _rescanRequested = true;
            return _scanTask;
        }

        return _scanTask = RunScanLoopAsync();
    }

    private async Task RunScanLoopAsync()
    {
        IsScanning = true;
        try
        {
            do
            {
                _rescanRequested = false;
                await ScanOnceAsync();
            } while (_rescanRequested);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task ScanOnceAsync()
    {
        var folders = await _library.GetFoldersAsync();
        if (folders.Count == 0)
        {
            ScanStatus = null;
            return;
        }

        int registered = 0, failed = 0;
        var missing = new List<string>();
        var lastRefresh = _clock.GetUtcNow();

        foreach (var folder in folders)
        {
            // Progress<T> 는 만든 스레드(UI)로 보고를 돌려보낸다
            var progress = new Progress<ScanProgress>(p =>
                ScanStatus = $"폴더 읽는 중… {Path.GetFileName(p.FolderPath)} · 영상 {p.Found}");

            // 지문 계산과 SQLite 는 동기 IO 가 섞여 있어 UI 스레드에서 돌리면 화면이 멈춘다
            var result = await Task.Run(() => _scanner.ScanAsync(folder, progress));

            registered += result.Registered;
            failed += result.Failed;
            if (result.FolderMissing) missing.Add(folder.Path);

            // 큰 폴더는 다 끝날 때까지 기다리지 않고 중간중간 목록에 올린다
            if (result.Registered > 0 && _clock.GetUtcNow() - lastRefresh > TimeSpan.FromSeconds(1))
            {
                await RefreshAsync();
                lastRefresh = _clock.GetUtcNow();
            }
        }

        var parts = new List<string>();
        if (registered > 0) parts.Add($"새 영상 {registered}개");
        if (failed > 0) parts.Add($"읽지 못한 파일 {failed}개");
        if (missing.Count > 0) parts.Add($"찾을 수 없는 폴더 {missing.Count}개 ({string.Join(", ", missing)})");
        ScanStatus = parts.Count == 0 ? null : "폴더 확인 — " + string.Join(" · ", parts);

        await RefreshAsync();
    }
}

/// <summary>고른 곳. <c>DirectoryId</c> 가 null 이면 최상위.</summary>
public sealed record DirectoryPick(long? DirectoryId);

/// <param name="VideoCount">이 디렉토리와 그 아래 모든 영상 수.</param>
public sealed record DirectoryCardItem(long Id, string Name, int VideoCount, int SubdirectoryCount)
{
    public string CountText => SubdirectoryCount > 0 ? $"영상 {VideoCount} · 디렉토리 {SubdirectoryCount}" : $"영상 {VideoCount}";
}

/// <param name="Id">null 이면 최상위(라이브러리).</param>
public sealed record BreadcrumbItem(long? Id, string Name, bool IsCurrent);

public sealed partial class SortOption(VideoSort sort, string label) : ObservableObject
{
    public VideoSort Sort { get; } = sort;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class TagChip(string name, int count) : ObservableObject
{
    public string Name { get; } = name;
    public int Count { get; } = count;
    public string Text => $"{Name}  {Count}";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>목록 카드 한 장. (UI-BLUEPRINT §3)</summary>
public sealed class VideoCardItem
{
    public VideoCardItem(LibraryItem source, string thumbnailPath, DateTimeOffset now, string locationText = "")
    {
        var v = source.Video;
        VideoId = v.Id;
        DirectoryId = v.DirectoryId;
        LocationText = locationText;
        Path = v.Path;
        Title = v.Title;
        TagList = source.Tags;
        IsMissing = !File.Exists(v.Path);

        DurationText = v.DurationMs is { } d ? Timecode.FormatShort(TimeSpan.FromMilliseconds(d)) : "";
        Progress = v.Progress;
        ProgressText = v.LastPlayedAt is null ? "" : v.Progress >= 0.95 ? "완료" : $"{v.Progress:P0}";
        LastPlayedText = v.LastPlayedAt is { } at ? Relative(now - at, at) : "재생 전";
        BookmarkText = source.BookmarkCount > 0 ? $"🔖{source.BookmarkCount}" : "";
        OffsetText = v.AvOffsetMs is { } ms and not 0 ? $"⏱{ms:+0;-0} ms" : "";
        SpeedText = v.LastSpeed is { } s && Math.Abs(s - 1.0) > 0.001 ? $"{s:0.##}x" : "";
        TagsText = string.Join("  ", source.Tags.Select(t => "#" + t));
        Thumbnail = IsMissing ? null : LoadThumbnail(thumbnailPath);
    }

    public long VideoId { get; }
    public long? DirectoryId { get; }

    /// <summary>검색 결과에서만: 이 영상이 있는 디렉토리 경로.</summary>
    public string LocationText { get; }
    public string Path { get; }
    public string Title { get; }
    public IReadOnlyList<string> TagList { get; }
    public bool IsMissing { get; }
    public string DurationText { get; }
    public double Progress { get; }
    public string ProgressText { get; }
    public string LastPlayedText { get; }
    public string BookmarkText { get; }
    public string OffsetText { get; }
    public string SpeedText { get; }
    public string TagsText { get; }
    public ImageSource? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail is not null;

    private static string Relative(TimeSpan ago, DateTimeOffset at) => ago switch
    {
        { TotalMinutes: < 1 } => "방금",
        { TotalHours: < 1 } => $"{(int)ago.TotalMinutes}분 전",
        { TotalDays: < 1 } => $"{(int)ago.TotalHours}시간 전",
        { TotalDays: < 7 } => $"{(int)ago.TotalDays}일 전",
        _ => at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// 썸네일은 플레이어를 떠날 때마다 같은 이름으로 덮어쓴다. 그래서
    /// <c>OnLoad</c>(파일을 잡고 있지 않음)와 <c>IgnoreImageCache</c>(URI 가 같아도 새로 읽음)가 둘 다 필요하다.
    /// </summary>
    private static BitmapImage? LoadThumbnail(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 480;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;   // 깨진 썸네일은 없는 셈 친다
        }
    }
}
