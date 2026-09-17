namespace OtogeTrainer.Core.Library;

/// <summary>라이브러리에 등록된 영상 한 편과 그 영상별 상태. (SPEC §8.2 videos)</summary>
public sealed record VideoRecord
{
    public required long Id { get; init; }

    /// <summary>경로가 바뀌어도 같은 영상을 알아보기 위한 식별자. (<see cref="FileFingerprint"/>)</summary>
    public required string Fingerprint { get; init; }

    /// <summary>마지막으로 확인된 경로.</summary>
    public required string Path { get; init; }

    public required string Title { get; init; }
    public required long FileSize { get; init; }
    public long? DurationMs { get; init; }
    public double? Fps { get; init; }
    public required DateTimeOffset AddedAt { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public int PlayCount { get; init; }
    public long LastPositionMs { get; init; }

    /// <summary>영상별 오프셋. null 이면 조정한 적 없음(= 0). 상속은 없다 (D-9).</summary>
    public int? AvOffsetMs { get; init; }

    public double? LastSpeed { get; init; }

    /// <summary>속한 디렉토리. null 이면 최상위. 파일 위치와 무관하다. (D-10)</summary>
    public long? DirectoryId { get; init; }

    /// <summary>진행률 0~1. 길이를 모르면 0.</summary>
    public double Progress => DurationMs is > 0 ? Math.Clamp((double)LastPositionMs / DurationMs.Value, 0, 1) : 0;
}

/// <summary>북마크 한 칸. (FR-321)</summary>
public sealed record Bookmark(int Slot, long PositionMs, string? Label = null);

/// <summary>목록 카드에 필요한 것. (FR-102 · FR-106 · FR-107)</summary>
public sealed record LibraryItem(VideoRecord Video, int BookmarkCount, IReadOnlyList<string> Tags);

/// <summary>등록된 녹화 폴더. (FR-104)</summary>
/// <param name="Path">정규화된 전체 경로. 끝에 구분자가 없다.</param>
/// <param name="TargetDirectoryId">이 폴더에서 새로 찾은 영상을 넣을 디렉토리. null 이면 최상위. (D-10)</param>
public sealed record LibraryFolder(
    long Id, string Path, DateTimeOffset AddedAt, DateTimeOffset? LastScanAt, long? TargetDirectoryId = null);

public sealed record TagCount(string Name, int Count);

public enum VideoSort
{
    /// <summary>최근 재생 순. 한 번도 재생하지 않은 영상은 뒤에, 최근 추가 순으로.</summary>
    RecentlyPlayed,
    Title,
    RecentlyAdded,
    MostPlayed,
}

/// <param name="Text">제목 또는 태그 이름에 포함된 글자. 대소문자 구분 없음.</param>
/// <param name="Tag">이 태그가 붙은 영상만.</param>
/// <param name="Directory">이 디렉토리 안의 영상만. null 이면 위치를 가리지 않는다.</param>
public sealed record VideoQuery(
    string? Text = null, string? Tag = null, VideoSort Sort = VideoSort.RecentlyPlayed, int Limit = 500,
    DirectoryFilter? Directory = null);

/// <summary>
/// 영상 라이브러리 저장소. 구현은 <c>OtogeTrainer.Data</c>.
/// <para>북마크 · 오프셋 · 태그는 변경 즉시 커밋한다. 비정상 종료로 잃으면 안 된다. (NFR-08)</para>
/// </summary>
public interface IVideoLibrary
{
    /// <summary>지문으로 찾고, 없으면 새로 등록한다. 찾았는데 경로가 달라졌으면 경로를 갱신한다.</summary>
    /// <param name="directoryForNew">새로 등록할 때만 넣을 디렉토리. 이미 있는 영상의 위치는 바꾸지 않는다.</param>
    Task<VideoRecord> OpenOrRegisterAsync(
        string path, string fingerprint, long fileSize, long? directoryForNew = null, CancellationToken ct = default);

    Task<VideoRecord?> GetAsync(long videoId, CancellationToken ct = default);

    /// <summary>한 번이라도 재생한 영상만, 최근 재생 순. (FR-102)</summary>
    Task<IReadOnlyList<LibraryItem>> GetRecentAsync(int limit, CancellationToken ct = default);

