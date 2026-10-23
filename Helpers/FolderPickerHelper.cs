using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SyncDroidWinUI.Helpers
{
    public static class FolderPickerHelper
    {
        [ComImport]
        [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig]
            int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(FILEOPENDIALOGOPTIONS fos);
            void GetOptions(out FILEOPENDIALOGOPTIONS pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientGuid();
            void SetFilter(IntPtr pFilter);
        }

        [Flags]
        private enum FILEOPENDIALOGOPTIONS : uint
        {
            FOS_PICKFOLDERS = 0x00000020,
            FOS_FORCEFILESYSTEM = 0x00000040,
            FOS_ALLOWMULTISELECT = 0x00000200,
            FOS_PATHMUSTEXIST = 0x00000800,
            FOS_FILEMUSTEXIST = 0x00001000,
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        private enum SIGDN : uint
        {
            SIGDN_FILESYSPATH = 0x80058000,
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAMEW
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            public string? lpstrInitialDir;
            public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string? lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string? lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

        private const int OFN_ALLOWMULTISELECT = 0x00000200;
        private const int OFN_EXPLORER = 0x00080000;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_NOCHANGEDIR = 0x00000008;

        public static string? PickFolder(IntPtr hwnd)
        {
            var clsid = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
            var type = Type.GetTypeFromCLSID(clsid);
            if (type == null) return null;

            var dialog = (IFileDialog)Activator.CreateInstance(type)!;
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST);
            dialog.SetTitle("Select Folder");

            int hr = dialog.Show(hwnd);
            if (hr == 0) // S_OK
            {
                dialog.GetResult(out var item);
                item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
                return path;
            }

            return null; // User cancelled or closed
        }

        public static List<string> PickFiles(IntPtr hwnd)
        {
            var results = new List<string>();
            const int maxChars = 65536;
            IntPtr fileBuffer = Marshal.AllocHGlobal(maxChars * 2);
            try
            {
                // Initialize first two bytes to null char (\0)
                Marshal.WriteInt16(fileBuffer, 0, 0);

                var ofn = new OPENFILENAMEW
                {
                    lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                    hwndOwner = hwnd,
                    lpstrFilter = "All Files (*.*)\0*.*\0\0",
                    nFilterIndex = 1,
                    lpstrFile = fileBuffer,
                    nMaxFile = maxChars,
                    lpstrTitle = "Select Files",
                    Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_ALLOWMULTISELECT | OFN_NOCHANGEDIR
                };

                if (GetOpenFileNameW(ref ofn))
                {
                    // Parse double-null-terminated Unicode string buffer
                    var parts = new List<string>();
                    int offset = 0;
                    while (offset < maxChars)
                    {
                        string? s = Marshal.PtrToStringUni(IntPtr.Add(fileBuffer, offset * 2));
                        if (string.IsNullOrEmpty(s))
                        {
                            break;
                        }
                        parts.Add(s);
                        offset += s.Length + 1;
                    }

                    if (parts.Count == 1)
                    {
                        results.Add(parts[0]);
                    }
                    else if (parts.Count > 1)
                    {
                        string dir = parts[0];
                        for (int i = 1; i < parts.Count; i++)
                        {
                            results.Add(Path.Combine(dir, parts[i]));
                        }
                    }
                }
            }
            catch
            {
                // Fallback to single-file IFileDialog if GetOpenFileNameW fails
                try
                {
                    var clsid = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
                    var type = Type.GetTypeFromCLSID(clsid);
                    if (type != null)
                    {
                        var dialog = (IFileDialog)Activator.CreateInstance(type)!;
                        dialog.GetOptions(out var options);
                        dialog.SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST | FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST);
                        dialog.SetTitle("Select File");
                        if (dialog.Show(hwnd) == 0)
                        {
                            dialog.GetResult(out var item);
                            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
                            if (!string.IsNullOrEmpty(path))
                            {
                                results.Add(path);
                            }
                        }
                    }
                }
                catch { }
            }
            finally
            {
                Marshal.FreeHGlobal(fileBuffer);
            }

            return results;
        }
    }
}
