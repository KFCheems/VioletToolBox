using System.IO;
using System.Runtime.InteropServices;

namespace VioletToolBox.Core;

/// <summary>
/// Locates external Android/flash tools on Linux.
/// Search order: $VIOLET_TOOLBOX_TOOLS → &lt;app dir&gt;/tools → ~/.local/share/VioletToolBox/tools → $PATH.
/// The same names are used as their upstream Windows counterparts (adb, fastboot, …),
/// minus the .exe suffix, so bundled tool folders stay drop-in compatible.
/// </summary>
public static class ToolLocator
{
    private static readonly string[] KnownNames =
    [
        "adb", "fastboot", "scrcpy", "aria2c", "7z", "lpmake", "magiskboot",
        "heimdall", "mke2fs", "e2fsdroid", "simg2img", "append2simg"
    ];

    /// <summary>Directories probed for bundled tools, most specific first.</summary>
    public static IReadOnlyList<string> SearchRoots()
    {
        var roots = new List<string>();

        var env = Environment.GetEnvironmentVariable("VIOLET_TOOLBOX_TOOLS");
        if (!string.IsNullOrWhiteSpace(env))
            roots.AddRange(env.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        roots.Add(Path.Combine(AppContext.BaseDirectory, "tools"));

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        roots.Add(Path.Combine(dataHome, "VioletToolBox", "tools"));

        return roots.Where(Directory.Exists).ToList();
    }

    /// <summary>Finds a tool; returns null when absent. Never throws.</summary>
    public static string? Find(string name)
    {
        foreach (var root in SearchRoots())
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(root, name),
                         Path.Combine(root, name, name),
                         Path.Combine(root, "platform-tools", name),
                         Path.Combine(root, "bin", name)
                     })
            {
                if (IsExecutable(candidate)) return candidate;
            }
        }

        // System PATH as the final, most common source (dnf install android-tools …).
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (IsExecutable(candidate)) return candidate;
        }

        return null;
    }

    public static bool TryFind(string name, out string? path, out string hint)
    {
        path = Find(name);
        if (path is not null)
        {
            hint = path;
            return true;
        }
        hint = $"未找到 {name}。可安装系统包（如 sudo dnf install android-tools），"
             + $"或将工具放入 tools/ 目录并设置 VIOLET_TOOLBOX_TOOLS。";
        return false;
    }

    /// <summary>One-line availability report for the diagnostics page.</summary>
    public static IReadOnlyList<(string Name, string? Path)> Describe()
        => KnownNames.Select(n => (n, Find(n))).ToList();

    private static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var mode = File.GetUnixFileMode(path);
            return mode.HasFlag(UnixFileMode.UserExecute)
                || mode.HasFlag(UnixFileMode.GroupExecute)
                || mode.HasFlag(UnixFileMode.OtherExecute);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ensures the executable bit on a bundled tool; Linux extracts of archives often drop it.</summary>
    public static void EnsureExecutable(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            if (!mode.HasFlag(UnixFileMode.UserExecute))
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Non-fatal: the spawn will surface a clear error if it truly cannot run.
        }
    }
}
