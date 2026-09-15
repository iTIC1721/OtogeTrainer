using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OtogeTrainer.App.Library;
using OtogeTrainer.App.Player;
using OtogeTrainer.Core.Library;
using OtogeTrainer.Input;

namespace OtogeTrainer.App;

public enum Screen { Library, Player }

/// <summary>화면 전환과 "파일 → 플레이어" 흐름을 묶는다.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IVideoLibrary _library;

    public ShellViewModel(LibraryViewModel library, PlayerViewModel player, IVideoLibrary videoLibrary)
    {
        Library = library;
        Player = player;
        _library = videoLibrary;

        Library.OpenPath = OpenPathAsync;
        Library.Notify = message => Notice = message;
        Player.BackRequested += async (_, _) => await BackToLibraryAsync();
    }

    public LibraryViewModel Library { get; }
    public PlayerViewModel Player { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryScreen), nameof(IsPlayerScreen))]
    private Screen _screen = Screen.Library;

    /// <summary>화면 상단 알림 배너. 오류와 안내를 겸한다.</summary>
    [ObservableProperty] private string? _notice;

    public bool IsLibraryScreen => Screen == Screen.Library;
    public bool IsPlayerScreen => Screen == Screen.Player;

    /// <summary>입력 가드가 묻는다: 지금 키를 막아야 하는가. (FR-501)</summary>
    public bool IsPlaying => IsPlayerScreen && Player.IsPlaying;

    public async Task StartAsync() => await Library.RefreshAsync();

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

    /// <summary>앱 종료 직전. 위치와 썸네일을 남긴다.</summary>
    public Task ShutdownAsync() => IsPlayerScreen ? Player.CloseAsync() : Task.CompletedTask;
}
