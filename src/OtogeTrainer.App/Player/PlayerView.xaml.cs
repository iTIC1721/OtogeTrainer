using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using OtogeTrainer.App.Settings;
using OtogeTrainer.Core.Playback;
using OtogeTrainer.Playback;

namespace OtogeTrainer.App.Player;

public partial class PlayerView : UserControl
{
    public PlayerView()
    {
        InitializeComponent();

        // ★ XAML 이벤트 속성(PreviewMouseLeftButtonDown="…")으로 걸면 트랙 클릭을 받지 못한다.
        //   Slider 는 IsMoveToPointEnabled 일 때 자기 클래스 처리기에서 트랙 클릭을 "처리됨" 으로
        //   표시하는데, 클래스 처리기는 인스턴스 처리기보다 먼저 돈다. 그래서 스크럽 시작이 불리지
        //   않았고, 슬라이더 값만 잠깐 바뀌었다가 30Hz 갱신이 원래 위치로 되돌려 버렸다.
        //   handledEventsToo 로 걸어야 한다.
        Timeline.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnTimelineDown), handledEventsToo: true);
        Timeline.AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(OnTimelineMove), handledEventsToo: true);
        Timeline.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnTimelineUp), handledEventsToo: true);
        Timeline.AddHandler(LostMouseCaptureEvent, new MouseEventHandler(OnTimelineCaptureLost), handledEventsToo: true);
    }

    private PlayerViewModel? ViewModel => DataContext as PlayerViewModel;

    /// <summary>
    /// 영상 표면은 앱 수명 동안 하나만 만든다. 화면을 오갈 때마다 mpv 를 새로 띄우지 않기 위해서다.
    /// </summary>
    public void AttachSurface(VideoSurfaceHost surface) => VideoFrame.Child = surface;

    internal Slider TimelineSlider => Timeline;

    internal Slider VolumeSliderControl => VolumeSlider;

    // ── 오프셋 프리셋 (D-9) ──────────────────────────────────────────────────

    /// <summary>
    /// 프리셋 메뉴는 누를 때마다 목록으로 새로 만든다. 설정 창에서 바뀐 이름 · 값이 바로 반영되고,
    /// 지금 오프셋과 같은 프리셋에 체크가 붙는다.
    /// </summary>
    private void OnOffsetPresetsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var menu = new ContextMenu { PlacementTarget = OffsetPresetButton, Placement = PlacementMode.Top };
        foreach (var preset in vm.OffsetPresets.Items)
        {
            var item = new MenuItem
            {
                Header = $"{preset.Name}    {preset.OffsetText}",
                IsChecked = vm.HasVideo && preset.OffsetMs == vm.OffsetMs,
                IsEnabled = vm.HasVideo,
            };
            item.Click += (_, _) => vm.ApplyOffsetPreset(preset);
            menu.Items.Add(item);
        }

        if (vm.OffsetPresets.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "저장된 프리셋이 없습니다", IsEnabled = false });

        menu.Items.Add(new Separator());
        var add = new MenuItem
        {
            Header = vm.OffsetPresets.CanAdd
                ? $"지금 값({vm.OffsetText})을 프리셋으로 추가"
                : $"프리셋은 {OffsetPresetList.MaxCount}개까지입니다 (설정에서 정리)",
            IsEnabled = vm.OffsetPresets.CanAdd,
        };
        add.Click += (_, _) => vm.AddCurrentOffsetAsPreset();
        menu.Items.Add(add);

        menu.IsOpen = true;
    }

    // ── 전체화면 떠 있는 컨트롤 ──────────────────────────────────────────────

    private Brush? _dockedBackground;

    /// <summary>
    /// 컨트롤 영역을 이 화면에서 떼어 낸다. 전체화면에서 영상 위에 띄우기 위해서다.
    /// 요소 자체를 옮기므로 바인딩 · 타임라인 처리기 · 북마크 메뉴가 그대로 따라간다.
    /// </summary>
    public Border DetachControls()
    {
        if (ControlsPanel.Parent is Panel parent) parent.Children.Remove(ControlsPanel);
        _dockedBackground ??= ControlsPanel.Background;
        ControlsPanel.Background = FloatingBackground;
        ControlsPanel.CornerRadius = new CornerRadius(10);
        ControlsPanel.BorderBrush = FloatingBorder;
        ControlsPanel.BorderThickness = new Thickness(1);
        return ControlsPanel;
    }

    /// <summary>떼어 냈던 컨트롤 영역을 원래 자리(하단)로 되돌린다.</summary>
    public void AttachControls()
    {
        if (ControlsPanel.Parent == LayoutRoot) return;
        switch (ControlsPanel.Parent)
        {
            case Panel panel: panel.Children.Remove(ControlsPanel); break;
            case ContentControl host: host.Content = null; break;
            case Decorator decorator: decorator.Child = null; break;
        }

        ControlsPanel.Background = _dockedBackground;
        ControlsPanel.CornerRadius = default;
        ControlsPanel.BorderThickness = default;
        Grid.SetRow(ControlsPanel, 2);
        LayoutRoot.Children.Add(ControlsPanel);
    }

    internal Border Controls => ControlsPanel;

    private static readonly Brush FloatingBackground = Frozen(new SolidColorBrush(Color.FromArgb(0xD0, 0x16, 0x18, 0x1D)));
    private static readonly Brush FloatingBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    // ── 타임라인 (FR-202) ────────────────────────────────────────────────────
    //
    // 트랙이든 썸이든 누른 곳으로 바로 이동하고, 누른 채 끌면 따라간다. 드래그는 슬라이더가
    // 마우스를 직접 잡아서 처리한다 — WPF 기본 동작으로는 썸을 잡았을 때만 끌 수 있고,
    // 트랙을 눌러 이동한 뒤에는 끌기가 이어지지 않는다.

    private void OnTimelineDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { HasVideo: true } vm) return;

        vm.BeginScrub(ValueAt(e));
        Timeline.CaptureMouse();
        e.Handled = true;   // 썸의 자체 드래그가 따로 시작되지 않게
    }

    private void OnTimelineMove(object sender, MouseEventArgs e)
    {
        if (!Timeline.IsMouseCaptured || ViewModel is not { IsScrubbing: true } vm) return;
        vm.UpdateScrub(ValueAt(e));
    }

    private void OnTimelineUp(object sender, MouseButtonEventArgs e)
    {
        if (!Timeline.IsMouseCaptured) return;
        var value = ValueAt(e);
        Timeline.ReleaseMouseCapture();          // → LostMouseCapture 가 뒤따른다
        ViewModel?.EndScrub(value);
        e.Handled = true;
    }

    /// <summary>창 밖으로 끌고 나가 캡처를 잃는 등, 뗌 없이 끝난 경우.</summary>
    private void OnTimelineCaptureLost(object sender, MouseEventArgs e)
    {
        if (ViewModel is { IsScrubbing: true } vm) vm.EndScrub(vm.TimelinePosition);
    }

    /// <summary>
    /// 마우스 위치의 타임라인 값(초). 트랙 폭에서 직접 비율을 계산한다.
    /// <para>
    /// ★ <c>Track.ValueFromPoint</c> 를 쓰면 안 된다. 그 메서드는 절대 위치가 아니라
    /// "썸의 현재 중심에서 떨어진 거리" 로 계산하는데, 썸 중심은 <b>마지막 레이아웃 시점</b>의 위치다.
    /// 재생 중에는 값이 30Hz 로 바뀌어 썸 위치가 한 박자 늦으므로 오차가 생기고, 누름 직후
    /// 캡처로 생기는 이동 이벤트에서 한 번 더 누적된다. 재생 중 40% 를 눌렀는데 20% 로 간
    /// 원인이었다(일시정지 중에는 값이 멈춰 있어 드러나지 않았다).
    /// </para>
    /// </summary>
    private double ValueAt(MouseEventArgs e)
    {
        if (Timeline.Template.FindName("PART_Track", Timeline) is not Track track || track.ActualWidth <= 0)
            return Timeline.Value;

        // 썸 반지름만큼은 양 끝에서 비워 둔다 — 썸 중심이 최소 · 최대 값에 닿는 지점
        var thumb = track.Thumb?.ActualWidth ?? 0;
        var usable = track.ActualWidth - thumb;
        var x = e.GetPosition(track).X - thumb / 2;
        var ratio = usable > 0 ? Math.Clamp(x / usable, 0, 1) : 0;

        return Timeline.Minimum + ratio * (Timeline.Maximum - Timeline.Minimum);
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
