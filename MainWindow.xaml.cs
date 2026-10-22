using Microsoft.UI.Xaml;
using SyncDroidWinUI.Services;
using SyncDroidWinUI.Views;

namespace SyncDroidWinUI
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            this.InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);

            SubtitleTextBlock.Text = LocalizationService.Instance.AppSubtitle;
            LocalizationService.Instance.LanguageChanged += (_, _) =>
            {
                SubtitleTextBlock.Text = LocalizationService.Instance.AppSubtitle;
            };

            try
            {
                var iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    this.AppWindow.SetIcon(iconPath);
                }
                this.AppWindow.Resize(new Windows.Graphics.SizeInt32(940, 720));
            }
            catch { }

            RootFrame.Navigate(typeof(MainPage));
        }
    }
}
