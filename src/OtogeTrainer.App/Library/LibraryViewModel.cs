using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtogeTrainer.Core.Library;
using OtogeTrainer.Core.Playback;

namespace OtogeTrainer.App.Library;

/// <summary>라이브러리(홈) 화면. M1 에서는 최근 재생 목록만 있다. (FR-102)</summary>
public sealed partial class LibraryViewModel(IVideoLibrary library, AppPaths paths, TimeProvider? clock = null)
    : ObservableObject
{
    public const int RecentLimit = 50;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ObservableCollection<RecentVideoItem> Recent { get; } = [];

    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>셸이 연결한다. 파일 경로를 받아 플레이어로 연다.</summary>
    public Func<string, Task>? OpenPath { get; set; }

    /// <summary>셸이 연결한다. 알림 배너에 메시지를 띄운다.</summary>
    public Action<string>? Notify { get; set; }

    public async Task RefreshAsync()
    {
        var items = await library.GetRecentAsync(RecentLimit);
        var now = _clock.GetUtcNow();

        Recent.Clear();
        foreach (var item in items)
            Recent.Add(new RecentVideoItem(item, paths.ThumbnailFor(item.Video.Fingerprint), now));

        IsEmpty = Recent.Count == 0;
    }

    [RelayCommand]
    private async Task OpenRecent(RecentVideoItem item)
    {
        if (item.IsMissing)
        {
            // 다시 찾기(FR-105)는 M2. 지금은 무엇이 문제인지만 알린다.
            Notify?.Invoke($"파일을 찾을 수 없습니다 — 옮겨졌거나 삭제됐습니다.\n{item.Path}");
            return;
        }

        if (OpenPath is not null) await OpenPath(item.Path);
    }
}

/// <summary>최근 목록 카드 한 장. (UI-BLUEPRINT §3)</summary>
public sealed class RecentVideoItem
{
    public RecentVideoItem(RecentVideo source, string thumbnailPath, DateTimeOffset now)
    {
        var v = source.Video;
        Path = v.Path;
        Title = v.Title;
        IsMissing = !File.Exists(v.Path);

        DurationText = v.DurationMs is { } d ? Timecode.FormatShort(TimeSpan.FromMilliseconds(d)) : "";
        Progress = v.Progress;
        ProgressText = v.Progress >= 0.95 ? "완료" : $"{v.Progress:P0}";
        LastPlayedText = v.LastPlayedAt is { } at ? Relative(now - at, at) : "";
        BookmarkText = source.BookmarkCount > 0 ? $"🔖{source.BookmarkCount}" : "";
        OffsetText = v.AvOffsetMs is { } ms and not 0 ? $"⏱{ms:+0;-0} ms" : "";
        SpeedText = v.LastSpeed is { } s && Math.Abs(s - 1.0) > 0.001 ? $"{s:0.##}x" : "";
        Thumbnail = IsMissing ? null : LoadThumbnail(thumbnailPath);
    }

    public string Path { get; }
    public string Title { get; }
    public bool IsMissing { get; }
    public string DurationText { get; }
    public double Progress { get; }
    public string ProgressText { get; }
    public string LastPlayedText { get; }
    public string BookmarkText { get; }
    public string OffsetText { get; }
    public string SpeedText { get; }
    public ImageSource? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail is not null;

    private static string Relative(TimeSpan ago, DateTimeOffset at) => ago switch
    {
        { TotalMinutes: < 1 } => "방금",
        { TotalHours: < 1 } => $"{(int)ago.TotalMinutes}분 전",
        { TotalDays: < 1 } => $"{(int)ago.TotalHours}시간 전",
        { TotalDays: < 7 } => $"{(int)ago.TotalDays}일 전",
        _ => at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// 썸네일은 플레이어를 떠날 때마다 같은 이름으로 덮어쓴다. 그래서
    /// <c>OnLoad</c>(파일을 잡고 있지 않음)와 <c>IgnoreImageCache</c>(URI 가 같아도 새로 읽음)가 둘 다 필요하다.
    /// </summary>
    private static BitmapImage? LoadThumbnail(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 480;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;   // 깨진 썸네일은 없는 셈 친다
        }
    }
}
