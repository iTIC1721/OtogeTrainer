using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using OtogeTrainer.Core.Playback;
using OtogeTrainer.Playback;

namespace OtogeTrainer.App.Player;

public partial class PlayerView : UserControl
{
    public PlayerView() => InitializeComponent();

    private PlayerViewModel? ViewModel => DataContext as PlayerViewModel;

    /// <summary>
    /// 영상 표면은 앱 수명 동안 하나만 만든다. 화면을 오갈 때마다 mpv 를 새로 띄우지 않기 위해서다.
    /// </summary>
    public void AttachSurface(VideoSurfaceHost surface) => VideoFrame.Child = surface;

    // 타임라인: 누르는 동안은 엔진 위치로 덮어쓰지 않고, 뗄 때 한 번만 정확 시크한다. (FR-202)
    private void OnTimelinePressed(object sender, MouseButtonEventArgs e) => ViewModel?.BeginScrub();

    private void OnTimelineReleased(object sender, MouseButtonEventArgs e) => ViewModel?.EndScrub(Timeline.Value);

    private void OnTimelineCaptureLost(object sender, MouseEventArgs e)
    {
        if (ViewModel?.IsScrubbing == true) ViewModel.EndScrub(Timeline.Value);
    }
}

/// <summary>북마크 슬롯의 짧은 시간 표기. 1시간 미만이면 <c>M:SS.f</c>.</summary>
public sealed class SlotTimeConverter : IValueConverter
{
    public static readonly SlotTimeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long ms) return "—";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? Timecode.FormatShort(t)
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
