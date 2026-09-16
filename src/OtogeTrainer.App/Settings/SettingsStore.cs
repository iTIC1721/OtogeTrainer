using System.IO;
using System.Text;
using System.Text.Json;
using OtogeTrainer.Input;

namespace OtogeTrainer.App.Settings;

/// <summary><c>settings.json</c> 의 내용. 사람이 열어 고칠 수 있게 이름으로 저장한다. (SPEC §4.4)</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    /// <summary>재생 중 동작 → 키 이름. 해제된 동작은 빈 문자열. (D-8)</summary>
    public Dictionary<string, string>? PlaybackKeys { get; set; }
}

/// <summary>설정 파일 읽기 · 쓰기.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Path { get; } = path;

    /// <summary>
    /// 재생 중 동작 키를 읽는다. 파일이 없거나 깨졌으면 기본값을 쓴다 — 설정 파일 때문에
    /// 앱이 뜨지 않는 일은 없어야 한다. 깨졌던 경우 <paramref name="error"/> 로 알린다.
    /// </summary>
    public PlaybackKeyMap LoadKeyMap(out string? error)
    {
        error = null;
        if (!File.Exists(Path)) return new PlaybackKeyMap();

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path, Encoding.UTF8), Json);
            return PlaybackKeyMap.FromSerializable(settings?.PlaybackKeys);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"설정 파일을 읽지 못해 기본 키 배치를 사용합니다. ({ex.Message})";
            return new PlaybackKeyMap();
        }
    }

    /// <summary>
    /// 저장한다. 임시 파일에 다 쓴 뒤 바꿔치기하므로, 쓰는 도중 앱이 죽어도 기존 설정이 반쯤 덮이지 않는다.
    /// </summary>
    public void Save(PlaybackKeyMap map)
    {
        var settings = new AppSettings { PlaybackKeys = map.ToSerializable() };
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json), Encoding.UTF8);
        File.Move(temp, Path, overwrite: true);
    }
}
