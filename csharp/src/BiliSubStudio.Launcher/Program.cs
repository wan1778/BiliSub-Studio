using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BiliSubStudio.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = AppContext.BaseDirectory;
        var runtimeDirectory = Path.Combine(root, "Runtime");
        var executable = Path.Combine(runtimeDirectory, "BiliSubStudio.exe");
        if (!File.Exists(executable))
        {
            MessageBoxW(IntPtr.Zero,
                "Không tìm thấy Runtime\\BiliSubStudio.exe. Hãy cài đặt lại BiliSub Studio.",
                "BiliSub Studio",
                0x00000010);
            return 2;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = runtimeDirectory,
            };
            foreach (var argument in args) startInfo.ArgumentList.Add(argument);
            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Không thể khởi động BiliSub Studio.");
            return 0;
        }
        catch (Exception error)
        {
            MessageBoxW(IntPtr.Zero,
                "Không thể mở BiliSub Studio.\n\n" + error.Message,
                "BiliSub Studio",
                0x00000010);
            return 3;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
