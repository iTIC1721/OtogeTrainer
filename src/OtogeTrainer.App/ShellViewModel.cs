using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OtogeTrainer.App.Library;
using OtogeTrainer.App.Player;
using OtogeTrainer.App.Settings;
using OtogeTrainer.Core.Library;
using OtogeTrainer.Input;

namespace OtogeTrainer.App;

public enum Screen { Library, Player }

/// <summary>화면 전환과 "파일 → 플레이어" 흐름을 묶는다.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IVideoLibrary _library;
    private readonly SettingsStore _settings;
    private readonly System.Windows.Threading.DispatcherTimer _audioSaveDelay;

    public static readonly TimeSpan AudioSaveDelay = TimeSpan.FromMilliseconds(400);

    public ShellViewModel(
        LibraryViewModel library, PlayerViewModel player, IVideoLibrary videoLibrary,
        PlaybackKeyMap keyMap, OffsetPresetList offsetPresets, SettingsStore settings)
    {
        OffsetPresets = offsetPresets;
        Library = library;
        Player = player;
        _library = videoLibrary;
        KeyMap = keyMap;
        _settings = settings;

        // 설정 창에서 키를 바꾸면 즉시 저장한다.
        KeyMap.Changed += (_, _) => SaveSettings();

        // 볼륨은 슬라이더를 끄는 동안 계속 바뀌므로 멈춘 뒤 한 번만 쓴다.
        _audioSaveDelay = new System.Windows.Threading.DispatcherTimer { Interval = AudioSaveDelay };
        _audioSaveDelay.Tick += (_, _) => SaveSettings();
        Player.AudioSettingsChanged += (_, _) => SaveSettingsSoon();
        OffsetPresets.Changed += (_, _) => SaveSettingsSoon();   // 이름을 치는 동안 매 글자 쓰지 않게

        Library.OpenPath = OpenPathAsync;
        Library.Notify = message => Notice = message;
        Library.EditTagsDialog = (item, existing) =>
        {
            var window = new TagEditorWindow(item.Title, item.TagList, existing) { Owner = System.Windows.Application.Current.MainWindow };
            return window.ShowDialog() == true ? window.Result : null;
        };
        Library.FoldersDialog = () =>
        {
            var window = new FoldersWindow(new FoldersViewModel(_library)) { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        };
        Player.BackRequested += async (_, _) => await BackToLibraryAsync();
    }

    public LibraryViewModel Library { get; }
    public PlayerViewModel Player { get; }

    /// <summary>재생 중 동작 키 배치. 입력 가드 · 플레이어 · 설정 창이 같은 객체를 본다. (D-8)</summary>
    public PlaybackKeyMap KeyMap { get; }

    /// <summary>오프셋 프리셋. 설정 창 · 플레이어가 같은 객체를 본다. (D-9)</summary>
    public OffsetPresetList OffsetPresets { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryScreen), nameof(IsPlayerScreen))]
    private Screen _screen = Screen.Library;

    /// <summary>화면 상단 알림 배너. 오류와 안내를 겸한다.</summary>
    [ObservableProperty] private string? _notice;

    /// <summary>
    /// 전체화면. 플레이어 화면에서만 의미가 있고, 라이브러리로 돌아가면 풀린다. (FR-207)
    /// 창 모양은 MainWindow 가 이 값을 보고 바꾼다.
    /// </summary>
    [ObservableProperty] private bool _isFullscreen;

    public bool IsLibraryScreen => Screen == Screen.Library;
    public bool IsPlayerScreen => Screen == Screen.Player;

    /// <summary>입력 가드가 묻는다: 지금 키를 막아야 하는가. (FR-501)</summary>
    public bool IsPlaying => IsPlayerScreen && Player.IsPlaying;

    /// <summary>앱 시작. 목록을 먼저 보여 주고, 등록된 폴더는 뒤에서 읽는다. (FR-104)</summary>
    public async Task StartAsync()
    {
        await Library.RefreshAsync();
        _ = Library.ScanFoldersAsync();
    }

    /// <summary>파일 경로 → 지문 → 라이브러리 등록/조회 → 플레이어. (FR-101 / FR-103)</summary>
    public async Task OpenPathAsync(string path)
    {
        Notice = null;

        if (!File.Exists(path))
        {
            Notice = $"파일을 찾을 수 없습니다.\n{path}";
            return;
        }

        try
        {
            // 지문은 파일 앞뒤 2MB 만 읽으므로 수 GB 파일에서도 즉시 끝난다.
            var (fingerprint, size) = await Core.Library.FileFingerprint.ComputeAsync(path);
            var record = await _library.OpenOrRegisterAsync(path, fingerprint, size);

            if (IsPlayerScreen) await Player.CloseAsync();
            Screen = Screen.Player;
            await Player.OpenAsync(record);

            if (Player.ErrorMessage is { } error)
            {
                // 열기에 실패하면 검은 화면에 두지 않고 라이브러리로 돌려보낸다.
                Screen = Screen.Library;
                Notice = error;

                // 방금 처음 등록된, 열리지도 않는 파일은 목록에 남기지 않는다. 등록 폴더 안의 파일은
                // 어차피 스캔이 다시 올리므로 그대로 두고 카드에서 보이게 한다.
                var inFolder = (await _library.GetFoldersAsync()).Any(f => FolderRules.Contains(f.Path, path));
                if (record is { LastPlayedAt: null, AvOffsetMs: null } && !inFolder)
                    await _library.RemoveAsync(record.Id);

                await Library.RefreshAsync();
            }
        }
        catch (IOException ex)
        {
            Notice = $"파일을 읽을 수 없습니다: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task BackToLibraryAsync()
    {
        if (!IsPlayerScreen) return;
        await Player.CloseAsync();
        IsFullscreen = false;
        Screen = Screen.Library;
        await Library.RefreshAsync();
    }

    [RelayCommand]
    private async Task OpenFileDialog()
    {
        var dialog = new OpenFileDialog { Title = "영상 열기", Filter = AppPaths.OpenFileFilter };
        if (dialog.ShowDialog() == true) await OpenPathAsync(dialog.FileName);
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    [RelayCommand]
    private void OpenSettings()
    {
        var window = new SettingsWindow(new SettingsViewModel(KeyMap, OffsetPresets)) { Owner = System.Windows.Application.Current.MainWindow };
        window.ShowDialog();
    }

    /// <summary>재생 중 동작 키. 플레이어 화면에서만 의미가 있다.</summary>
    public void HandlePlaybackAction(PlaybackAction action, bool isRepeat)
    {
        if (!IsPlayerScreen) return;

        if (action == PlaybackAction.ToggleFullscreen)
        {
            if (!isRepeat) ToggleFullscreen();   // 누르고 있어도 한 번만
            return;
        }

        Player.ExecuteAction(action, isRepeat);
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        if (IsPlayerScreen) IsFullscreen = !IsFullscreen;
    }

    /// <summary>입력 가드에서 온 키. 일시정지 상태에서만 도달한다.</summary>
    public bool HandleShortcut(KeyEventContext ctx)
    {
        if (ctx.EffectiveKey == Key.O && ctx.Modifiers == ModifierKeys.Control)
        {
            OpenFileDialogCommand.Execute(null);
            return true;
        }

        return IsPlayerScreen && Player.HandleShortcut(ctx);
    }

    private void SaveSettingsSoon()
    {
        _audioSaveDelay.Stop();
        _audioSaveDelay.Start();
    }

    private void SaveSettings()
    {
        _audioSaveDelay.Stop();
        try { _settings.Save(KeyMap, Player.Volume, Player.IsMuted, OffsetPresets); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = $"설정을 저장하지 못했습니다: {ex.Message}";
        }
    }

    /// <summary>앱 종료 직전. 위치와 썸네일, 아직 쓰지 않은 볼륨 설정을 남긴다.</summary>
    public Task ShutdownAsync()
    {
        if (_audioSaveDelay.IsEnabled) SaveSettings();
        return IsPlayerScreen ? Player.CloseAsync() : Task.CompletedTask;
    }
}
