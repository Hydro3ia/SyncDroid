using Microsoft.UI.Xaml;
using SyncDroidWinUI.Services;
using SyncDroidWinUI.Views;
using System;
using System.Runtime.InteropServices;

namespace SyncDroidWinUI
{
    public sealed partial class MainWindow : Window
    {
        private const int DefaultWindowWidthDip = 980;
        private const int DefaultWindowHeightDip = 750;
        private const int MinWindowWidthDip = 760;
        private const int MinWindowHeightDip = 620;
        private const uint WM_GETMINMAXINFO = 0x0024;
        private static readonly UIntPtr SubclassId = (UIntPtr)1001;

        private IntPtr _hwnd;
        private SUBCLASSPROC? _subclassProc;

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

                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                double scale = 1.0;
                if (_hwnd != IntPtr.Zero)
                {
                    uint dpi = GetDpiForWindow(_hwnd);
                    if (dpi > 0)
                    {
                        scale = dpi / 96.0;
                    }

                    _subclassProc = WindowSubclassProc;
                    SetWindowSubclass(_hwnd, _subclassProc, SubclassId, UIntPtr.Zero);
                }

                this.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)Math.Round(DefaultWindowWidthDip * scale),
                    (int)Math.Round(DefaultWindowHeightDip * scale)));
            }
            catch { }

            RootFrame.Navigate(typeof(MainPage));

            this.Closed += MainWindow_Closed;
        }

        private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (uMsg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(hWnd);
                double scale = dpi > 0 ? dpi / 96.0 : 1.0;

                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMinTrackSize.x = (int)Math.Round(MinWindowWidthDip * scale);
                mmi.ptMinTrackSize.y = (int)Math.Round(MinWindowHeightDip * scale);
                Marshal.StructureToPtr(mmi, lParam, false);
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            try
            {
                if (_hwnd != IntPtr.Zero && _subclassProc != null)
                {
                    RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
                    _subclassProc = null;
                }
            }
            catch { }

            try { CompanionSyncService.Instance.StopServer(); } catch { }
            try { LocalSendService.Instance.StopServer(); } catch { }
            try { AdbSyncService.Instance.ShutdownOnAppExit(); } catch { }
        }

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);
    }
}
