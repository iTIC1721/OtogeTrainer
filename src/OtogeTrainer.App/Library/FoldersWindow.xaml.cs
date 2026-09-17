using System.Windows;
using Microsoft.Win32;

namespace OtogeTrainer.App.Library;

public partial class FoldersWindow : Window
{
    public FoldersWindow(FoldersViewModel vm)
    {
        InitializeComponent();
        DataContext = ViewModel = vm;

        vm.PickFolder = () =>
        {
            var dialog = new OpenFolderDialog { Title = "녹화 폴더 선택" };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };

        vm.ConfirmRemove = item => MessageBox.Show(
            this,
            $"{item.Path}\n\n이 폴더의 등록을 해제할까요?\n파일은 지우지 않습니다. 한 번도 열지 않은 영상만 목록에서 빠집니다.",
            "폴더 등록 해제", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

        vm.PickTargetDirectory = (tree, current) =>
        {
            var picker = new DirectoryPickerWindow(
                "새 영상 넣을 디렉토리", "이 녹화 폴더에서 새로 찾은 영상을 어디에 넣을까요?", tree, current) { Owner = this };
            return picker.ShowDialog() == true ? new DirectoryPick(picker.SelectedDirectoryId) : null;
        };

        vm.ConfirmClearExclusions = count => MessageBox.Show(
            this,
            $"목록에서 제거했던 영상 {count}개를 다시 포함할까요?\n\n녹화 폴더 안에 있는 파일은 다음 스캔에서 다시 올라옵니다. 예전 북마크 · 오프셋 · 태그는 돌아오지 않습니다.",
            "제외한 영상 다시 포함", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

        Loaded += async (_, _) => await vm.LoadAsync();
    }

    internal FoldersViewModel ViewModel { get; }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
