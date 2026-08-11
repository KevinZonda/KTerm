namespace KevinZonda.KTerm;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);

        string startingDirectory;
        try
        {
            if (args.Length > 1)
            {
                throw new ArgumentException("KTerm accepts at most one starting directory.");
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
                "KTerm startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Application.Run(new MainForm(startingDirectory));
    }
}
