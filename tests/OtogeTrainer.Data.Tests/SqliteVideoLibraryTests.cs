using Microsoft.Data.Sqlite;
using OtogeTrainer.Core.Library;
using Xunit;

namespace OtogeTrainer.Data.Tests;

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class SqliteVideoLibraryTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otoge-db-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
    private LibraryDatabase _db = null!;
    private SqliteVideoLibrary _lib = null!;

    private string DbPath => Path.Combine(_dir, "library.db");

    public async Task InitializeAsync()
    {
        _db = new LibraryDatabase(DbPath);
        await _db.MigrateAsync();
        _lib = new SqliteVideoLibrary(_db, _clock);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();   // 풀링된 연결이 파일을 잡고 있으면 지워지지 않는다
        try { Directory.Delete(_dir, recursive: true); } catch { /* 임시 폴더 */ }
        return Task.CompletedTask;
    }

    private Task<VideoRecord> Register(string path = @"D:\rec\conflict_HARD.mp4", string fp = "100:aaa")
        => _lib.OpenOrRegisterAsync(path, fp, 100);

    // ── 마이그레이션 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task 새_DB는_최신_스키마로_올라간다()
        => Assert.Equal(LibraryDatabase.LatestVersion, await _db.GetVersionAsync());

    [Fact]
    public async Task 마이그레이션은_여러_번_불러도_안전하다()
    {
        var v = await Register();
        await _db.MigrateAsync();
        await _db.MigrateAsync();

        Assert.Equal(LibraryDatabase.LatestVersion, await _db.GetVersionAsync());
        Assert.NotNull(await _lib.GetAsync(v.Id));   // 재실행이 기존 데이터를 날리지 않는다
    }

    [Fact]
    public async Task 이_앱보다_새_버전의_DB는_열지_않는다()
    {
        await using (var conn = await _db.OpenAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version = 99;";
            await cmd.ExecuteNonQueryAsync();
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.MigrateAsync());
        Assert.Contains("v99", ex.Message);
    }

    // ── 등록 / 지문 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 처음_여는_영상은_파일명을_제목으로_등록된다()
    {
        var v = await Register();

        Assert.Equal("conflict_HARD", v.Title);
        Assert.Equal(0, v.LastPositionMs);
        Assert.Null(v.AvOffsetMs);
        Assert.Equal(0, v.EffectiveOffsetMs);
        Assert.Equal(_clock.Now, v.AddedAt);
    }

    [Fact]
    public async Task 같은_지문이면_같은_영상이다()
    {
        var a = await Register();
        var b = await Register();
        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public async Task 파일을_옮기면_경로를_따라가고_상태는_보존한다()
    {
        var a = await Register(@"D:\rec\conflict_HARD.mp4");
        await _lib.SetBookmarkAsync(a.Id, 1, 42_116);
        await _lib.SaveOffsetAsync(a.Id, 33);

        var moved = await Register(@"E:\archive\2026\conflict_HARD_best.mp4");

        Assert.Equal(a.Id, moved.Id);
        Assert.Equal(@"E:\archive\2026\conflict_HARD_best.mp4", moved.Path);
        Assert.Equal("conflict_HARD_best", moved.Title);
        Assert.Equal(33, moved.AvOffsetMs);
        Assert.Single(await _lib.GetBookmarksAsync(moved.Id));
    }

    // ── 최근 목록 (FR-102) ───────────────────────────────────────────────────

    [Fact]
    public async Task 한_번도_재생하지_않은_영상은_최근_목록에_없다()
    {
        await Register();
        Assert.Empty(await _lib.GetRecentAsync(50));
    }

    [Fact]
    public async Task 최근_목록은_최근_재생_순이며_개수_제한과_북마크_수를_반영한다()
    {
        var old = await Register(@"D:\a.mp4", "1:a");
        var mid = await Register(@"D:\b.mp4", "1:b");
        var recent = await Register(@"D:\c.mp4", "1:c");

        await _lib.MarkPlayedAsync(old.Id, _clock.Now.AddHours(-3));
        await _lib.MarkPlayedAsync(mid.Id, _clock.Now.AddHours(-2));
        await _lib.MarkPlayedAsync(recent.Id, _clock.Now.AddHours(-1));
        await _lib.SetBookmarkAsync(mid.Id, 1, 1000);
        await _lib.SetBookmarkAsync(mid.Id, 3, 3000);

        var list = await _lib.GetRecentAsync(2);

        Assert.Equal(["c", "b"], list.Select(r => r.Video.Title));
        Assert.Equal([0, 2], list.Select(r => r.BookmarkCount));
    }

    [Fact]
    public async Task 재생할_때마다_횟수가_오른다()
    {
        var v = await Register();
        await _lib.MarkPlayedAsync(v.Id, _clock.Now);
        await _lib.MarkPlayedAsync(v.Id, _clock.Now.AddMinutes(1));

        var reloaded = await _lib.GetAsync(v.Id);
        Assert.Equal(2, reloaded!.PlayCount);
        Assert.Equal(_clock.Now.AddMinutes(1), reloaded.LastPlayedAt);
    }

    // ── 상태 복원 (FR-103) ───────────────────────────────────────────────────

    [Fact]
    public async Task 위치_배속_오프셋_길이가_저장되고_다시_읽힌다()
    {
        var v = await Register();
        await _lib.UpdateMediaInfoAsync(v.Id, 261_000, 60);
        await _lib.SavePlaybackStateAsync(v.Id, 134_383, 0.5);
        await _lib.SaveOffsetAsync(v.Id, 33);

        var r = (await _lib.GetAsync(v.Id))!;

        Assert.Equal(261_000, r.DurationMs);
        Assert.Equal(60, r.Fps);
        Assert.Equal(134_383, r.LastPositionMs);
        Assert.Equal(0.5, r.LastSpeed);
        Assert.Equal(33, r.AvOffsetMs);
        Assert.Equal(134_383.0 / 261_000, r.Progress, precision: 6);
    }

    [Fact]
    public async Task 범위를_벗어난_값은_저장_시점에_정규화된다()
    {
        var v = await Register();
        await _lib.SaveOffsetAsync(v.Id, 5000);
        await _lib.SavePlaybackStateAsync(v.Id, -10, 99);

        var r = (await _lib.GetAsync(v.Id))!;
        Assert.Equal(1000, r.AvOffsetMs);
        Assert.Equal(0, r.LastPositionMs);
        Assert.Equal(4.0, r.LastSpeed);
    }

    // ── 북마크 (FR-321 / FR-322) ─────────────────────────────────────────────

    [Fact]
    public async Task 북마크를_저장_덮어쓰기_삭제할_수_있다()
    {
        var v = await Register();

        await _lib.SetBookmarkAsync(v.Id, 2, 1000);
        await _lib.SetBookmarkAsync(v.Id, 5, 5000);
        await _lib.SetBookmarkAsync(v.Id, 2, 2222);   // 덮어쓰기

        Assert.Equal(
            [new Bookmark(2, 2222), new Bookmark(5, 5000)],
            await _lib.GetBookmarksAsync(v.Id));

        await _lib.ClearBookmarkAsync(v.Id, 2);
        Assert.Equal([new Bookmark(5, 5000)], await _lib.GetBookmarksAsync(v.Id));
    }

    [Fact]
    public async Task 덮어써도_라벨은_보존된다()
    {
        var v = await Register();
        await _lib.SetBookmarkAsync(v.Id, 1, 1000);

        await using (var conn = await _db.OpenAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE bookmarks SET label = '라스트 트릴' WHERE slot = 1;";
            await cmd.ExecuteNonQueryAsync();
        }

        await _lib.SetBookmarkAsync(v.Id, 1, 9999);
        Assert.Equal(new Bookmark(1, 9999, "라스트 트릴"), (await _lib.GetBookmarksAsync(v.Id))[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task 잘못된_슬롯은_거부된다(int slot)
    {
        var v = await Register();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _lib.SetBookmarkAsync(v.Id, slot, 1));
    }

    [Fact]
    public async Task 영상마다_북마크가_따로다()
    {
        var a = await Register(@"D:\a.mp4", "1:a");
        var b = await Register(@"D:\b.mp4", "1:b");
        await _lib.SetBookmarkAsync(a.Id, 1, 111);
        await _lib.SetBookmarkAsync(b.Id, 1, 222);

        Assert.Equal(111, (await _lib.GetBookmarksAsync(a.Id))[0].PositionMs);
        Assert.Equal(222, (await _lib.GetBookmarksAsync(b.Id))[0].PositionMs);
    }

    // ── 삭제 (FR-108) ────────────────────────────────────────────────────────

    [Fact]
    public async Task 목록에서_제거하면_북마크도_함께_지워진다()
    {
        var v = await Register();
        await _lib.SetBookmarkAsync(v.Id, 1, 1000);

        await _lib.RemoveAsync(v.Id);

        Assert.Null(await _lib.GetAsync(v.Id));

        // 외래 키가 꺼져 있으면 고아 북마크가 남는다. 연결마다 켜는지 확인.
        await using var conn = await _db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM bookmarks;";
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    // ── 영속성 (NFR-08) ──────────────────────────────────────────────────────

    [Fact]
    public async Task 새_인스턴스로_다시_열어도_데이터가_남아_있다()
    {
        var v = await Register();
        await _lib.SetBookmarkAsync(v.Id, 3, 8133);
        await _lib.SaveOffsetAsync(v.Id, -25);

        // 앱 재시작을 흉내 낸다.
        SqliteConnection.ClearAllPools();
        var db2 = new LibraryDatabase(DbPath);
        await db2.MigrateAsync();
        var lib2 = new SqliteVideoLibrary(db2, _clock);

        Assert.Equal(-25, (await lib2.GetAsync(v.Id))!.AvOffsetMs);
        Assert.Equal(8133, (await lib2.GetBookmarksAsync(v.Id))[0].PositionMs);
    }
}
