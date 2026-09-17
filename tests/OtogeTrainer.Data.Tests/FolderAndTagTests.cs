using Microsoft.Data.Sqlite;
using OtogeTrainer.Core.Library;
using Xunit;

namespace OtogeTrainer.Data.Tests;

/// <summary>M2 — 폴더 · 검색 · 태그 · 스캔. (FR-104 · 106 · 107 · 405)</summary>
public sealed class FolderAndTagTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otoge-lib-" + Guid.NewGuid().ToString("N"));
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
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 임시 폴더 */ }
        return Task.CompletedTask;
    }

    private int _seq;

    private async Task<VideoRecord> Register(string path)
    {
        _clock.Now = _clock.Now.AddMinutes(1);   // 추가 순서를 구분할 수 있게
        return await _lib.OpenOrRegisterAsync(path, $"100:{++_seq}", 100);
    }

    private async Task<string[]> Titles(VideoQuery query)
        => (await _lib.QueryAsync(query)).Select(i => i.Video.Title).ToArray();

    // ── 마이그레이션 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task v1_DB를_올려도_기존_데이터가_남는다()
    {
        var path = Path.Combine(_dir, "v1.db");

        // v1 스키마만 있는 DB 를 만든다 — 실제 M1 사용자의 DB
        await using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Migrations.All[0].Sql + """
                PRAGMA user_version = 1;
                INSERT INTO videos (fingerprint, path, title, file_size, added_at, av_offset_ms)
                VALUES ('1:a', 'D:\rec\a.mp4', 'a', 1, '2026-09-01T00:00:00.0000000+00:00', 33);
                INSERT INTO bookmarks (video_id, slot, position_ms, created_at)
                VALUES (1, 2, 5000, '2026-09-01T00:00:00.0000000+00:00');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var db = new LibraryDatabase(path);
        await db.MigrateAsync();
        var lib = new SqliteVideoLibrary(db, _clock);

        Assert.Equal(LibraryDatabase.LatestVersion, await db.GetVersionAsync());
        Assert.Equal(33, (await lib.GetAsync(1))!.AvOffsetMs);
        Assert.Equal(5000, (await lib.GetBookmarksAsync(1))[0].PositionMs);
        Assert.Empty(await lib.GetFoldersAsync());
        await lib.SetVideoTagsAsync(1, ["iidx"]);
        Assert.Equal(["iidx"], await lib.GetVideoTagsAsync(1));
    }

    // ── 검색 · 정렬 (FR-106) ─────────────────────────────────────────────────

    [Fact]
    public async Task 조회는_재생하지_않은_영상도_포함하고_기본은_최근_재생_순이다()
    {
        var a = await Register(@"D:\rec\alpha.mp4");
        await Register(@"D:\rec\beta.mp4");
        var c = await Register(@"D:\rec\gamma.mp4");
        await _lib.MarkPlayedAsync(a.Id, _clock.Now.AddHours(1));
        await _lib.MarkPlayedAsync(c.Id, _clock.Now.AddHours(2));

        // 재생한 것 최근 순 → 재생 안 한 것
        Assert.Equal(["gamma", "alpha", "beta"], await Titles(new VideoQuery()));
    }

    [Fact]
    public async Task 정렬_기준을_바꿀_수_있다()
    {
        var b = await Register(@"D:\rec\Beta.mp4");
        await Register(@"D:\rec\alpha.mp4");
        var c = await Register(@"D:\rec\gamma.mp4");
        await _lib.MarkPlayedAsync(c.Id, _clock.Now);
        await _lib.MarkPlayedAsync(c.Id, _clock.Now);
        await _lib.MarkPlayedAsync(b.Id, _clock.Now);

        Assert.Equal(["alpha", "Beta", "gamma"], await Titles(new VideoQuery(Sort: VideoSort.Title)));
        Assert.Equal(["gamma", "alpha", "Beta"], await Titles(new VideoQuery(Sort: VideoSort.RecentlyAdded)));
        Assert.Equal(["gamma", "Beta", "alpha"], await Titles(new VideoQuery(Sort: VideoSort.MostPlayed)));
    }

    [Fact]
    public async Task 제목이나_태그로_검색하고_낱말은_모두_맞아야_한다()
    {
        var a = await Register(@"D:\rec\conflict_HARD.mp4");
        var b = await Register(@"D:\rec\conflict_EASY.mp4");
        await Register(@"D:\rec\other.mp4");
        await _lib.SetVideoTagsAsync(a.Id, ["트릴"]);
        await _lib.SetVideoTagsAsync(b.Id, ["계단"]);

        Assert.Equal(2, (await Titles(new VideoQuery("CONFLICT"))).Length);   // 대소문자 무시
        Assert.Equal(["conflict_HARD"], await Titles(new VideoQuery("트릴")));
        Assert.Equal(["conflict_EASY"], await Titles(new VideoQuery("conflict 계단")));
        Assert.Empty(await Titles(new VideoQuery("other 트릴")));
        Assert.Equal(3, (await Titles(new VideoQuery("   "))).Length);
    }

    [Fact]
    public async Task 검색어의_와일드카드_문자는_글자_그대로_찾는다()
    {
        await Register(@"D:\rec\100%_clear.mp4");
        await Register(@"D:\rec\100x clear.mp4");

        Assert.Equal(["100%_clear"], await Titles(new VideoQuery("100%_")));
    }

    // ── 태그 (FR-107) ────────────────────────────────────────────────────────

    [Fact]
    public async Task 태그를_붙이고_필터하고_목록에_함께_실린다()
    {
        var a = await Register(@"D:\rec\a.mp4");
        var b = await Register(@"D:\rec\b.mp4");
        await _lib.SetVideoTagsAsync(a.Id, ["sdvx", " iidx ", "IIDX"]);
        await _lib.SetVideoTagsAsync(b.Id, ["iidx"]);

        Assert.Equal(["iidx", "sdvx"], await _lib.GetVideoTagsAsync(a.Id));
        Assert.Equal([new TagCount("iidx", 2), new TagCount("sdvx", 1)], await _lib.GetTagsAsync());
        Assert.Equal(["a"], await Titles(new VideoQuery(Tag: "SDVX")));

        var item = (await _lib.QueryAsync(new VideoQuery(Sort: VideoSort.Title)))[0];
        Assert.Equal(["iidx", "sdvx"], item.Tags);
    }

    [Fact]
    public async Task 태그는_통째로_바뀌고_아무도_안_쓰는_태그는_사라진다()
    {
        var a = await Register(@"D:\rec\a.mp4");
        await _lib.SetVideoTagsAsync(a.Id, ["old", "keep"]);
        await _lib.SetVideoTagsAsync(a.Id, ["keep", "new"]);

        Assert.Equal(["keep", "new"], await _lib.GetVideoTagsAsync(a.Id));
        Assert.DoesNotContain(await _lib.GetTagsAsync(), t => t.Name == "old");

        await _lib.SetVideoTagsAsync(a.Id, []);
        Assert.Empty(await _lib.GetTagsAsync());
    }

    [Fact]
    public async Task 기존_태그와_대소문자만_다르면_기존_표기를_쓴다()
    {
        var a = await Register(@"D:\rec\a.mp4");
        var b = await Register(@"D:\rec\b.mp4");
        await _lib.SetVideoTagsAsync(a.Id, ["IIDX"]);
        await _lib.SetVideoTagsAsync(b.Id, ["iidx"]);

        Assert.Equal([new TagCount("IIDX", 2)], await _lib.GetTagsAsync());
    }

    [Fact]
    public async Task 영상을_지우면_태그_연결도_지워진다()
    {
        var a = await Register(@"D:\rec\a.mp4");
        await _lib.SetVideoTagsAsync(a.Id, ["iidx"]);
        await _lib.RemoveAsync(a.Id);

        Assert.Empty(await _lib.GetTagsAsync());
    }

    // ── 폴더 (FR-104 · FR-405) ───────────────────────────────────────────────

    [Fact]
    public async Task 폴더는_정규화된_경로로_한_번만_등록된다()
    {
        var a = await _lib.AddFolderAsync(@"D:\rec\");
        var b = await _lib.AddFolderAsync(@"d:\REC");

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(@"D:\rec", a.Path);
        Assert.Single(await _lib.GetFoldersAsync());
    }

    [Fact]
    public async Task v2_DB의_폴더_기본_오프셋은_물려받던_영상에_굳혀진다()
    {
        var path = Path.Combine(_dir, "v2.db");

        await using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Migrations.All[0].Sql + Migrations.All[1].Sql + """
                PRAGMA user_version = 2;
                INSERT INTO folders (path, default_av_offset_ms, added_at) VALUES
                    ('D:\rec', 33, '2026-09-01T00:00:00.0000000+00:00'),
                    ('D:\rec\iidx', 10, '2026-09-01T00:00:00.0000000+00:00'),
                    ('D:\plain', NULL, '2026-09-01T00:00:00.0000000+00:00'),
                    ('E:\', 7, '2026-09-01T00:00:00.0000000+00:00');
                INSERT INTO videos (fingerprint, path, title, file_size, added_at, av_offset_ms) VALUES
                    ('1:a', 'D:\rec\a.mp4', 'a', 1, '2026-09-01T00:00:00.0000000+00:00', NULL),
                    ('1:b', 'd:\REC\iidx\b.mp4', 'b', 1, '2026-09-01T00:00:00.0000000+00:00', NULL),
                    ('1:c', 'D:\rec\c.mp4', 'c', 1, '2026-09-01T00:00:00.0000000+00:00', -20),
                    ('1:d', 'D:\recording\d.mp4', 'd', 1, '2026-09-01T00:00:00.0000000+00:00', NULL),
                    ('1:e', 'D:\plain\e.mp4', 'e', 1, '2026-09-01T00:00:00.0000000+00:00', NULL),
                    ('1:f', 'E:\f.mp4', 'f', 1, '2026-09-01T00:00:00.0000000+00:00', NULL);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var db = new LibraryDatabase(path);
        await db.MigrateAsync();
        var lib = new SqliteVideoLibrary(db, _clock);

        async Task<int?> Offset(string title)
            => (await lib.QueryAsync(new VideoQuery(Sort: VideoSort.Title))).Single(i => i.Video.Title == title).Video.AvOffsetMs;

        Assert.Equal(33, await Offset("a"));
        Assert.Equal(10, await Offset("b"));     // 가장 가까운 폴더 · 대소문자 무시
        Assert.Equal(-20, await Offset("c"));    // 영상별 값은 그대로
        Assert.Null(await Offset("d"));          // 이름 앞부분만 같은 다른 폴더
        Assert.Null(await Offset("e"));          // 기본값 없는 폴더
        Assert.Equal(7, await Offset("f"));      // 드라이브 루트
        Assert.Equal(4, (await lib.GetFoldersAsync()).Count);
    }

    [Fact]
    public async Task 영상별_오프셋을_지울_수_있다()
    {
        var v = await Register(@"D:\rec\a.mp4");
        await _lib.SaveOffsetAsync(v.Id, 20);
        await _lib.SaveOffsetAsync(v.Id, null);

        Assert.Null((await _lib.GetAsync(v.Id))!.AvOffsetMs);
    }

    [Fact]
    public async Task 폴더를_해제하면_손대지_않은_영상만_목록에서_빠진다()
    {
        var folder = await _lib.AddFolderAsync(@"D:\rec");
        await _lib.AddFolderAsync(@"D:\rec\keep");

        var untouched = await Register(@"D:\rec\untouched.mp4");
        var played = await Register(@"D:\rec\played.mp4");
        var bookmarked = await Register(@"D:\rec\bookmarked.mp4");
        var tagged = await Register(@"D:\rec\tagged.mp4");
        var offset = await Register(@"D:\rec\offset.mp4");
        var zeroOffset = await Register(@"D:\rec\zero.mp4");
        var otherFolder = await Register(@"D:\rec\keep\other.mp4");      // 다른 등록 폴더에도 속한다
        var lookalike = await Register(@"D:\recording\lookalike.mp4");  // 경로 앞부분만 같다

        await _lib.MarkPlayedAsync(played.Id, _clock.Now);
        await _lib.SetBookmarkAsync(bookmarked.Id, 1, 1000);
        await _lib.SetVideoTagsAsync(tagged.Id, ["x"]);
        await _lib.SaveOffsetAsync(offset.Id, 33);
        await _lib.SaveOffsetAsync(zeroOffset.Id, 0);

        Assert.Equal(1, await _lib.RemoveFolderAsync(folder.Id));

        Assert.Null(await _lib.GetAsync(untouched.Id));
        foreach (var kept in new[] { played, bookmarked, tagged, offset, zeroOffset, otherFolder, lookalike })
            Assert.NotNull(await _lib.GetAsync(kept.Id));
        Assert.Equal([@"D:\rec\keep"], (await _lib.GetFoldersAsync()).Select(f => f.Path));
    }

    // ── 스캔 ─────────────────────────────────────────────────────────────────

    private string MakeFile(string relative, int size = 64)
    {
        var path = Path.Combine(_dir, "videos", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        new Random(relative.GetHashCode()).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task 스캔은_하위_폴더까지_영상만_등록한다()
    {
        MakeFile("a.mp4");
        MakeFile(@"iidx\b.MKV");
        MakeFile(@"iidx\2026\c.webm");
        MakeFile("notes.txt");
        MakeFile("d.mp4.part");
        var hidden = MakeFile("hidden.mp4");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var folder = await _lib.AddFolderAsync(Path.Combine(_dir, "videos"));
        var result = await new LibraryScanner(_lib, _clock).ScanAsync(folder);

        Assert.Equal((3, 3, 0, 0, false), (result.Found, result.Registered, result.Skipped, result.Failed, result.FolderMissing));
        Assert.Equal(["a", "b", "c"], await Titles(new VideoQuery(Sort: VideoSort.Title)));
        Assert.Equal(_clock.Now, (await _lib.GetFoldersAsync())[0].LastScanAt);
    }

    [Fact]
    public async Task 다시_스캔하면_아는_파일은_건너뛰고_바뀐_파일만_다시_읽는다()
    {
        MakeFile("a.mp4");
        var b = MakeFile("b.mp4");
        var folder = await _lib.AddFolderAsync(Path.Combine(_dir, "videos"));
        var scanner = new LibraryScanner(_lib, _clock);
        await scanner.ScanAsync(folder);

        MakeFile("c.mp4");
        File.WriteAllBytes(b, new byte[200]);   // 크기가 바뀌었다 — 녹화를 이어 쓴 경우 등

        var again = await scanner.ScanAsync(folder);
        Assert.Equal((3, 2, 1), (again.Found, again.Registered, again.Skipped));
    }

    [Fact]
    public async Task 옮긴_파일은_스캔에서_같은_영상으로_알아본다()
    {
        var original = MakeFile("a.mp4");
        var folder = await _lib.AddFolderAsync(Path.Combine(_dir, "videos"));
        var scanner = new LibraryScanner(_lib, _clock);
        await scanner.ScanAsync(folder);
        var video = (await _lib.QueryAsync(new VideoQuery()))[0].Video;
        await _lib.SetBookmarkAsync(video.Id, 1, 1234);

        var moved = Path.Combine(_dir, "videos", "archive", "a.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(original, moved);
        await scanner.ScanAsync(folder);

        var items = await _lib.QueryAsync(new VideoQuery());
        Assert.Single(items);
        Assert.Equal(moved, items[0].Video.Path);
        Assert.Equal(1, items[0].BookmarkCount);
    }

    [Fact]
    public async Task 없는_폴더는_실패가_아니라_없음으로_알린다()
    {
        var folder = await _lib.AddFolderAsync(Path.Combine(_dir, "unplugged"));
        var result = await new LibraryScanner(_lib, _clock).ScanAsync(folder);

        Assert.True(result.FolderMissing);
        Assert.Null((await _lib.GetFoldersAsync())[0].LastScanAt);
    }
}
