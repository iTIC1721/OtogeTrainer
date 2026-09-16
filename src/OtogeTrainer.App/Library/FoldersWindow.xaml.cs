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

        Loaded += async (_, _) => await vm.LoadAsync();
    }

    internal FoldersViewModel ViewModel { get; }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
