using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ImpulsumLauncher14.Services;

public class GameService
{
    private const string GameExe = "fifa14.exe";
    private const string ConfigExe = "fifasetup\\fifaconfig.exe";

    public string DetectedGameExe { get; private set; } = GameExe;

    public string FindDefaultPath()
    {
        string[] searchPaths =
        [
            @"C:\Program Files\Origin Games\FIFA 14\Game",
            @"C:\Program Files (x86)\Origin Games\FIFA 14\Game",
            @"C:\Program Files\EA Games\FIFA 14\Game",
            @"C:\Program Files (x86)\EA Games\FIFA 14\Game",
            @"C:\Games\FIFA 14\Game",
            @"C:\FIFA 14\Game",
        ];

        var regPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var hive in regPaths)
        {
            using var key = Registry.LocalMachine.OpenSubKey(hive);
            if (key == null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(sub);
                var displayName = subKey?.GetValue("DisplayName")?.ToString() ?? "";
                if (!displayName.Contains("FIFA 14", StringComparison.OrdinalIgnoreCase)) continue;
                var installPath = subKey?.GetValue("InstallLocation")?.ToString();
                if (string.IsNullOrEmpty(installPath)) continue;

                var gamePath = Path.Combine(installPath, "Game");
                if (Directory.Exists(gamePath)) return gamePath;

                if (Directory.Exists(installPath)) return installPath;
            }
        }

        foreach (var p in searchPaths)
        {
            if (Directory.Exists(p)) return p;
        }

        return string.Empty;
    }

    public bool ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!Directory.Exists(path)) return false;

        var main = Path.Combine(path, GameExe);
        if (File.Exists(main))
        {
            DetectedGameExe = GameExe;
            return true;
        }

        DetectedGameExe = GameExe;
        return false;
    }

    public string GetGameExePath(string gamePath)
    {
        ValidatePath(gamePath);
        return Path.Combine(gamePath, DetectedGameExe);
    }

    public Version? GetFileVersion(string gamePath)
    {
        var exePath = GetGameExePath(gamePath);
        if (!File.Exists(exePath)) return null;

        var info = FileVersionInfo.GetVersionInfo(exePath);

        var raw = (info.FileVersion ?? info.ProductVersion ?? "");
        if (!string.IsNullOrEmpty(raw))
        {
            raw = raw.Replace(',', '.');
            if (Version.TryParse(raw, out var v))
                return v;
        }

        if (info.FileMajorPart != 0 || info.FileMinorPart != 0 ||
            info.FileBuildPart != 0 || info.FilePrivatePart != 0)
            return new Version(info.FileMajorPart, info.FileMinorPart,
                               info.FileBuildPart, info.FilePrivatePart);

        return null;
    }

    public string ApplyDllPatches(string gamePath)
    {
        var patchesDir = Path.Combine(AppContext.BaseDirectory, "patches");
        var cardsSrc = Path.Combine(patchesDir, "CardsDLLzf.dll");
        var cardsDst = Path.Combine(gamePath, "CardsDLLzf.dll");

        var powSrc = Path.Combine(patchesDir, "powdllzf.dll");
        var powDst = Path.Combine(gamePath, "dlc", "dlc_powdll", "dlc", "powdll", "powdllzf.dll");

        var artSrc = Path.Combine(patchesDir, "artAssets");
        var artDst = Path.Combine(gamePath, "data", "ui", "external", "ion_fut", "artAssets");

        if (!File.Exists(cardsSrc))
            return $"Unable to patch CardsDLLzf.dll: source not found at {cardsSrc}";
        if (!File.Exists(powSrc))
            return $"Unable to patch powdllzf.dll: source not found at {powSrc}";

        try
        {
            File.Copy(cardsSrc, cardsDst, overwrite: true);

            var powDir = Path.GetDirectoryName(powDst)!;
            Directory.CreateDirectory(powDir);
            File.Copy(powSrc, powDst, overwrite: true);

            if (Directory.Exists(artSrc))
                CopyDirectory(artSrc, artDst);
        }
        catch (Exception ex)
        {
            return $"Failed to apply DLL patches: {ex.Message}";
        }

        return string.Empty;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }

    public async Task<ProcessResult> LaunchGameAsync(string gamePath)
    {
        var result = new ProcessResult();

        if (!ValidatePath(gamePath))
        {
            result.Success = false;
            result.ErrorMessage = "FIFA 14 not found at the specified path.";
            return result;
        }

        var exePath = GetGameExePath(gamePath);
        if (!File.Exists(exePath))
        {
            result.Success = false;
            result.ErrorMessage = "fifa14.exe was not found at " + exePath;
            return result;
        }

        try
        {
            using var gameProcess = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = gamePath,
                UseShellExecute = true,
                Verb = "open",
            });

            if (gameProcess == null)
            {
                result.Success = false;
                result.ErrorMessage = "Failed to start fifa14.exe.";
                return result;
            }

            result.Success = true;
            result.ProcessId = gameProcess.Id;

            while (!gameProcess.HasExited)
            {
                await Task.Delay(250);
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private static Process? FindGameProcess(string gamePath)
    {
        var expectedPath = Path.GetFullPath(Path.Combine(gamePath, GameExe));

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExe)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch
            {
                process.Dispose();
            }

            process.Dispose();
        }

        return null;
    }
}

public class ProcessResult
{
    public bool Success { get; set; }
    public int ProcessId { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}