using System.Security.Cryptography;

namespace OtogeTrainer.Core.Library;

/// <summary>
/// 경로가 바뀌어도 같은 영상을 알아보기 위한 식별자. (SPEC §5.1)
/// <para>
/// <c>파일 크기 + SHA-256(선두 1MB ‖ 말미 1MB)</c>. 2MB 이하면 파일 전체를 해시한다.
/// </para>
/// <para>
/// <b>재생 길이는 넣지 않는다.</b> SPEC 초안에는 있었으나, 길이는 엔진이 파일을 열어야
/// 알 수 있다. 그러면 영상을 연 <i>뒤에야</i> DB 를 조회할 수 있어 마지막 위치 · 배속 ·
/// 오프셋을 첫 프레임 전에 적용하지 못하고 화면이 한 번 튄다(FR-103). 크기와 앞뒤 해시만으로
/// 캡처 파일은 충분히 구별된다 — 선두에는 컨테이너 헤더와 생성 시각이, 말미에는 인덱스가 있다.
/// </para>
/// </summary>
public static class FileFingerprint
{
    public const int ChunkSize = 1024 * 1024;

    /// <summary>약 2MB 만 읽으므로 수 GB 파일에서도 즉시 끝난다.</summary>
    public static async Task<(string Fingerprint, long FileSize)> ComputeAsync(
        string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920, useAsync: true);

        var size = stream.Length;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (size <= ChunkSize * 2L)
        {
            await AppendAsync(stream, sha, (int)size, ct);
        }
        else
        {
            await AppendAsync(stream, sha, ChunkSize, ct);
            stream.Seek(-ChunkSize, SeekOrigin.End);
            await AppendAsync(stream, sha, ChunkSize, ct);
        }

        var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        return ($"{size}:{hash}", size);
    }

    private static async Task AppendAsync(Stream stream, IncrementalHash sha, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) break;
            read += n;
        }
        sha.AppendData(buffer, 0, read);
    }
}
