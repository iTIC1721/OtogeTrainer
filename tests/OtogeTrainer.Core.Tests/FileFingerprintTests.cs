using OtogeTrainer.Core.Library;
using Xunit;

namespace OtogeTrainer.Core.Tests;

public sealed class FileFingerprintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otoge-fp-" + Guid.NewGuid().ToString("N"));

    public FileFingerprintTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 임시 폴더 */ }
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private const int Big = FileFingerprint.ChunkSize * 3 + 12345;

    [Fact]
    public async Task 같은_내용이면_경로가_달라도_같다()
    {
        var content = Random(Big, 1);
        var a = await FileFingerprint.ComputeAsync(Write("a.mp4", content));
        var b = await FileFingerprint.ComputeAsync(Write("sub_moved_renamed.mkv", content));

        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public async Task 선두가_다르면_다르다()
    {
        var content = Random(Big, 2);
        var a = await FileFingerprint.ComputeAsync(Write("a.mp4", content));
        content[10] ^= 0xFF;
        var b = await FileFingerprint.ComputeAsync(Write("b.mp4", content));

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public async Task 말미가_다르면_다르다()
    {
        var content = Random(Big, 3);
        var a = await FileFingerprint.ComputeAsync(Write("a.mp4", content));
        content[^10] ^= 0xFF;
        var b = await FileFingerprint.ComputeAsync(Write("b.mp4", content));

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public async Task 크기가_다르면_다르다()
    {
        var content = Random(Big, 4);
        var a = await FileFingerprint.ComputeAsync(Write("a.mp4", content));
        var b = await FileFingerprint.ComputeAsync(Write("b.mp4", [.. content, 0]));

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    /// <summary>
    /// 알려진 한계를 문서로 남긴다. 가운데만 다른 파일은 구별하지 못한다.
    /// 캡처 파일은 선두(헤더·생성 시각)와 말미(인덱스)가 반드시 달라지므로 실용상 문제없다.
    /// </summary>
    [Fact]
    public async Task 알려진_한계_가운데만_다르면_같다고_본다()
    {
        var content = Random(Big, 5);
        var a = await FileFingerprint.ComputeAsync(Write("a.mp4", content));
        content[Big / 2] ^= 0xFF;
        var b = await FileFingerprint.ComputeAsync(Write("b.mp4", content));

        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public async Task 작은_파일은_전체를_해시한다()
    {
        var content = Random(FileFingerprint.ChunkSize, 6);
        var a = await FileFingerprint.ComputeAsync(Write("a.mp4", content));
        content[FileFingerprint.ChunkSize / 2] ^= 0xFF;
        var b = await FileFingerprint.ComputeAsync(Write("b.mp4", content));

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public async Task 형식은_크기_콜론_해시()
    {
        var (fp, size) = await FileFingerprint.ComputeAsync(Write("a.mp4", Random(100, 7)));

        Assert.Equal(100, size);
        Assert.Matches("^100:[0-9a-f]{64}$", fp);
    }

    [Fact]
    public async Task 다른_프로세스가_쓰는_중인_파일도_읽을_수_있다()
    {
        // OBS 가 녹화 중인 파일을 라이브러리에 끌어다 놓는 경우.
        var path = Write("recording.mp4", Random(Big, 8));
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var (fp, _) = await FileFingerprint.ComputeAsync(path);
        Assert.NotEmpty(fp);
    }
}