    /// <summary>검색 · 태그 필터 · 정렬. 재생하지 않은 영상도 포함한다. (FR-106)</summary>
    Task<IReadOnlyList<LibraryItem>> QueryAsync(VideoQuery query, CancellationToken ct = default);

    /// <summary>등록된 모든 영상의 경로 → 파일 크기. 스캔에서 이미 아는 파일을 건너뛰는 데 쓴다.</summary>
    Task<IReadOnlyDictionary<string, long>> GetKnownFileSizesAsync(CancellationToken ct = default);

    Task MarkPlayedAsync(long videoId, DateTimeOffset at, CancellationToken ct = default);

    Task UpdateMediaInfoAsync(long videoId, long durationMs, double? fps, CancellationToken ct = default);

    Task SavePlaybackStateAsync(long videoId, long positionMs, double speed, CancellationToken ct = default);

    /// <summary>영상별 오프셋. null 이면 지운다(= 0).</summary>
    Task SaveOffsetAsync(long videoId, int? offsetMs, CancellationToken ct = default);

    Task<IReadOnlyList<Bookmark>> GetBookmarksAsync(long videoId, CancellationToken ct = default);

    Task SetBookmarkAsync(long videoId, int slot, long positionMs, CancellationToken ct = default);

    Task ClearBookmarkAsync(long videoId, int slot, CancellationToken ct = default);

    /// <summary>목록에서만 제거한다. 파일은 건드리지 않는다. (FR-108)</summary>
    Task RemoveAsync(long videoId, CancellationToken ct = default);

    // ── 녹화 폴더 (FR-104) ──────────────────────────────────────────────────

    Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken ct = default);

    /// <summary>폴더를 등록한다. 이미 있으면 그것을 돌려준다.</summary>
    Task<LibraryFolder> AddFolderAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 폴더 등록을 해제한다. 파일은 건드리지 않는다.
    /// 그 폴더에서만 찾은 영상 중 <b>사용자 기록이 전혀 없는 것</b>(재생 · 북마크 · 태그 · 오프셋)만 목록에서 뺀다.
    /// </summary>
    /// <returns>목록에서 뺀 영상 수.</returns>
    Task<int> RemoveFolderAsync(long folderId, CancellationToken ct = default);

    Task MarkFolderScannedAsync(long folderId, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>이 녹화 폴더에서 새로 찾은 영상을 넣을 디렉토리. null 이면 최상위.</summary>
    Task SetFolderTargetDirectoryAsync(long folderId, long? directoryId, CancellationToken ct = default);

    // ── 디렉토리 (D-10) ─────────────────────────────────────────────────────
    // 규칙 위반(이름 중복 · 자기 안으로 이동 · 없는 디렉토리)은 LibraryRuleException.

    Task<IReadOnlyList<DirectoryEntry>> GetDirectoriesAsync(CancellationToken ct = default);

    Task<LibraryDirectory> CreateDirectoryAsync(long? parentId, string name, CancellationToken ct = default);

    Task RenameDirectoryAsync(long directoryId, string name, CancellationToken ct = default);

    Task MoveDirectoryAsync(long directoryId, long? newParentId, CancellationToken ct = default);

    /// <summary>
    /// 디렉토리를 지운다. <b>안의 영상과 하위 디렉토리는 한 단계 위로 올라간다</b> — 영상 기록은 사라지지 않는다.
    /// 위에 같은 이름이 있으면 <c>이름 (2)</c> 로 바꿔 올린다. 이 디렉토리를 대상으로 하던 녹화 폴더도 위를 가리키게 된다.
    /// </summary>
    Task DeleteDirectoryAsync(long directoryId, CancellationToken ct = default);

    /// <summary>영상들을 디렉토리로 옮긴다. null 이면 최상위.</summary>
    Task MoveVideosAsync(IReadOnlyCollection<long> videoIds, long? directoryId, CancellationToken ct = default);

    // ── 태그 (FR-107) ───────────────────────────────────────────────────────

    /// <summary>영상이 하나 이상 붙은 태그만, 이름 순.</summary>
    Task<IReadOnlyList<TagCount>> GetTagsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetVideoTagsAsync(long videoId, CancellationToken ct = default);

    /// <summary>영상의 태그를 통째로 바꾼다. <see cref="TagRules.Normalize"/> 가 적용된다.</summary>
    Task SetVideoTagsAsync(long videoId, IEnumerable<string> tags, CancellationToken ct = default);
}
