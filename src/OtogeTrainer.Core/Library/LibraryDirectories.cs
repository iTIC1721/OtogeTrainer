namespace OtogeTrainer.Core.Library;

/// <summary>
/// 라이브러리 안에서만 존재하는 정리용 디렉토리. 디스크의 파일 위치와 아무 관계가 없다. (D-10)
/// <para>영상은 정확히 한 곳(디렉토리 하나 또는 최상위)에 속한다.</para>
/// </summary>
/// <param name="ParentId">null 이면 최상위.</param>
public sealed record LibraryDirectory(long Id, long? ParentId, string Name, DateTimeOffset CreatedAt);

/// <param name="VideoCount">바로 안에 든 영상 수 (하위 디렉토리 제외).</param>
public sealed record DirectoryEntry(LibraryDirectory Directory, int VideoCount);

/// <summary>디렉토리 · 영상의 위치를 거를 때. <c>Id</c> 가 null 이면 최상위.</summary>
public sealed record DirectoryFilter(long? Id, bool IncludeSubdirectories);

/// <summary>사용자에게 그대로 보여 줄 수 있는 규칙 위반 (이름 중복, 자기 안으로 이동 등).</summary>
public sealed class LibraryRuleException(string message) : Exception(message);

public static class DirectoryRules
{
    public const int MaxNameLength = 40;
    public const string DefaultNewName = "새 디렉토리";

    /// <summary>앞뒤 공백 제거 · 연속 공백 하나로 · 길이 제한. 비면 예외.</summary>
    public static string NormalizeName(string? name)
    {
        var normalized = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > MaxNameLength) normalized = normalized[..MaxNameLength].TrimEnd();
        if (normalized.Length == 0) throw new LibraryRuleException("디렉토리 이름을 입력하세요.");
        return normalized;
    }

    /// <summary>
    /// 같은 곳에 같은 이름(대소문자 무시)이 있으면 <c>이름 (2)</c>, <c>이름 (3)</c> … 을 붙인다.
    /// 윈도우 탐색기의 "새 폴더 (2)" 와 같다.
    /// </summary>
    public static string UniqueName(string name, IEnumerable<string> siblingNames)
    {
        var taken = new HashSet<string>(siblingNames, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;

        for (var n = 2; ; n++)
        {
            var suffix = $" ({n})";
            var stem = name.Length + suffix.Length > MaxNameLength ? name[..(MaxNameLength - suffix.Length)].TrimEnd() : name;
            var candidate = stem + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}

/// <summary>디렉토리 목록으로 부모 · 자식 · 경로를 계산한다. 목록은 작으므로 통째로 메모리에 둔다.</summary>
public sealed class DirectoryTree
{
    private readonly Dictionary<long, LibraryDirectory> _byId;
    private readonly ILookup<long?, LibraryDirectory> _byParent;

    public DirectoryTree(IEnumerable<LibraryDirectory> directories)
    {
        _byId = directories.ToDictionary(d => d.Id);
        _byParent = _byId.Values.ToLookup(d => d.ParentId);
    }

    public IReadOnlyCollection<LibraryDirectory> All => _byId.Values;

    public LibraryDirectory? Find(long? id) => id is { } value && _byId.TryGetValue(value, out var d) ? d : null;

    public bool Contains(long? id) => id is null || _byId.ContainsKey(id.Value);

    /// <summary>바로 아래 디렉토리. 이름 순.</summary>
    public IReadOnlyList<LibraryDirectory> Children(long? parentId)
        => _byParent[parentId].OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>최상위부터 <paramref name="id"/> 까지. 최상위(null)면 빈 목록.</summary>
    public IReadOnlyList<LibraryDirectory> PathTo(long? id)
    {
        var path = new List<LibraryDirectory>();
        var guard = 0;
        for (var d = Find(id); d is not null && guard++ < 1000; d = Find(d.ParentId))
            path.Add(d);
        path.Reverse();
        return path;
    }

    public string DisplayPath(long? id, string root = "라이브러리")
        => string.Join(" › ", new[] { root }.Concat(PathTo(id).Select(d => d.Name)));

    /// <summary><paramref name="ancestorId"/> 자신과 그 아래 모든 디렉토리.</summary>
    public IReadOnlySet<long> SelfAndDescendants(long ancestorId)
    {
        var result = new HashSet<long>();
        var stack = new Stack<long>([ancestorId]);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!result.Add(id)) continue;
            foreach (var child in _byParent[id]) stack.Push(child.Id);
        }
        return result;
    }

    /// <summary>트리를 위에서부터 펼친 순서 (디렉토리 고르기 목록용). 깊이는 0부터.</summary>
    public IEnumerable<(LibraryDirectory Directory, int Depth)> Flatten(long? parentId = null, int depth = 0)
    {
        foreach (var child in Children(parentId))
        {
            yield return (child, depth);
            foreach (var nested in Flatten(child.Id, depth + 1)) yield return nested;
        }
    }
}
