using Microsoft.Data.Sqlite;
using OtogeTrainer.Core.Library;
using Xunit;

namespace OtogeTrainer.Data.Tests;

/// <summary>목록에서 제거 · 제외 목록. (FR-108)</summary>
public sealed class RemoveVideoTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otoge-rm-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
    private LibraryDatabase _db = null!;
    private SqliteVideoLibrary _lib = null!;

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

    private string VideosDir => Path.Combine(_dir, "videos");

    private string MakeFile(string relative)
    {
        var path = Path.Combine(VideosDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Guid.NewGuid().ToByteArray());
        return path;
    }

    private async Task<ScanResult> Scan()
    {
        var folders = await _lib.GetFoldersAsync();
        var folder = folders.Count > 0 ? folders[0] : await _lib.AddFolderAsync(VideosDir);
        return await new LibraryScanner(_lib, _clock).ScanAsync(folder);
    }

    private async Task<string[]> Titles()
        => (await _lib.QueryAsync(new VideoQuery(Sort: VideoSort.Title))).Select(i => i.Video.Title).ToArray();

    [Fact]
    public async Task 제거하면_기록이_함께_지워지고_파일은_남는다()
    {
        var path = MakeFile("a.mp4");
        var (fp, size) = await FileFingerprint.ComputeAsync(path);
        var v = await _lib.OpenOrRegisterAsync(path, fp, size);
        await _lib.SetBookmarkAsync(v.Id, 1, 1000);
        await _lib.SetVideoTagsAsync(v.Id, ["only-here"]);

        Assert.Equal(1, await _lib.RemoveVideosAsync([v.Id, v.Id]));

        Assert.Null(await _lib.GetAsync(v.Id));
        Assert.Empty(await _lib.GetBookmarksAsync(v.Id));
        Assert.Empty(await _lib.GetTagsAsync());   // 아무도 안 쓰는 태그도 정리
        Assert.True(File.Exists(path));
        Assert.Equal(1, await _lib.GetExcludedCountAsync());
    }

    [Fact]
    public async Task 제거한_영상은_스캔이_다시_올리지_않는다()
    {
        MakeFile("a.mp4");
        MakeFile("b.mp4");
        await Scan();
        var a = (await _lib.QueryAsync(new VideoQuery("a")))[0].Video;

        await _lib.RemoveVideosAsync([a.Id]);
        var again = await Scan();

        Assert.Equal(["b"], await Titles());
        Assert.Equal((2, 0, 2), (again.Found, again.Registered, again.Skipped));
    }

    [Fact]
    public async Task 제거한_파일을_옮겨도_제외가_유지된다()
    {
        var original = MakeFile("a.mp4");
        await Scan();
        await _lib.RemoveVideosAsync([(await _lib.QueryAsync(new VideoQuery()))[0].Video.Id]);

        var moved = Path.Combine(VideosDir, "archive", "a.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(original, moved);

        Assert.Equal(0, (await Scan()).Registered);
        Assert.Empty(await Titles());

        // 옮긴 경로를 기억했으므로 그다음 스캔은 지문 계산 없이 건너뛴다
        var known = await _lib.GetKnownFileSizesAsync();
        Assert.True(known.ContainsKey(moved));
    }

    [Fact]
    public async Task 직접_열면_제외가_풀리고_다시_목록에_들어온다()
    {
        var path = MakeFile("a.mp4");
        var (fp, size) = await FileFingerprint.ComputeAsync(path);
        var v = await _lib.OpenOrRegisterAsync(path, fp, size);
        await _lib.SetBookmarkAsync(v.Id, 1, 1000);
        await _lib.RemoveVideosAsync([v.Id]);

        var reopened = await _lib.OpenOrRegisterAsync(path, fp, size);

        Assert.Empty(await _lib.GetBookmarksAsync(reopened.Id));   // 기록은 새로 시작
        Assert.Equal(0, await _lib.GetExcludedCountAsync());
        Assert.Equal(1, (await Scan()).Skipped);     // 이제 스캔에서도 아는 파일
    }

    [Fact]
    public async Task 제외를_모두_풀면_다음_스캔에서_다시_올라온다()
    {
        MakeFile("a.mp4");
        MakeFile("b.mp4");
        await Scan();
        await _lib.RemoveVideosAsync((await _lib.QueryAsync(new VideoQuery())).Select(i => i.Video.Id).ToList());
        Assert.Empty(await Titles());

        Assert.Equal(2, await _lib.ClearExclusionsAsync());
        Assert.Equal(2, (await Scan()).Registered);
        Assert.Equal(["a", "b"], await Titles());
    }

    [Fact]
    public async Task 그냥_지우기는_제외_목록에_넣지_않는다()
    {
        MakeFile("junk.mp4");
        await Scan();
        await _lib.RemoveAsync((await _lib.QueryAsync(new VideoQuery()))[0].Video.Id);

        Assert.Equal(0, await _lib.GetExcludedCountAsync());
        Assert.Equal(1, (await Scan()).Registered);
    }
}
