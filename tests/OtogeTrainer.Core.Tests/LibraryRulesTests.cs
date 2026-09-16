using OtogeTrainer.Core.Library;
using Xunit;

namespace OtogeTrainer.Core.Tests;

public sealed class FolderRulesTests
{
    private static LibraryFolder Folder(string path, int? offset)
        => new(path.GetHashCode(), path, offset, DateTimeOffset.UnixEpoch, null);

    [Theory]
    [InlineData(@"D:\rec\", @"D:\rec")]
    [InlineData(@"D:\rec", @"D:\rec")]
    [InlineData(@"D:\rec\..\rec\iidx\", @"D:\rec\iidx")]
    [InlineData(@"D:/rec/iidx", @"D:\rec\iidx")]
    [InlineData(@"D:\", @"D:\")]       // 드라이브 루트는 구분자를 남긴다
    public void 경로를_정규화한다(string input, string expected)
        => Assert.Equal(expected, FolderRules.Normalize(input));

    [Theory]
    [InlineData(@"D:\rec", @"D:\rec\a.mp4", true)]
    [InlineData(@"D:\rec", @"D:\rec\iidx\2026\a.mp4", true)]
    [InlineData(@"D:\REC\", @"d:\rec\a.mp4", true)]
    [InlineData(@"D:\rec", @"D:\recording\a.mp4", false)]   // 이름 앞부분만 같은 다른 폴더
    [InlineData(@"D:\rec", @"D:\rec", false)]
    [InlineData(@"D:\rec", @"E:\rec\a.mp4", false)]
    [InlineData(@"D:\", @"D:\rec\a.mp4", true)]
    public void 폴더_포함_여부는_경계를_지킨다(string folder, string file, bool expected)
        => Assert.Equal(expected, FolderRules.Contains(folder, file));

    [Fact]
    public void 기본값이_지정된_가장_가까운_폴더를_따른다()
    {
        var folders = new[]
        {
            Folder(@"D:\rec", 33),
            Folder(@"D:\rec\iidx", null),
            Folder(@"D:\rec\sdvx", 10),
        };

        Assert.Equal(33, FolderRules.InheritedOffset(folders, @"D:\rec\a.mp4"));
        Assert.Equal(33, FolderRules.InheritedOffset(folders, @"D:\rec\iidx\a.mp4"));   // 지정 안 한 하위 폴더는 위를 따른다
        Assert.Equal(10, FolderRules.InheritedOffset(folders, @"D:\rec\sdvx\x\a.mp4"));
        Assert.Null(FolderRules.InheritedOffset(folders, @"E:\other\a.mp4"));
    }

    [Theory]
    [InlineData(20, 33, 20)]
    [InlineData(0, 33, 0)]        // 영상별 0 도 명시적인 값이다
    [InlineData(null, 33, 33)]
    [InlineData(null, null, 0)]
    public void 영상별_값이_폴더_기본값보다_우선한다(int? video, int? inherited, int expected)
        => Assert.Equal(expected, FolderRules.EffectiveOffset(video, inherited));
}

public sealed class TagRulesTests
{
    [Fact]
    public void 쉼표로_나누고_다듬는다()
        => Assert.Equal(["iidx", "연습중", "라스트 트릴"], TagRules.Parse(" iidx, 연습중 ,,  라스트   트릴 "));

    [Fact]
    public void 앞의_샵은_뗀다()
        => Assert.Equal(["iidx"], TagRules.Parse("#iidx"));

    [Fact]
    public void 대소문자만_다른_중복은_먼저_나온_표기를_남긴다()
        => Assert.Equal(["IIDX", "sdvx"], TagRules.Parse("IIDX, iidx, sdvx, SDVX"));

    [Fact]
    public void 빈_입력은_빈_목록()
    {
        Assert.Empty(TagRules.Parse(null));
        Assert.Empty(TagRules.Parse(" , ,# "));
    }

    [Fact]
    public void 길이와_개수를_제한한다()
    {
        Assert.Equal(TagRules.MaxLength, TagRules.Parse(new string('가', 100))[0].Length);

        var many = string.Join(',', Enumerable.Range(0, 50).Select(i => "t" + i));
        Assert.Equal(TagRules.MaxPerVideo, TagRules.Parse(many).Count);
    }
}

public sealed class VideoFilesTests
{
    [Theory]
    [InlineData("a.mp4", true)]
    [InlineData("a.MKV", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.mp4.part", false)]
    [InlineData("noext", false)]
    public void 확장자로_판단한다(string path, bool expected)
        => Assert.Equal(expected, VideoFiles.IsVideo(path));
}
