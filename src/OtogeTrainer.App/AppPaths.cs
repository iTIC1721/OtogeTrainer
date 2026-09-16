using System.IO;

namespace OtogeTrainer.App;

/// <summary>앱 데이터가 놓이는 곳. (SPEC §8.1)</summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        DatabasePath = Path.Combine(DataDirectory, "library.db");
        ThumbnailDirectory = Path.Combine(DataDirectory, "thumbs");
        LogDirectory = Path.Combine(DataDirectory, "logs");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");

        Directory.CreateDirectory(ThumbnailDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary><c>%APPDATA%\OtogeTrainer</c></summary>
    public static AppPaths Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OtogeTrainer"));

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string ThumbnailDirectory { get; }
    public string LogDirectory { get; }
    public string SettingsPath { get; }

    /// <summary>
    /// 썸네일은 지문으로 이름 짓는다. 파일을 옮겨도(경로가 바뀌어도) 같은 썸네일을 찾는다.
    /// 지문의 <c>크기:</c> 접두어는 파일명에 콜론을 쓸 수 없어 뗀다.
    /// </summary>
    public string ThumbnailFor(string fingerprint)
        => Path.Combine(ThumbnailDirectory, fingerprint[(fingerprint.IndexOf(':') + 1)..] + ".jpg");

    /// <summary>재생 가능한 확장자. (FR-101)</summary>
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".ts", ".m2ts", ".flv", ".wmv",
    };

    public static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public const string OpenFileFilter =
        "영상 파일|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.ts;*.m2ts;*.flv;*.wmv|모든 파일|*.*";
}
