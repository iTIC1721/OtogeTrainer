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

    /// <summary>
    /// 영상별 오프셋. null 이면 상위(폴더/전역) 기본값을 상속한다(FR-405).
    /// M1 에는 상속이 없으므로 <see cref="EffectiveOffsetMs"/> 는 null 을 0 으로 본다.
    /// </summary>
    public int? AvOffsetMs { get; init; }

    public double? LastSpeed { get; init; }

    public int EffectiveOffsetMs => AvOffsetMs ?? 0;

    /// <summary>진행률 0~1. 길이를 모르면 0.</summary>
    public double Progress => DurationMs is > 0 ? Math.Clamp((double)LastPositionMs / DurationMs.Value, 0, 1) : 0;
}

/// <summary>북마크 한 칸. (FR-321)</summary>
public sealed record Bookmark(int Slot, long PositionMs, string? Label = null);

/// <summary>최근 목록 카드에 필요한 것. (FR-102)</summary>
public sealed record RecentVideo(VideoRecord Video, int BookmarkCount);

/// <summary>
/// 영상 라이브러리 저장소. 구현은 <c>OtogeTrainer.Data</c>.
/// <para>북마크와 오프셋은 변경 즉시 커밋한다. 비정상 종료로 잃으면 안 된다. (NFR-08)</para>
/// </summary>
public interface IVideoLibrary
{
    /// <summary>지문으로 찾고, 없으면 새로 등록한다. 찾았는데 경로가 달라졌으면 경로를 갱신한다.</summary>
    Task<VideoRecord> OpenOrRegisterAsync(string path, string fingerprint, long fileSize, CancellationToken ct = default);

    Task<VideoRecord?> GetAsync(long videoId, CancellationToken ct = default);

    /// <summary>최근 재생 순. (FR-102)</summary>
    Task<IReadOnlyList<RecentVideo>> GetRecentAsync(int limit, CancellationToken ct = default);

    Task MarkPlayedAsync(long videoId, DateTimeOffset at, CancellationToken ct = default);

    Task UpdateMediaInfoAsync(long videoId, long durationMs, double? fps, CancellationToken ct = default);

    Task SavePlaybackStateAsync(long videoId, long positionMs, double speed, CancellationToken ct = default);

    Task SaveOffsetAsync(long videoId, int offsetMs, CancellationToken ct = default);

    Task<IReadOnlyList<Bookmark>> GetBookmarksAsync(long videoId, CancellationToken ct = default);

    Task SetBookmarkAsync(long videoId, int slot, long positionMs, CancellationToken ct = default);

    Task ClearBookmarkAsync(long videoId, int slot, CancellationToken ct = default);

    /// <summary>목록에서만 제거한다. 파일은 건드리지 않는다. (FR-108)</summary>
    Task RemoveAsync(long videoId, CancellationToken ct = default);
}
