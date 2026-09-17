using OtogeTrainer.Core.Library;
using Xunit;

namespace OtogeTrainer.Core.Tests;

public sealed class DirectoryRulesTests
{
    [Theory]
    [InlineData("  IIDX  ", "IIDX")]
    [InlineData("라스트   트릴", "라스트 트릴")]
    public void 이름을_다듬는다(string input, string expected)
        => Assert.Equal(expected, DirectoryRules.NormalizeName(input));

    [Fact]
    public void 빈_이름은_거부하고_긴_이름은_자른다()
    {
        Assert.Throws<LibraryRuleException>(() => DirectoryRules.NormalizeName("   "));
        Assert.Equal(DirectoryRules.MaxNameLength, DirectoryRules.NormalizeName(new string('가', 100)).Length);
    }

    [Fact]
    public void 겹치는_이름에는_번호를_붙인다()
    {
        Assert.Equal("IIDX", DirectoryRules.UniqueName("IIDX", ["SDVX"]));
        Assert.Equal("IIDX (2)", DirectoryRules.UniqueName("IIDX", ["iidx"]));
        Assert.Equal("IIDX (3)", DirectoryRules.UniqueName("IIDX", ["IIDX", "IIDX (2)"]));

        var longName = new string('가', DirectoryRules.MaxNameLength);
        var unique = DirectoryRules.UniqueName(longName, [longName]);
        Assert.EndsWith(" (2)", unique);
        Assert.Equal(DirectoryRules.MaxNameLength, unique.Length);
    }

    [Fact]
    public void 트리_경로_자손_펼치기()
    {
        var t = DateTimeOffset.UnixEpoch;
        var tree = new DirectoryTree(
        [
            new LibraryDirectory(1, null, "리듬게임", t),
            new LibraryDirectory(2, 1, "SDVX", t),
            new LibraryDirectory(3, 1, "IIDX", t),
            new LibraryDirectory(4, 3, "연습", t),
            new LibraryDirectory(5, null, "대회", t),
        ]);

        Assert.Equal("라이브러리 › 리듬게임 › IIDX › 연습", tree.DisplayPath(4));
        Assert.Equal("라이브러리", tree.DisplayPath(null));
        Assert.Equal(new HashSet<long> { 1, 2, 3, 4 }, tree.SelfAndDescendants(1));
        Assert.Equal(
            ["대회:0", "리듬게임:0", "IIDX:1", "연습:2", "SDVX:1"],
            tree.Flatten().Select(x => $"{x.Directory.Name}:{x.Depth}"));
    }

    [Fact]
    public void 가장_깊은_녹화_폴더를_찾는다()
    {
        var t = DateTimeOffset.UnixEpoch;
        LibraryFolder[] folders = [new(1, @"D:\rec", t, null, 10), new(2, @"D:\rec\iidx", t, null, 20)];

        Assert.Equal(2, FolderRules.FindOwner(folders, @"D:\rec\iidx\a.mp4")?.Id);
        Assert.Equal(1, FolderRules.FindOwner(folders, @"D:\rec\sdvx\a.mp4")?.Id);
        Assert.Null(FolderRules.FindOwner(folders, @"E:\a.mp4"));
    }
}
