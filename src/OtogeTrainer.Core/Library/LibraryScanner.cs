namespace OtogeTrainer.Core.Library;

public sealed record ScanProgress(string FolderPath, int Found, int Registered, int Skipped, int Failed);

// Skipped 는 이미 아는 파일과 목록에서 제거한(제외 목록) 파일을 함께 센다

/// <param name="FolderMissing">폴더가 없거나 접근할 수 없다 (외장 디스크를 뺀 경우 등).</param>
public sealed record ScanResult(string FolderPath, int Found, int Registered, int Skipped, int Failed, bool FolderMissing);

/// <summary>
/// 등록된 폴더를 훑어 영상을 라이브러리에 올린다. (FR-104)
/// <para>
/// <b>이미 아는 파일은 지문을 다시 계산하지 않는다.</b> 경로와 크기가 같으면 같은 파일로 본다.
/// 지문 계산은 파일당 2MB 를 읽으므로, 수백 개 폴더를 앱 시작마다 다시 읽으면 외장 HDD 에서 느리다.
/// </para>
/// <para>
/// 옮겨진 파일은 경로가 달라 새로 지문을 계산하고, 지문이 같으므로 기존 영상의 경로가 갱신된다
/// (북마크 · 오프셋 · 태그가 따라간다).
/// </para>
/// </summary>
public sealed class LibraryScanner(IVideoLibrary library, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ScanResult> ScanAsync(
        LibraryFolder folder, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder.Path))
            return new ScanResult(folder.Path, 0, 0, 0, 0, FolderMissing: true);

        var known = new Dictionary<string, long>(await library.GetKnownFileSizesAsync(ct), StringComparer.OrdinalIgnoreCase);

        // 새 영상이 들어갈 디렉토리는 파일마다 "가장 깊은 녹화 폴더" 기준이다. 상위 폴더를 먼저 훑어도
        // 하위 폴더에 지정한 디렉토리로 들어가게 한다.
        var folders = await library.GetFoldersAsync(ct);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,          // 권한 없는 하위 폴더 하나 때문에 스캔 전체가 멈추지 않게
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        int found = 0, registered = 0, skipped = 0, failed = 0;

        foreach (var path in Directory.EnumerateFiles(folder.Path, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!VideoFiles.IsVideo(path)) continue;
            found++;

            try
            {
                var size = new FileInfo(path).Length;
                if (known.TryGetValue(path, out var knownSize) && knownSize == size)
                {
                    skipped++;
                }
                else
                {
                    var (fingerprint, fileSize) = await FileFingerprint.ComputeAsync(path, ct);
                    if (await library.IsExcludedAsync(fingerprint, path, fileSize, ct))
                    {
                        skipped++;   // 사용자가 목록에서 제거한 영상. 옮겨진 경로여도 다시 올리지 않는다
                    }
                    else
                    {
                        var target = (FolderRules.FindOwner(folders, path) ?? folder).TargetDirectoryId;
                        await library.OpenOrRegisterAsync(path, fingerprint, fileSize, target, ct);
                        registered++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;   // 녹화 중이라 잠긴 파일 등. 다음 스캔에서 다시 시도한다
            }

            progress?.Report(new ScanProgress(folder.Path, found, registered, skipped, failed));
        }

        await library.MarkFolderScannedAsync(folder.Id, _clock.GetUtcNow(), ct);
        return new ScanResult(folder.Path, found, registered, skipped, failed, FolderMissing: false);
    }
}
