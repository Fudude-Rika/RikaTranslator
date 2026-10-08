using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit;

/// <summary>Resolves the game binary, rather than Unreal's short-lived bootstrap executable.</summary>
public static class UnrealLayout
{
    public const string ModRelative = "ue4ss/Mods/GameTranslateToolkit";
    private static readonly ConcurrentDictionary<string, (long Length, long Time, string Version)> Versions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex VersionMarker = new(@"Unreal Engine ([45]\.\d{1,2}\.\d{1,3})", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string? ShippingExecutable(string root, string executable)
    {
        root = Path.GetFullPath(root);
        var candidates = new List<string>();
        void AddDirectory(string bin)
        {
            if (!Directory.Exists(bin) || !SafePath(root, bin)) return;
            candidates.AddRange(Directory.EnumerateFiles(bin, "*-Win64-Shipping.exe").Take(32).Where(p => SafePath(root, p)));
        }
        AddDirectory(Path.Combine(root, "Binaries", "Win64"));
        if (Directory.Exists(root))
            foreach (var child in Directory.EnumerateDirectories(root).Take(64))
                if (!Path.GetFileName(child).Equals("Engine", StringComparison.OrdinalIgnoreCase)) AddDirectory(Path.Combine(child, "Binaries", "Win64"));
        if (File.Exists(executable) && SafePath(root, executable) && Path.GetFileName(executable).EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.GetFullPath(executable));
        var unique = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var expected = Path.GetFileNameWithoutExtension(executable) + "-Win64-Shipping.exe";
        var matches = unique.Where(p => Path.GetFileName(p).Equals(expected, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : unique.Length == 1 ? unique[0] : null;
    }

    public static string EngineVersion(string executable)
    {
        var info = new FileInfo(executable);
        if (Versions.TryGetValue(executable, out var cached) && cached.Length == info.Length && cached.Time == info.LastWriteTimeUtc.Ticks) return cached.Version;
        using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[2 * 1024 * 1024 + 128];
        var carry = 0; long remaining = Math.Min(stream.Length, 256L * 1024 * 1024);
        var version = "";
        while (remaining > 0)
        {
            var count = stream.Read(buffer, carry, (int)Math.Min(buffer.Length - carry, remaining));
            if (count == 0) break;
            remaining -= count;
            // The native marker is UTF-16LE. Check both alignments and ASCII without allocating the full binary.
            foreach (var text in new[] { Encoding.Unicode.GetString(buffer, 0, carry + count), Encoding.Unicode.GetString(buffer, 1, Math.Max(0, carry + count - 1)), Encoding.ASCII.GetString(buffer, 0, carry + count) })
            {
                var match = VersionMarker.Match(text);
                if (match.Success) { version = match.Groups[1].Value; break; }
            }
            if (version.Length > 0) break;
            var total = carry + count;
            carry = Math.Min(128, total);
            Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
        }
        Versions[executable] = (info.Length, info.LastWriteTimeUtc.Ticks, version);
        return version;
    }

    // This release deliberately exposes the route validated on UE 5.7; other versions remain visible as detection-only.
    public static bool SupportedVersion(string version) => Version.TryParse(version, out var v) && v.Major == 5 && v.Minor == 7;

    public static string? ModDirectory(GameRecord game)
    {
        var exe = ShippingExecutable(game.Directory, game.ExecutablePath);
        return exe == null ? null : Path.Combine(Path.GetDirectoryName(exe)!, ModRelative.Replace('/', Path.DirectorySeparatorChar));
    }

    public static bool IsRunning(GameRecord game)
    {
        try
        {
            var expected = ShippingExecutable(game.Directory, game.ExecutablePath);
            if (expected == null) return false;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
                using (process)
                    try { if (!process.HasExited && process.MainModule?.FileName is { } file && Path.GetFullPath(file).Equals(expected, StringComparison.OrdinalIgnoreCase)) return true; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }

    public static bool SafePath(string root, string path)
    {
        try
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetFullPath(path);
            if (!current.Equals(root, StringComparison.OrdinalIgnoreCase) && !current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                current = Path.GetDirectoryName(current);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
