using Microsoft.Data.Sqlite;
using OtogeTrainer.Core.Library;
using Xunit;

namespace OtogeTrainer.Data.Tests;

/// <summary>디렉토리 — 파일 위치와 무관한 앱 안의 정리 단위. (D-10)</summary>
public sealed class DirectoryTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otoge-dir-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
    private LibraryDatabase _db = null!;
    private SqliteVideoLibrary _lib = null!;
    private int _seq;

    public async Task InitializeAsync()
    {
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        await _db.MigrateAsync();
        _lib = new SqliteVideoLibrary(_db, _clock);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 임시 폴더 */ }
        return Task.CompletedTask;
    }

    private Task<VideoRecord> Register(string title, long? directory = null)
        => _lib.OpenOrRegisterAsync($@"D:\rec\{title}.mp4", $"100:{++_seq}", 100, directory);

    private async Task<DirectoryTree> Tree()
        => new((await _lib.GetDirectoriesAsync()).Select(e => e.Directory));

    private async Task<string[]> TitlesIn(long? directory, bool recursive)
        => (await _lib.QueryAsync(new VideoQuery(Sort: VideoSort.Title, Directory: new DirectoryFilter(directory, recursive))))
            .Select(i => i.Video.Title).ToArray();

    [Fact]
    public async Task 중첩_디렉토리를_만들고_경로를_계산한다()
    {
        var games = await _lib.CreateDirectoryAsync(null, "  리듬게임 ");
        var iidx = await _lib.CreateDirectoryAsync(games.Id, "IIDX");
        var practice = await _lib.CreateDirectoryAsync(iidx.Id, "연습곡");

        var tree = await Tree();
        Assert.Equal("리듬게임", games.Name);
        Assert.Equal("라이브러리 › 리듬게임 › IIDX › 연습곡", tree.DisplayPath(practice.Id));
        Assert.Equal([iidx.Id], tree.Children(games.Id).Select(d => d.Id));
    }

    [Fact]
    public async Task 같은_곳에_같은_이름은_안_되고_다른_곳이면_된다()
    {
        var a = await _lib.CreateDirectoryAsync(null, "IIDX");
        var ex = await Assert.ThrowsAsync<LibraryRuleException>(() => _lib.CreateDirectoryAsync(null, "iidx"));
        Assert.Contains("이미", ex.Message);

        await _lib.CreateDirectoryAsync(a.Id, "IIDX");   // 안쪽에는 같은 이름 가능
        await Assert.ThrowsAsync<LibraryRuleException>(() => _lib.CreateDirectoryAsync(null, "   "));
    }

    [Fact]
    public async Task 이름을_바꾼다()
    {
        var a = await _lib.CreateDirectoryAsync(null, "sdvx");
        var b = await _lib.CreateDirectoryAsync(null, "IIDX");

        await _lib.RenameDirectoryAsync(a.Id, "SDVX");   // 대소문자만 바꾸기는 가능
        Assert.Equal("SDVX", (await Tree()).Find(a.Id)!.Name);
        await Assert.ThrowsAsync<LibraryRuleException>(() => _lib.RenameDirectoryAsync(b.Id, "sdvx"));
    }

    [Fact]
    public async Task 디렉토리를_옮기되_자기_안으로는_못_옮긴다()
    {
        var a = await _lib.CreateDirectoryAsync(null, "A");
        var b = await _lib.CreateDirectoryAsync(a.Id, "B");
        var c = await _lib.CreateDirectoryAsync(null, "C");

        await Assert.ThrowsAsync<LibraryRuleException>(() => _lib.MoveDirectoryAsync(a.Id, b.Id));
        await Assert.ThrowsAsync<LibraryRuleException>(() => _lib.MoveDirectoryAsync(a.Id, a.Id));

        await _lib.MoveDirectoryAsync(b.Id, c.Id);
        Assert.Equal("라이브러리 › C › B", (await Tree()).DisplayPath(b.Id));

        await _lib.CreateDirectoryAsync(null, "B");
        await Assert.ThrowsAsync<LibraryRuleException>(() => _lib.MoveDirectoryAsync(b.Id, null));   // 위에 같은 이름
    }

    [Fact]
    public async Task 영상은_한_곳에만_있고_옮길_수_있다()
    {
        var iidx = await _lib.CreateDirectoryAsync(null, "IIDX");
        var a = await Register("a");
        var b = await Register("b");

        await _lib.MoveVideosAsync([a.Id, b.Id], iidx.Id);
        Assert.Empty(await TitlesIn(null, recursive: false));
        Assert.Equal(["a", "b"], await TitlesIn(iidx.Id, recursive: false));

        await _lib.MoveVideosAsync([b.Id], null);
        Assert.Equal(["b"], await TitlesIn(null, recursive: false));
        Assert.Equal(iidx.Id, (await _lib.GetAsync(a.Id))!.DirectoryId);
        Assert.Equal(1, (await _lib.GetDirectoriesAsync()).Single().VideoCount);
    }

    [Fact]
    public async Task 하위_포함_조회와_검색은_아래_디렉토리까지_본다()
    {
        var games = await _lib.CreateDirectoryAsync(null, "리듬게임");
        var iidx = await _lib.CreateDirectoryAsync(games.Id, "IIDX");
        await Register("root");
        await Register("games", games.Id);
        await Register("iidx", iidx.Id);

        Assert.Equal(["games"], await TitlesIn(games.Id, recursive: false));
        Assert.Equal(["games", "iidx"], await TitlesIn(games.Id, recursive: true));
        Assert.Equal(["games", "iidx", "root"], await TitlesIn(null, recursive: true));
        Assert.Equal(["root"], await TitlesIn(null, recursive: false));

        var found = await _lib.QueryAsync(new VideoQuery("iidx", Directory: new DirectoryFilter(games.Id, true)));
        Assert.Equal(["iidx"], found.Select(i => i.Video.Title));
    }

    [Fact]
    public async Task 디렉토리를_지우면_내용이_위로_올라가고_이름이_겹치면_번호를_붙인다()
    {
        var games = await _lib.CreateDirectoryAsync(null, "리듬게임");
        var iidx = await _lib.CreateDirectoryAsync(games.Id, "IIDX");
        var inner = await _lib.CreateDirectoryAsync(iidx.Id, "연습");
        await _lib.CreateDirectoryAsync(games.Id, "연습");   // 위에 같은 이름이 이미 있다
        var video = await Register("v", iidx.Id);
        var folder = await _lib.AddFolderAsync(@"D:\rec");
        await _lib.SetFolderTargetDirectoryAsync(folder.Id, iidx.Id);

        await _lib.DeleteDirectoryAsync(iidx.Id);

        var tree = await Tree();
        Assert.Null(tree.Find(iidx.Id));
        Assert.Equal("라이브러리 › 리듬게임 › 연습 (2)", tree.DisplayPath(inner.Id));
        Assert.Equal(games.Id, (await _lib.GetAsync(video.Id))!.DirectoryId);          // 영상은 남는다
        Assert.Equal(games.Id, (await _lib.GetFoldersAsync())[0].TargetDirectoryId);   // 녹화 폴더도 위를 가리킨다
    }

    [Fact]
    public async Task 이미_있는_영상은_다시_열어도_위치가_바뀌지_않는다()
    {
        var a = await _lib.CreateDirectoryAsync(null, "A");
        var b = await _lib.CreateDirectoryAsync(null, "B");
        var v = await _lib.OpenOrRegisterAsync(@"D:\rec\x.mp4", "1:x", 1, a.Id);
        var again = await _lib.OpenOrRegisterAsync(@"E:\moved\x.mp4", "1:x", 1, b.Id);

        Assert.Equal(a.Id, v.DirectoryId);
        Assert.Equal(a.Id, again.DirectoryId);
    }

    [Fact]
    public async Task 디렉토리에_넣은_영상은_녹화_폴더를_해제해도_남는다()
    {
        var dir = await _lib.CreateDirectoryAsync(null, "정리함");
        var folder = await _lib.AddFolderAsync(@"D:\rec");
        var organized = await Register("organized", dir.Id);
        var loose = await Register("loose");

        Assert.Equal(1, await _lib.RemoveFolderAsync(folder.Id));
        Assert.NotNull(await _lib.GetAsync(organized.Id));
        Assert.Null(await _lib.GetAsync(loose.Id));
    }

    // ── 스캔: 녹화 폴더의 대상 디렉토리 ─────────────────────────────────────

    private string MakeFile(string relative)
    {
        var path = Path.Combine(_dir, "videos", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Guid.NewGuid().ToByteArray());
        return path;
    }

    [Fact]
    public async Task 스캔은_가장_깊은_녹화_폴더의_디렉토리에_넣는다()
    {
        MakeFile("a.mp4");
        MakeFile(@"iidx\b.mp4");
        MakeFile(@"plain\c.mp4");

        var games = await _lib.CreateDirectoryAsync(null, "리듬게임");
        var iidx = await _lib.CreateDirectoryAsync(games.Id, "IIDX");
        var root = await _lib.AddFolderAsync(Path.Combine(_dir, "videos"));
        var iidxFolder = await _lib.AddFolderAsync(Path.Combine(_dir, "videos", "iidx"));
        await _lib.SetFolderTargetDirectoryAsync(root.Id, games.Id);
        await _lib.SetFolderTargetDirectoryAsync(iidxFolder.Id, iidx.Id);

        // 상위 폴더부터 훑어도 하위 폴더에 지정한 곳으로 간다
        var scanner = new LibraryScanner(_lib, _clock);
        foreach (var folder in (await _lib.GetFoldersAsync()).OrderBy(f => f.Path.Length))
            await scanner.ScanAsync(folder);

        Assert.Equal(["a", "c"], await TitlesIn(games.Id, recursive: false));
        Assert.Equal(["b"], await TitlesIn(iidx.Id, recursive: false));
    }

    [Fact]
    public async Task v3_DB를_올리면_모든_영상이_최상위에_있다()
    {
        var path = Path.Combine(_dir, "v3.db");
        await using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = string.Concat(Migrations.All.Take(3).Select(m => m.Sql)) + """
                PRAGMA user_version = 3;
                INSERT INTO videos (fingerprint, path, title, file_size, added_at) VALUES ('1:a', 'D:\rec\a.mp4', 'a', 1, '2026-09-01T00:00:00.0000000+00:00');
                INSERT INTO folders (path, added_at) VALUES ('D:\rec', '2026-09-01T00:00:00.0000000+00:00');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var db = new LibraryDatabase(path);
        await db.MigrateAsync();
        var lib = new SqliteVideoLibrary(db, _clock);

        Assert.Null((await lib.GetAsync(1))!.DirectoryId);
        Assert.Null((await lib.GetFoldersAsync())[0].TargetDirectoryId);
        Assert.Empty(await lib.GetDirectoriesAsync());
    }
}
