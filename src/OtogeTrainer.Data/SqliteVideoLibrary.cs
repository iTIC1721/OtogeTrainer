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
        "last_played_at, play_count, last_position_ms, av_offset_ms, last_speed, directory_id";

    public async Task<VideoRecord> OpenOrRegisterAsync(
        string path, string fingerprint, long fileSize, long? directoryForNew = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var existing = await QuerySingleAsync(conn, tx, "WHERE fingerprint = $fp", ct, ("$fp", fingerprint));

        if (existing is null)
        {
            await using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT INTO videos (fingerprint, path, title, file_size, added_at, directory_id) " +
                "VALUES ($fp, $path, $title, $size, $now, " +
                "        (SELECT id FROM directories WHERE id = $dir)) RETURNING id;";   // 지워진 디렉토리면 최상위로
            insert.Parameters.AddWithValue("$fp", fingerprint);
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$title", DefaultTitle(path));
            insert.Parameters.AddWithValue("$size", fileSize);
            insert.Parameters.AddWithValue("$now", Now());
            insert.Parameters.AddWithValue("$dir", (object?)directoryForNew ?? DBNull.Value);
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

    public Task<IReadOnlyList<LibraryItem>> GetRecentAsync(int limit, CancellationToken ct = default)
        => QueryItemsAsync(
            "WHERE last_played_at IS NOT NULL", "ORDER BY last_played_at DESC, id DESC", limit, [], ct);

    public Task<IReadOnlyList<LibraryItem>> QueryAsync(VideoQuery query, CancellationToken ct = default)
    {
        var conditions = new List<string>();
        var args = new List<(string Name, object Value)>();

        // 공백으로 나눈 낱말이 모두 제목이나 태그 어딘가에 있어야 한다. "iidx 트릴" → 둘 다 포함
        var words = (query.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            conditions.Add(
                $"(title LIKE $w{i} ESCAPE '\\' OR EXISTS (SELECT 1 FROM video_tags vt JOIN tags t ON t.id = vt.tag_id " +
                $"WHERE vt.video_id = videos.id AND t.name LIKE $w{i} ESCAPE '\\'))");
            args.Add(($"$w{i}", "%" + EscapeLike(words[i]) + "%"));
        }

        switch (query.Directory)
        {
            case null: break;
            case { Id: null, IncludeSubdirectories: true }: break;   // 최상위와 그 아래 = 전부
            case { Id: null }: conditions.Add("directory_id IS NULL"); break;
            case { Id: { } dir, IncludeSubdirectories: false }:
                conditions.Add("directory_id = $dir");
                args.Add(("$dir", dir));
                break;
            case { Id: { } dir }:
                conditions.Add(
                    "directory_id IN (WITH RECURSIVE sub(id) AS (SELECT $dir UNION ALL " +
                    "SELECT d.id FROM directories d JOIN sub ON d.parent_id = sub.id) SELECT id FROM sub)");
                args.Add(("$dir", dir));
                break;
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            conditions.Add(
                "EXISTS (SELECT 1 FROM video_tags vt JOIN tags t ON t.id = vt.tag_id " +
                "WHERE vt.video_id = videos.id AND t.name = $tag)");
            args.Add(("$tag", query.Tag.Trim()));
        }

        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        var order = query.Sort switch
        {
            VideoSort.Title => "ORDER BY title COLLATE NOCASE, id",
            VideoSort.RecentlyAdded => "ORDER BY added_at DESC, id DESC",
            VideoSort.MostPlayed => "ORDER BY play_count DESC, last_played_at DESC, id DESC",
            _ => "ORDER BY last_played_at IS NULL, last_played_at DESC, added_at DESC, id DESC",
        };

        return QueryItemsAsync(where, order, Math.Max(1, query.Limit), args, ct);
    }

    public async Task<IReadOnlyDictionary<string, long>> GetKnownFileSizesAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, file_size FROM videos;";

        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = reader.GetInt64(1);
        return map;
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

    public Task SaveOffsetAsync(long videoId, int? offsetMs, CancellationToken ct = default)
        => ExecuteAsync(
            "UPDATE videos SET av_offset_ms = $ms WHERE id = $id;", ct,
            ("$ms", offsetMs is { } ms ? OffsetRules.Clamp(ms) : DBNull.Value), ("$id", videoId));

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

    // ── 폴더 ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await ReadFoldersAsync(conn, null, "", ct);
    }

    public async Task<LibraryFolder> AddFolderAsync(string path, CancellationToken ct = default)
    {
        var normalized = FolderRules.Normalize(path);

        await using var conn = await db.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO folders (path, added_at) VALUES ($path, $now) ON CONFLICT (path) DO NOTHING;";
            cmd.Parameters.AddWithValue("$path", normalized);
            cmd.Parameters.AddWithValue("$now", Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var found = await ReadFoldersAsync(conn, null, "WHERE path = $path", ct, ("$path", normalized));
        return found[0];
    }

    public async Task<int> RemoveFolderAsync(long folderId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var all = await ReadFoldersAsync(conn, tx, "", ct);
        var target = all.FirstOrDefault(f => f.Id == folderId);
        if (target is null) return 0;
        var others = all.Where(f => f.Id != folderId).ToList();

        // 사용자가 손댄 흔적이 없는 영상만 후보. 재생 · 북마크 · 태그 · 오프셋 중 하나라도 있으면 남긴다
        var candidates = new List<(long Id, string Path)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "SELECT id, path FROM videos v " +
                "WHERE last_played_at IS NULL AND av_offset_ms IS NULL AND directory_id IS NULL " +
                "  AND NOT EXISTS (SELECT 1 FROM bookmarks b WHERE b.video_id = v.id) " +
                "  AND NOT EXISTS (SELECT 1 FROM video_tags vt WHERE vt.video_id = v.id);";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                candidates.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        // 경로 포함 판정은 SQL LIKE 대신 FolderRules 로 — 경계(D:\rec 와 D:\recording)와 대소문자를 한 곳에서 다룬다
        var removed = 0;
        foreach (var (id, videoPath) in candidates)
        {
            if (!FolderRules.Contains(target.Path, videoPath)) continue;
            if (others.Any(f => FolderRules.Contains(f.Path, videoPath))) continue;

            await using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM videos WHERE id = $id;";
            del.Parameters.AddWithValue("$id", id);
            removed += await del.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM folders WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", folderId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return removed;
    }

    public Task MarkFolderScannedAsync(long folderId, DateTimeOffset at, CancellationToken ct = default)
        => ExecuteAsync(
            "UPDATE folders SET last_scan_at = $at WHERE id = $id;", ct,
            ("$at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)), ("$id", folderId));

    public Task SetFolderTargetDirectoryAsync(long folderId, long? directoryId, CancellationToken ct = default)
        => ExecuteAsync(
            "UPDATE folders SET target_directory_id = (SELECT id FROM directories WHERE id = $dir) WHERE id = $id;", ct,
            ("$dir", (object?)directoryId ?? DBNull.Value), ("$id", folderId));

    // ── 디렉토리 (D-10) ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DirectoryEntry>> GetDirectoriesAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT d.id, d.parent_id, d.name, d.created_at, " +
            "       (SELECT COUNT(*) FROM videos v WHERE v.directory_id = d.id) " +
            "FROM directories d ORDER BY d.name COLLATE NOCASE;";

        var list = new List<DirectoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new DirectoryEntry(ReadDirectory(reader), reader.GetInt32(4)));
        return list;
    }

    public async Task<LibraryDirectory> CreateDirectoryAsync(long? parentId, string name, CancellationToken ct = default)
    {
        var normalized = DirectoryRules.NormalizeName(name);

        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var tree = new DirectoryTree(await ReadDirectoriesAsync(conn, tx, ct));

        if (!tree.Contains(parentId)) throw new LibraryRuleException("상위 디렉토리가 없습니다. 목록을 새로 고친 뒤 다시 시도하세요.");
        EnsureNameFree(tree, parentId, normalized, exceptId: null);

        await using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT INTO directories (parent_id, name, created_at) VALUES ($parent, $name, $now) RETURNING id;";
        insert.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$name", normalized);
        insert.Parameters.AddWithValue("$now", Now());
        var id = (long)(await insert.ExecuteScalarAsync(ct))!;

        await tx.CommitAsync(ct);
        return new LibraryDirectory(id, parentId, normalized, _clock.GetUtcNow());
    }

    public async Task RenameDirectoryAsync(long directoryId, string name, CancellationToken ct = default)
    {
        var normalized = DirectoryRules.NormalizeName(name);

        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var tree = new DirectoryTree(await ReadDirectoriesAsync(conn, tx, ct));
        var dir = tree.Find(directoryId) ?? throw MissingDirectory();

        EnsureNameFree(tree, dir.ParentId, normalized, exceptId: directoryId);
        await RunAsync(conn, tx, "UPDATE directories SET name = $name WHERE id = $id;", ct,
            ("$name", normalized), ("$id", directoryId));
        await tx.CommitAsync(ct);
    }

    public async Task MoveDirectoryAsync(long directoryId, long? newParentId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var tree = new DirectoryTree(await ReadDirectoriesAsync(conn, tx, ct));
        var dir = tree.Find(directoryId) ?? throw MissingDirectory();

        if (dir.ParentId == newParentId) return;
        if (!tree.Contains(newParentId)) throw MissingDirectory();
        if (newParentId is { } target && tree.SelfAndDescendants(directoryId).Contains(target))
            throw new LibraryRuleException($"'{dir.Name}' 을(를) 자기 자신이나 그 안의 디렉토리로 옮길 수 없습니다.");
        EnsureNameFree(tree, newParentId, dir.Name, exceptId: directoryId);

        await RunAsync(conn, tx, "UPDATE directories SET parent_id = $parent WHERE id = $id;", ct,
            ("$parent", (object?)newParentId ?? DBNull.Value), ("$id", directoryId));
        await tx.CommitAsync(ct);
    }

    public async Task DeleteDirectoryAsync(long directoryId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var tree = new DirectoryTree(await ReadDirectoriesAsync(conn, tx, ct));
        var dir = tree.Find(directoryId) ?? throw MissingDirectory();
        object parent = (object?)dir.ParentId ?? DBNull.Value;

        // 하위 디렉토리를 위로. 위에 같은 이름이 있으면 "이름 (2)"
        var siblingNames = tree.Children(dir.ParentId).Where(d => d.Id != directoryId).Select(d => d.Name).ToList();
        foreach (var child in tree.Children(directoryId))
        {
            var name = DirectoryRules.UniqueName(child.Name, siblingNames);
            siblingNames.Add(name);
            await RunAsync(conn, tx, "UPDATE directories SET parent_id = $parent, name = $name WHERE id = $id;", ct,
                ("$parent", parent), ("$name", name), ("$id", child.Id));
        }

        await RunAsync(conn, tx, "UPDATE videos SET directory_id = $parent WHERE directory_id = $id;", ct,
            ("$parent", parent), ("$id", directoryId));
        await RunAsync(conn, tx, "UPDATE folders SET target_directory_id = $parent WHERE target_directory_id = $id;", ct,
            ("$parent", parent), ("$id", directoryId));
        await RunAsync(conn, tx, "DELETE FROM directories WHERE id = $id;", ct, ("$id", directoryId));
        await tx.CommitAsync(ct);
    }

    public async Task MoveVideosAsync(IReadOnlyCollection<long> videoIds, long? directoryId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        if (directoryId is { } dir && new DirectoryTree(await ReadDirectoriesAsync(conn, tx, ct)).Find(dir) is null)
            throw MissingDirectory();

        foreach (var videoId in videoIds)
        {
            await RunAsync(conn, tx, "UPDATE videos SET directory_id = $dir WHERE id = $id;", ct,
                ("$dir", (object?)directoryId ?? DBNull.Value), ("$id", videoId));
        }
        await tx.CommitAsync(ct);
    }

    private static void EnsureNameFree(DirectoryTree tree, long? parentId, string name, long? exceptId)
    {
        if (tree.Children(parentId).Any(d => d.Id != exceptId && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new LibraryRuleException($"'{tree.DisplayPath(parentId)}' 에 이미 '{name}' 디렉토리가 있습니다.");
    }

    private static LibraryRuleException MissingDirectory()
        => new("디렉토리를 찾을 수 없습니다. 목록을 새로 고친 뒤 다시 시도하세요.");

    private static async Task<List<LibraryDirectory>> ReadDirectoriesAsync(
        SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, parent_id, name, created_at FROM directories;";

        var list = new List<LibraryDirectory>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(ReadDirectory(reader));
        return list;
    }

    private static LibraryDirectory ReadDirectory(SqliteDataReader r) => new(
        r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), ParseTime(r.GetString(3)));

    private static async Task RunAsync(
        SqliteConnection conn, SqliteTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── 태그 ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TagCount>> GetTagsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT t.name, COUNT(*) FROM tags t JOIN video_tags vt ON vt.tag_id = t.id " +
            "GROUP BY t.id ORDER BY t.name COLLATE NOCASE;";

        var list = new List<TagCount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new TagCount(reader.GetString(0), reader.GetInt32(1)));
        return list;
    }

    public async Task<IReadOnlyList<string>> GetVideoTagsAsync(long videoId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT t.name FROM tags t JOIN video_tags vt ON vt.tag_id = t.id " +
            "WHERE vt.video_id = $id ORDER BY t.name COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$id", videoId);

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    public async Task SetVideoTagsAsync(long videoId, IEnumerable<string> tags, CancellationToken ct = default)
    {
        var normalized = TagRules.Normalize(tags);

        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        async Task Run(string sql, params (string Name, object Value)[] args)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await Run("DELETE FROM video_tags WHERE video_id = $id;", ("$id", videoId));

        foreach (var tag in normalized)
        {
            // 이미 있는 태그면 처음 만든 표기를 쓴다 ("IIDX" 가 있으면 "iidx" 입력도 거기에 붙는다)
            await Run("INSERT INTO tags (name) VALUES ($name) ON CONFLICT (name) DO NOTHING;", ("$name", tag));
            await Run(
                "INSERT OR IGNORE INTO video_tags (video_id, tag_id) SELECT $id, id FROM tags WHERE name = $name;",
                ("$id", videoId), ("$name", tag));
        }

        await Run("DELETE FROM tags WHERE NOT EXISTS (SELECT 1 FROM video_tags vt WHERE vt.tag_id = tags.id);");
        await tx.CommitAsync(ct);
    }

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

    private async Task<IReadOnlyList<LibraryItem>> QueryItemsAsync(
        string where, string order, int limit, IEnumerable<(string Name, object Value)> args, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {VideoColumns}, " +
            "  (SELECT COUNT(*) FROM bookmarks b WHERE b.video_id = videos.id AND b.slot IS NOT NULL), " +
            "  (SELECT group_concat(t.name, char(31)) FROM video_tags vt JOIN tags t ON t.id = vt.tag_id " +
            "   WHERE vt.video_id = videos.id) " +
            $"FROM videos {where} {order} LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);

        var list = new List<LibraryItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string[] tags = reader.IsDBNull(15)
                ? []
                : reader.GetString(15).Split(TagSeparator).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            list.Add(new LibraryItem(ReadVideo(reader), reader.GetInt32(14), tags));
        }
        return list;
    }

    private const char TagSeparator = '';

    private static string EscapeLike(string text)
        => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static async Task<IReadOnlyList<LibraryFolder>> ReadFoldersAsync(
        SqliteConnection conn, SqliteTransaction? tx, string where, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"SELECT id, path, added_at, last_scan_at, target_directory_id FROM folders {where} " +
            "ORDER BY path COLLATE NOCASE;";
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);

        var list = new List<LibraryFolder>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LibraryFolder(
                reader.GetInt64(0),
                reader.GetString(1),
                ParseTime(reader.GetString(2)),
                reader.IsDBNull(3) ? null : ParseTime(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }
        return list;
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
        DirectoryId = r.IsDBNull(13) ? null : r.GetInt64(13),
    };

    private static DateTimeOffset ParseTime(string s)
        => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
