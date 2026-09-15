using System.Globalization;
using Microsoft.Data.Sqlite;
using OtogeTrainer.Core.Library;
using OtogeTrainer.Core.Playback;

namespace OtogeTrainer.Data;

/// <summary>
/// <see cref="IVideoLibrary"/> 의 SQLite 구현.
/// <para>
/// 작업마다 연결을 열고 닫는다(풀링됨). 연결을 오래 쥐고 있지 않으므로 스레드를 가리지 않는다.
/// 모든 쓰기는 호출이 끝나는 시점에 커밋되어 있다(NFR-08).
/// </para>
/// </summary>
public sealed class SqliteVideoLibrary(LibraryDatabase db, TimeProvider? clock = null) : IVideoLibrary
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private const string VideoColumns =
        "id, fingerprint, path, title, file_size, duration_ms, fps, added_at, " +
        "last_played_at, play_count, last_position_ms, av_offset_ms, last_speed";

    public async Task<VideoRecord> OpenOrRegisterAsync(
        string path, string fingerprint, long fileSize, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var existing = await QuerySingleAsync(conn, tx, "WHERE fingerprint = $fp", ct, ("$fp", fingerprint));

        if (existing is null)
        {
            await using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT INTO videos (fingerprint, path, title, file_size, added_at) " +
                "VALUES ($fp, $path, $title, $size, $now) RETURNING id;";
            insert.Parameters.AddWithValue("$fp", fingerprint);
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$title", DefaultTitle(path));
            insert.Parameters.AddWithValue("$size", fileSize);
            insert.Parameters.AddWithValue("$now", Now());
            var id = (long)(await insert.ExecuteScalarAsync(ct))!;

            existing = await QuerySingleAsync(conn, tx, "WHERE id = $id", ct, ("$id", id));
        }
        else if (!string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            // 파일이 옮겨졌다. 북마크 · 오프셋은 그대로 두고 경로만 따라간다. (FR-105 의 기반)
            // 제목이 옛 파일명 그대로라면 새 파일명으로 바꾼다. 사용자가 고친 제목은 보존한다.
            var retitle = existing.Title == DefaultTitle(existing.Path);

            await using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = retitle
                ? "UPDATE videos SET path = $path, title = $title WHERE id = $id;"
                : "UPDATE videos SET path = $path WHERE id = $id;";
            update.Parameters.AddWithValue("$path", path);
            update.Parameters.AddWithValue("$title", DefaultTitle(path));
            update.Parameters.AddWithValue("$id", existing.Id);
            await update.ExecuteNonQueryAsync(ct);

            existing = existing with { Path = path, Title = retitle ? DefaultTitle(path) : existing.Title };
        }

        await tx.CommitAsync(ct);
        return existing!;
    }

    public async Task<VideoRecord?> GetAsync(long videoId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await QuerySingleAsync(conn, null, "WHERE id = $id", ct, ("$id", videoId));
    }

    public async Task<IReadOnlyList<RecentVideo>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {VideoColumns}, " +
            "  (SELECT COUNT(*) FROM bookmarks b WHERE b.video_id = videos.id AND b.slot IS NOT NULL) " +
            "FROM videos WHERE last_played_at IS NOT NULL " +
            "ORDER BY last_played_at DESC, id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<RecentVideo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new RecentVideo(ReadVideo(reader), reader.GetInt32(13)));
        return list;
    }

    public Task MarkPlayedAsync(long videoId, DateTimeOffset at, CancellationToken ct = default)
        => ExecuteAsync(
            "UPDATE videos SET last_played_at = $at, play_count = play_count + 1 WHERE id = $id;", ct,
            ("$at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)), ("$id", videoId));

    public Task UpdateMediaInfoAsync(long videoId, long durationMs, double? fps, CancellationToken ct = default)
        => ExecuteAsync(
            "UPDATE videos SET duration_ms = $d, fps = $fps WHERE id = $id;", ct,
            ("$d", durationMs), ("$fps", (object?)fps ?? DBNull.Value), ("$id", videoId));

    public Task SavePlaybackStateAsync(long videoId, long positionMs, double speed, CancellationToken ct = default)
        => ExecuteAsync(
            "UPDATE videos SET last_position_ms = $pos, last_speed = $speed WHERE id = $id;", ct,
            ("$pos", Math.Max(0, positionMs)), ("$speed", SpeedRules.Normalize(speed)), ("$id", videoId));

    public Task SaveOffsetAsync(long videoId, int offsetMs, CancellationToken ct = default)
        => ExecuteAsync(
            "UPDATE videos SET av_offset_ms = $ms WHERE id = $id;", ct,
            ("$ms", OffsetRules.Clamp(offsetMs)), ("$id", videoId));

    public async Task<IReadOnlyList<Bookmark>> GetBookmarksAsync(long videoId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT slot, position_ms, label FROM bookmarks " +
            "WHERE video_id = $id AND slot IS NOT NULL ORDER BY slot;";
        cmd.Parameters.AddWithValue("$id", videoId);

        var list = new List<Bookmark>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new Bookmark(reader.GetInt32(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return list;
    }

    public Task SetBookmarkAsync(long videoId, int slot, long positionMs, CancellationToken ct = default)
    {
        BookmarkSlots.EnsureValid(slot);

        // 같은 슬롯에 다시 저장하면 위치만 바꾼다. 라벨(FR-324)은 지우지 않는다.
        return ExecuteAsync(
            "INSERT INTO bookmarks (video_id, slot, position_ms, created_at) VALUES ($id, $slot, $pos, $now) " +
            "ON CONFLICT (video_id, slot) DO UPDATE SET position_ms = excluded.position_ms;", ct,
            ("$id", videoId), ("$slot", slot), ("$pos", Math.Max(0, positionMs)), ("$now", Now()));
    }

    public Task ClearBookmarkAsync(long videoId, int slot, CancellationToken ct = default)
    {
        BookmarkSlots.EnsureValid(slot);
        return ExecuteAsync(
            "DELETE FROM bookmarks WHERE video_id = $id AND slot = $slot;", ct,
            ("$id", videoId), ("$slot", slot));
    }

    public Task RemoveAsync(long videoId, CancellationToken ct = default)
        => ExecuteAsync("DELETE FROM videos WHERE id = $id;", ct, ("$id", videoId));

    // ── 보조 ─────────────────────────────────────────────────────────────────

    private string Now() => _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static string DefaultTitle(string path) => System.IO.Path.GetFileNameWithoutExtension(path);

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<VideoRecord?> QuerySingleAsync(
        SqliteConnection conn, SqliteTransaction? tx, string where, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {VideoColumns} FROM videos {where} LIMIT 1;";
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVideo(reader) : null;
    }

    private static VideoRecord ReadVideo(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Fingerprint = r.GetString(1),
        Path = r.GetString(2),
        Title = r.GetString(3),
        FileSize = r.GetInt64(4),
        DurationMs = r.IsDBNull(5) ? null : r.GetInt64(5),
        Fps = r.IsDBNull(6) ? null : r.GetDouble(6),
        AddedAt = ParseTime(r.GetString(7)),
        LastPlayedAt = r.IsDBNull(8) ? null : ParseTime(r.GetString(8)),
        PlayCount = r.GetInt32(9),
        LastPositionMs = r.GetInt64(10),
        AvOffsetMs = r.IsDBNull(11) ? null : r.GetInt32(11),
        LastSpeed = r.IsDBNull(12) ? null : r.GetDouble(12),
    };

    private static DateTimeOffset ParseTime(string s)
        => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
