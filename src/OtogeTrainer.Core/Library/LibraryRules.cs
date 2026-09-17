namespace OtogeTrainer.Core.Library;

/// <summary>재생 가능한 파일. (FR-101)</summary>
public static class VideoFiles
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".ts", ".m2ts", ".flv", ".wmv",
    };

    public static bool IsVideo(string path) => Extensions.Contains(Path.GetExtension(path));
}

/// <summary>녹화 폴더 경로 규칙. (FR-104)</summary>
public static class FolderRules
{
    /// <summary>전체 경로로 바꾸고 끝의 구분자를 뗀다. <c>D:\rec\</c> 와 <c>D:\rec</c> 를 같게 본다.</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        // "D:\" 같은 드라이브 루트는 구분자를 떼면 "D:" (현재 디렉터리 상대)가 되어 뜻이 바뀐다
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ? full : Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>
    /// 파일을 포함하는 <b>가장 깊은</b> 녹화 폴더. 그 폴더의 대상 디렉토리로 새 영상이 들어간다.
    /// 상위 폴더와 하위 폴더를 둘 다 등록했으면 하위 폴더가 이긴다.
    /// </summary>
    public static LibraryFolder? FindOwner(IEnumerable<LibraryFolder> folders, string filePath)
        => folders.Where(f => Contains(f.Path, filePath)).MaxBy(f => f.Path.Length);

    /// <summary>
    /// 파일이 폴더 아래(하위 폴더 포함)에 있는가. 대소문자를 구분하지 않는다.
    /// <para>
    /// 경계를 지킨다 — <c>D:\rec</c> 는 <c>D:\recording\a.mp4</c> 를 포함하지 않는다.
    /// </para>
    /// </summary>
    public static bool Contains(string folder, string filePath)
    {
        var dir = Normalize(folder);
        var file = Path.GetFullPath(filePath);
        if (!file.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Length == dir.Length) return false;

        return Path.EndsInDirectorySeparator(dir)
               || file[dir.Length] == Path.DirectorySeparatorChar
               || file[dir.Length] == Path.AltDirectorySeparatorChar;
    }
}

/// <summary>태그 입력 규칙. (FR-107)</summary>
public static class TagRules
{
    public const int MaxLength = 30;
    public const int MaxPerVideo = 20;

    /// <summary>"iidx, 연습중,  라스트 트릴 " 같은 입력을 태그 목록으로.</summary>
    public static IReadOnlyList<string> Parse(string? input)
        => Normalize((input ?? "").Split([',', '\n', ';']));

    /// <summary>
    /// 앞뒤 공백 제거 · 연속 공백 하나로 · 앞의 <c>#</c> 제거 · 빈 것 버림 · 길이 제한 ·
    /// 대소문자만 다른 중복 제거(먼저 나온 표기 유지) · 개수 제한.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var raw in tags)
        {
            var tag = string.Join(' ', raw.Trim().TrimStart('#').Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (tag.Length == 0) continue;
            if (tag.Length > MaxLength) tag = tag[..MaxLength].TrimEnd();
            if (!seen.Add(tag)) continue;

            result.Add(tag);
            if (result.Count == MaxPerVideo) break;
        }

        return result;
    }
}
