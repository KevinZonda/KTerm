using KevinZonda.Terminal.Interop;
using KevinZonda.Terminal.Terminal;

namespace KevinZonda.Terminal;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (ConsoleThemeHelper.TryRun(args, out var helperExitCode))
        {
            Environment.ExitCode = helperExitCode;
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);

        string startingDirectory;
        try
        {
            if (args.Length > 1)
            {
                throw new ArgumentException("KevinZonda Terminal accepts at most one starting directory.");
            }

            startingDirectory = Path.GetFullPath(
                args.Length == 0 ? Environment.CurrentDirectory : args[0]);
            if (!Directory.Exists(startingDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"The starting directory does not exist:\n\n{startingDirectory}");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                DirectoryNotFoundException or
                PathTooLongException)
        {
            MessageBox.Show(
                exception.Message,
                "KevinZonda Terminal startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // Alert when the cached OpenConsole.exe fails its integrity check: the
        // file may be corrupted or tampered with, and it is never executed.
        // A "no" decision is remembered so later sessions in this run don't nag.
        var integrityDeclined = false;
        ConHost.IntegrityConflictHandler = path =>
        {
            if (integrityDeclined)
            {
                return false;
            }

            var choice = MessageBox.Show(
                $"缓存的终端主机文件与 KevinZonda Terminal 内置副本不一致：\n\n{path}\n\n" +
                "可能是磁盘损坏，也可能是被其他程序篡改。KevinZonda Terminal 不会使用这个文件。\n\n" +
                "是否从内置副本重新释放？\n\n" +
                "是：重新释放并继续\n否：本次运行回退到系统控制台（部分终端功能受限）",
                "KevinZonda Terminal security warning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (choice == DialogResult.No)
            {
                integrityDeclined = true;
                return false;
            }
            return true;
        };

        Application.Run(new MainForm(startingDirectory));
    }
}
