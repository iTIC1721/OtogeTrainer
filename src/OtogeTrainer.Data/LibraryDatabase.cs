using Microsoft.Data.Sqlite;

namespace OtogeTrainer.Data;

/// <summary>
/// 라이브러리 DB 파일과 스키마 버전을 관리한다.
/// <para>
/// 스키마 버전은 별도 테이블 대신 <c>PRAGMA user_version</c> 에 둔다. DB 헤더에 저장되어
/// 트랜잭션과 함께 롤백되므로, 마이그레이션이 중간에 실패해도 버전만 앞서 나가는 일이 없다.
/// </para>
/// </summary>
public sealed class LibraryDatabase
{
    private readonly string _connectionString;

    public LibraryDatabase(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>이 코드가 아는 가장 최신 스키마 버전.</summary>
    public static int LatestVersion => Migrations.All[^1].Version;

    /// <summary>
    /// 연결을 연다. 외래 키 제약은 연결마다 켜야 한다 — SQLite 기본값은 꺼져 있어서,
    /// 이걸 빠뜨리면 영상을 지워도 북마크가 고아로 남는다.
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000;";
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    /// <summary>
    /// 스키마를 최신으로 올린다. 앱 시작 시 한 번 호출한다.
    /// </summary>
    /// <exception cref="InvalidOperationException">DB 가 이 앱보다 새 버전으로 만들어진 경우.</exception>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);

        await using (var wal = conn.CreateCommand())
        {
            // WAL: 읽기와 쓰기가 서로 막지 않는다. 재생 중 위치 저장이 목록 조회를 막으면 안 된다.
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteNonQueryAsync(ct);
        }

        var current = await GetVersionAsync(conn, ct);

        if (current > LatestVersion)
        {
            // 새 버전 앱으로 연 DB 를 옛 버전이 건드리면 데이터가 깨질 수 있다.
            throw new InvalidOperationException(
                $"라이브러리 DB 가 이 앱보다 새 버전(v{current})으로 만들어졌습니다. " +
                $"이 앱은 v{LatestVersion} 까지만 지원합니다. 앱을 업데이트하세요. ({Path})");
        }

        foreach (var migration in Migrations.All.Where(m => m.Version > current))
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var ver = conn.CreateCommand())
            {
                ver.Transaction = tx;
                ver.CommandText = $"PRAGMA user_version = {migration.Version};";
                await ver.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
    }

    public async Task<int> GetVersionAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await GetVersionAsync(conn, ct);
    }

    private static async Task<int> GetVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
}

internal sealed record Migration(int Version, string Description, string Sql);

/// <summary>
/// 스키마 변경 이력. <b>이미 배포된 항목은 절대 고치지 않는다</b> — 새 항목을 뒤에 추가한다.
/// </summary>
internal static class Migrations
{
    public static readonly IReadOnlyList<Migration> All =
    [
        new(1, "초기 스키마 — 영상, 북마크 (M1)",
            """
            CREATE TABLE videos (
                id               INTEGER PRIMARY KEY,
                fingerprint      TEXT    NOT NULL UNIQUE,
                path             TEXT    NOT NULL,
                title            TEXT    NOT NULL,
                file_size        INTEGER NOT NULL,
                duration_ms      INTEGER,
                fps              REAL,
                added_at         TEXT    NOT NULL,
                last_played_at   TEXT,
                play_count       INTEGER NOT NULL DEFAULT 0,
                last_position_ms INTEGER NOT NULL DEFAULT 0,
                av_offset_ms     INTEGER,          -- NULL 이면 상위 기본값 상속 (FR-405, M2)
                last_speed       REAL
            );

            CREATE INDEX ix_videos_last_played ON videos (last_played_at DESC);

            -- slot 이 NULL 이면 슬롯 밖 추가 북마크 (FR-327, M3).
            -- UNIQUE 는 NULL 을 여러 개 허용하므로 슬롯은 영상당 하나씩, 추가 북마크는 무제한이 된다.
            CREATE TABLE bookmarks (
                id          INTEGER PRIMARY KEY,
                video_id    INTEGER NOT NULL REFERENCES videos (id) ON DELETE CASCADE,
                slot        INTEGER CHECK (slot IS NULL OR slot BETWEEN 1 AND 5),
                position_ms INTEGER NOT NULL,
                label       TEXT,
                created_at  TEXT    NOT NULL,
                UNIQUE (video_id, slot)
            );
            """),
    ];
}
