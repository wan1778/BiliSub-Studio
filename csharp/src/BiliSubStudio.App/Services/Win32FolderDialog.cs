using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace BiliSubStudio.App.Services;

// Hop chon thu muc Win32 (IFileDialog + FOS_PICKFOLDERS) mo DUNG thu muc hien tai.
// Ly do: WinRT FolderPicker khong co API dat thu muc bat dau — luc nao cung mo
// Downloads roi bat user tu mo den E:\facebook\... qua cac thu muc quet cham.
// Dialog chay tren UI thread (DispatcherQueue), that bai bat ky buoc nao cung
// nem de caller fallback ve WinRT cu (khong bao gio treo/mat hon slow hien tai).
internal static class Win32FolderDialog
{
    private const uint FOS_PICKFOLDERS = 0x20;
    private const uint FOS_FORCEFILESYSTEM = 0x40;
    private const uint FOS_NOVALIDATE = 0x100;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoCreateInstance(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IFileDialog fileDialog);

    // Vtable IModalWindow + IFileDialog theo dung thu tu, toi GetResult.
    // IID XAC MINH runtime (QI-OK tren object that, 2026-09-14).
    // Cac slot khong dung van phai khai bao de giu dung offset.
    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFilterIndex(uint iFilter);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
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
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    public static Task<string?> PickFolderAsync(DispatcherQueue dispatcher, nint hwnd, string? initialDirectory, string title)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try { completion.TrySetResult(ShowOnUiThread(hwnd, initialDirectory, title)); }
            catch (Exception error) { completion.TrySetException(error); }
        }))
        {
            completion.TrySetException(new InvalidOperationException("Không đưa được hộp chọn thư mục lên UI thread."));
        }
        return completion.Task;
    }

    private static string? ShowOnUiThread(nint hwnd, string? initialDirectory, string title)
    {
        CoCreateInstance(CLSID_FileOpenDialog, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */,
            new Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), out IFileDialog dialog);
        try
        {
            dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOVALIDATE);
            dialog.SetTitle(title);
            dialog.SetOkButtonLabel("Chọn thư mục");
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero,
                    new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), out IShellItem initial);
                try { dialog.SetFolder(initial); }
                catch { }
                finally { Marshal.ReleaseComObject(initial); }
            }
            // Cancel (HRESULT 0x800704C7) la binh thuong: tra null nhu WinRT (folder null).
            if (dialog.Show(hwnd) != 0) return null;
            dialog.GetResult(out IShellItem result);
            try
            {
                result.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            finally { Marshal.ReleaseComObject(result); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }
}
