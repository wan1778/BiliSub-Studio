using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BiliSubStudio.Core.IO;

/// <summary>
/// Windows DPAPI unprotected bytes (Chromium browsers keep their cookie values
/// and master key under DPAPI on Windows). Mirrors the P/Invoke style already
/// used by <see cref="BiliSubStudio.Core.Authentication.SessionStore"/>: a small
/// owned surface, no extra dependency.
/// </summary>
public static class WindowsDataProtection
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static byte[] Protect(byte[] data)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("DPAPI chỉ khả dụng trên Windows.");
        if (data.Length == 0) return data;
        var inputBlob = new DataBlob(data);
        var outputBlob = new DataBlob();
        try
        {
            if (!CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref outputBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptProtectData");
            var plain = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, plain, 0, plain.Length);
            return plain;
        }
        finally
        {
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
            inputBlob.Release();
        }
    }

    public static byte[] Unprotect(byte[] data)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("DPAPI chỉ khả dụng trên Windows.");
        if (data.Length == 0) return data;        var inputBlob = new DataBlob(data);
        var outputBlob = new DataBlob();
        try
        {
            if (!CryptUnprotectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref outputBlob))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CryptUnprotectData");
            var plain = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, plain, 0, plain.Length);
            return plain;
        }
        finally
        {
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
            inputBlob.Release();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;

        public DataBlob(byte[] data)
        {
            Data = Marshal.AllocHGlobal(data.Length);
            Length = data.Length;
            Marshal.Copy(data, 0, Data, Length);
        }

        public void Release()
        {
            if (Data != IntPtr.Zero)
            {
                LocalFree(Data);
                Data = IntPtr.Zero;
                Length = 0;
            }
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
