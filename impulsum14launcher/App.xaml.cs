using System.Diagnostics;
using System.Windows;

namespace ImpulsumLauncher14;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!IsDotNet8SdkInstalled())
        {
            MessageBox.Show(
                "Microsoft .NET 8 SDK is not installed. Please run the setup as administrator and install .NET 8 SDK before launching the launcher.",
                "Missing .NET 8 SDK",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown();
            return;
        }
    }

    private static bool IsDotNet8SdkInstalled()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-sdks",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                return false;

            var output = (stdout + Environment.NewLine + stderr).Trim();
            if (string.IsNullOrWhiteSpace(output))
                return false;

            foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("8.", StringComparison.Ordinal) || trimmed.StartsWith("8.0", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
