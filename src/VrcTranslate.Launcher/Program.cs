using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VrcTranslate.Launcher;

internal static partial class Program
{
    private const string DataDirectoryVariable = "VRC_TRANSLATE_DATA_DIR";

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint window, string text, string caption, uint type);

    [STAThread]
    private static int Main()
    {
        var root = AppContext.BaseDirectory;
        var programDirectory = Path.Combine(root, "程序文件");
        var executable = Path.Combine(programDirectory, "VrcTranslate.exe");
        if (!File.Exists(executable))
        {
            MessageBox(
                0,
                "找不到“程序文件”目录中的 VrcTranslate.exe。请完整解压发布包，不要单独移动启动程序。",
                "VRCTranslate 无法启动",
                0x10);
            return 2;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = programDirectory,
                UseShellExecute = false,
            };
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DataDirectoryVariable)))
                start.Environment[DataDirectoryVariable] = Path.Combine(root, "data");

            _ = Process.Start(start)
                ?? throw new InvalidOperationException("系统没有创建应用进程。");
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox(0, $"启动失败：{exception.Message}", "VRCTranslate 无法启动", 0x10);
            return 1;
        }
    }
}
