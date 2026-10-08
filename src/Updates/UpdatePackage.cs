using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit.Updates;

public sealed class UpdateManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Product { get; set; } = "rika-translator";
    public string Version { get; set; } = "";
    public string MinimumVersion { get; set; } = "0.4.0";
    public string Architecture { get; set; } = "win-x64";
    public int DataFormatVersion { get; set; } = 1;
    public string Layout { get; set; } = "flat";
    public List<UpdateFile> RemoveFiles { get; set; } = [];
    public string ReleaseNotes { get; set; } = "";
    public List<UpdateFile> Files { get; set; } = [];
}

public sealed class UpdateFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public static class UpdatePackage
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public const long MaxArchiveSize = 1024L * 1024 * 1024;
    public const long MaxExpandedSize = 3L * 1024 * 1024 * 1024;
    public static readonly string[] RequiredFiles = ["RikaTranslator.exe", "RikaTranslator.dll", "RikaTranslator.deps.json", "RikaTranslator.runtimeconfig.json", "RikaUpdater.exe", "RikaUpdater.dll", "RikaUpdater.deps.json", "RikaUpdater.runtimeconfig.json", "wwwroot/index.html", "wwwroot/app.js", "wwwroot/styles.css", "hostpolicy.dll", "hostfxr.dll", "coreclr.dll", "System.Private.CoreLib.dll"];
    public static string[] RequiredFor(string layout) => layout == "runtime" ? RequiredFiles.Select(p => p.EndsWith(".exe") || p.StartsWith("wwwroot/") ? p : "runtime/" + p).ToArray() : RequiredFiles;
    public static string ApplicationDirectory => Environment.ProcessPath is { } executable && Path.GetFileNameWithoutExtension(executable) is "RikaTranslator" or "RikaUpdater" ? Path.GetDirectoryName(executable)! : AppContext.BaseDirectory;
    public static string DetectLayout(string root) => File.Exists(Path.Combine(root, "runtime", "RikaTranslator.dll")) ? "runtime" : "flat";

    public static Version ParseVersion(string text)
    {
        if (!Regex.IsMatch(text ?? "", @"^\d{1,5}\.\d{1,5}\.\d{1,5}$") || !System.Version.TryParse(text, out var version))
            throw new InvalidDataException("更新包版本格式无效。");
        return version;
    }

    public static void CheckRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 500 || path.Contains('\\') || path.StartsWith('/') || path.Any(c => char.IsControl(c) || ":*?\"<>|".Contains(c)))
            throw new InvalidDataException("更新包包含无效路径。");
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0];
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                throw new InvalidDataException("更新包路径不兼容 Windows。");
        }
        if (parts[0].Equals("data", StringComparison.OrdinalIgnoreCase) || parts[0].StartsWith('.') || parts[0].Equals("updates", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包不能包含用户数据或更新备份。");
    }

    public static bool Within(string root, string path)
    {
        root = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        path = System.IO.Path.GetFullPath(path);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string Target(string root, string relative)
    {
        CheckRelativePath(relative);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!Within(root, full)) throw new InvalidDataException("更新文件超出程序目录。");
        RejectLinks(full);
        return full;
    }

    public static void RejectLinks(string path)
    {
        for (string? current = System.IO.Path.GetFullPath(path); current != null; current = System.IO.Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新路径不能包含符号链接或目录联接。"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static async Task<UpdateManifest> ValidateAsync(string path, string currentVersion, CancellationToken ct = default)
    {
        RejectLinks(path);
        var size = new FileInfo(path).Length;
        if (size is < 22 or > MaxArchiveSize) throw new InvalidDataException("更新包大小无效，最大支持 1 GB。");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count is < 2 or > 5001) throw new InvalidDataException("更新包文件数量无效。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("更新包不能包含符号链接。");
            if (entry.FullName.EndsWith('/'))
            {
                if (entry.FullName != "payload/") CheckRelativePath(entry.FullName.TrimEnd('/'));
                continue;
            }
            CheckRelativePath(entry.FullName);
            if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("更新包包含重复文件路径。");
        }
        if (!entries.TryGetValue("rika-update.json", out var manifestEntry) || manifestEntry.FullName != "rika-update.json")
            throw new InvalidDataException("这不是 Rika Translator 更新包。请选择包含更新信息的专用 ZIP 更新包。");
        if (manifestEntry.Length > 2 * 1024 * 1024) throw new InvalidDataException("更新信息文件过大。");
        UpdateManifest manifest;
        try
        {
            using var stream = manifestEntry.Open();
            using var buffer = new MemoryStream();
            await CopyBoundedAsync(stream, buffer, manifestEntry.Length, ct);
            manifest = JsonSerializer.Deserialize<UpdateManifest>(buffer.ToArray(), Json) ?? throw new InvalidDataException("更新信息为空。");
        }
        catch (JsonException) { throw new InvalidDataException("更新信息不是有效的 JSON。"); }
        if (manifest.FormatVersion != 1 || manifest.Product != "rika-translator" || manifest.DataFormatVersion != 1)
            throw new InvalidDataException("更新包的产品或数据格式不兼容。");
        if (manifest.Architecture != "win-x64") throw new InvalidDataException("请选择 Windows x64 更新包。");
        var current = ParseVersion(currentVersion);
        var target = ParseVersion(manifest.Version);
        var minimum = ParseVersion(manifest.MinimumVersion);
        if (target <= current) throw new InvalidDataException("更新包版本必须高于当前版本，不能重复安装或降级。");
        if (minimum > current || minimum > target) throw new InvalidDataException($"此更新包要求当前版本至少为 {manifest.MinimumVersion}。");
        if (manifest.Layout is not ("flat" or "runtime") || manifest.Layout == "runtime" && minimum < new Version(0, 5, 1)) throw new InvalidDataException("此目录布局要求先更新到 0.5.1 或更高版本。");
        if (string.IsNullOrWhiteSpace(manifest.ReleaseNotes) || manifest.ReleaseNotes.Length > 20000 || manifest.Files == null || manifest.Files.Count is < 1 or > 5000)
            throw new InvalidDataException("更新包缺少有效的更新说明或程序文件清单。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            CheckRelativePath(file.Path);
            if (!names.Add(file.Path)) throw new InvalidDataException("程序文件清单包含重复路径。");
            if (file.Size is < 0 or > MaxArchiveSize || !Regex.IsMatch(file.Sha256 ?? "", "^[a-f0-9]{64}$")) throw new InvalidDataException("文件校验信息无效。");
            total = checked(total + file.Size);
            if (total > MaxExpandedSize) throw new InvalidDataException("更新包展开后过大。");
            if (!entries.TryGetValue("payload/" + file.Path, out var entry) || entry.FullName != "payload/" + file.Path || entry.Length != file.Size)
                throw new InvalidDataException("更新包缺少文件或文件长度不符：" + file.Path);
            using var stream = entry.Open();
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920]; long read = 0; int count;
            while ((count = await stream.ReadAsync(buffer, ct)) != 0)
            {
                read += count;
                if (read > file.Size) throw new InvalidDataException("更新包文件超出声明长度：" + file.Path);
                hasher.AppendData(buffer, 0, count);
            }
            if (read != file.Size) throw new InvalidDataException("更新包文件长度不符：" + file.Path);
            var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (hash != file.Sha256) throw new InvalidDataException("更新包文件校验失败：" + file.Path);
        }
        if (RequiredFor(manifest.Layout).Any(required => !names.Contains(required))) throw new InvalidDataException("更新包缺少启动程序、独立更新程序或运行组件。");
        if (manifest.RemoveFiles == null || manifest.RemoveFiles.Count > 5000 || manifest.Layout == "flat" && manifest.RemoveFiles.Count != 0) throw new InvalidDataException("旧运行文件清单无效。");
        var removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.RemoveFiles)
        {
            CheckRelativePath(file.Path);
            if (!removals.Add(file.Path) || names.Contains(file.Path) || !names.Contains("runtime/" + file.Path) || file.Size is < 0 or > MaxArchiveSize || !Regex.IsMatch(file.Sha256 ?? "", "^[a-f0-9]{64}$")) throw new InvalidDataException("旧运行文件必须有对应的新位置和有效校验值。");
        }
        if (entries.Count != manifest.Files.Count + 1) throw new InvalidDataException("更新包包含未列入清单的文件。");
        return manifest;
    }

    public static async Task ExtractAsync(string archive, UpdateManifest manifest, string destination, CancellationToken ct)
    {
        RejectLinks(destination);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Target(destination, file.Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            using var input = zip.GetEntry("payload/" + file.Path)!.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyBoundedAsync(input, output, file.Size, ct);
            await output.FlushAsync(ct);
        }
        foreach (var file in manifest.Files)
            if (Hash(Target(destination, file.Path)) != file.Sha256) throw new InvalidDataException("解包后文件校验失败：" + file.Path);
    }

    public static async Task CopyBoundedAsync(Stream source, Stream destination, long length, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            total += count;
            if (total > length) throw new InvalidDataException("文件超出声明长度。");
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (total != length) throw new InvalidDataException("文件长度与声明不符。");
    }

    public static void WriteJson<T>(string path, T value)
    {
        RejectLinks(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        RejectLinks(temp);
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
}
