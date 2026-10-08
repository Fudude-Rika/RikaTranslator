using System.Reflection;
using System.Text;

namespace GameTranslateToolkit;

public sealed partial class EngineService
{
    private string? ExistingUnityConfig(GameRecord game)
    {
        if (game.Engine != "Unity") return null;
        const string name = "BepInEx/config/AutoTranslatorConfig.ini";
        var manifest = ActiveManifest(game);
        var owned = manifest?.Files.FirstOrDefault(f => f.RelativePath.Replace('\\', '/') == name);
        if (owned != null)
        {
            if (!owned.Existed || owned.BackupFile == null) return null;
            var backup = SafeTarget(ManifestDirectory(game.Id), owned.BackupFile);
            var bytes = ReadFileBounded(backup, 1024 * 1024);
            if (Hash(bytes) != owned.OriginalHash) throw new InvalidOperationException("既有字体配置备份校验失败，未沿用。");
            return Encoding.UTF8.GetString(bytes).TrimStart('\ufeff');
        }
        var path = SafeTarget(game.Directory, name);
        return File.Exists(path) ? Encoding.UTF8.GetString(ReadFileBounded(path, 1024 * 1024)).TrimStart('\ufeff') : null;
    }
    private static string IniEntry(string text, string section, string key, string fallback = "")
    {
        var current = ""; var result = fallback;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim(); if (line.StartsWith(';') || line.StartsWith('#') || line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { current = line[1..^1]; continue; }
            var equals = line.IndexOf('=');
            if (equals > 0 && current.Equals(section, StringComparison.OrdinalIgnoreCase) && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) result = line[(equals + 1)..].Trim();
        }
        return result;
    }
    private Dictionary<string, string> ExistingFontValues(GameRecord game)
    {
        var text = ExistingUnityConfig(game) ?? throw new InvalidOperationException("没有发现原有 XUnity 字体配置。沿用已有字体与使用游戏原字体是两种不同模式。");
        return new[] { "OverrideFont", "OverrideFontTextMeshPro", "FallbackFontTextMeshPro" }.ToDictionary(k => k, k => IniEntry(text, "Behaviour", k));
    }
    public UnityMigrationPreview MigrationPreview(GameRecord game)
    {
        var preview = new UnityMigrationPreview();
        if (game.Engine != "Unity") return preview;
        try
        {
            var loader = SafeTarget(game.Directory, "BepInEx/core/" + (game.Backend == "IL2CPP" ? "BepInEx.Core.dll" : "BepInEx.dll"));
            if (File.Exists(loader)) preview.Loader = AssemblyName.GetAssemblyName(loader).FullName;
            var translator = SafeTarget(game.Directory, "BepInEx/plugins/XUnity.AutoTranslator/XUnity.AutoTranslator.Plugin.Core.dll");
            if (File.Exists(translator)) preview.Translator = AssemblyName.GetAssemblyName(translator).FullName;
            var config = ExistingUnityConfig(game);
            if (config == null) { preview.Notes.Add("没有发现安装前的 XUnity 配置。可选择自动中文字体或游戏原字体。"); return preview; }
            preview.Available = true; preview.ConfigFound = true; preview.FontValues = ExistingFontValues(game);
            preview.TargetLanguage = IniEntry(config, "General", "Language", "en");
            preview.CacheMatchesLanguage = preview.TargetLanguage.Equals(game.TargetLanguage ?? "zh-CN", StringComparison.OrdinalIgnoreCase);
            var configured = IniEntry(config, "Files", "Directory", "Translation/{Lang}/Text");
            configured = configured.Replace("{Lang}", preview.TargetLanguage).Replace("{GameExeName}", Path.GetFileNameWithoutExtension(game.ExecutablePath));
            var root = Path.GetFullPath(game.Directory);
            // Both official BepInEx entry points use Paths.BepInExRootPath as TranslationPath.
            var cache = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(root, "BepInEx", configured.Replace('/', Path.DirectorySeparatorChar)));
            preview.CacheDirectory = cache;
            if (Directory.Exists(cache)) preview.CacheFileCount = CacheFiles(cache).Count;
            preview.Notes.Add("原字体参数和其他配置会在安装前完整备份。选择沿用已有字体后保留系统字体、TMP 替换和回退值；已有字库文件保持原样。");
            preview.Notes.Add(preview.CacheMatchesLanguage ? "目标语言相同；安装保留原缓存路径和缓存文件，继续由 XUnity 读取，不合并进工具译文库。" : $"旧缓存语言为 {preview.TargetLanguage}，当前目标语言不同。旧文件保留，但不能直接判断可复用；可先调整游戏目标语言，或手动选择相同语言的缓存目录。");
            foreach (var pair in preview.FontValues.Where(f => f.Key != "OverrideFont" && f.Value.Length > 0))
            {
                var fontPath = Path.GetFullPath(Path.IsPathRooted(pair.Value) ? pair.Value : Path.Combine(root, pair.Value.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(fontPath)) { preview.Notes.Add(pair.Key + " 可能是游戏 Resources 名称或系统字体，也可能文件缺失；需要在游戏中核验。"); continue; }
                try { Updates.UpdatePackage.RejectLinks(fontPath); preview.TmpFonts.Add(TmpFontInspector.InspectSafe(ReadFileBounded(fontPath, 64 * 1024 * 1024), game.EngineVersion)); }
                catch (Exception ex) { preview.Notes.Add("已有 TMP 字体检查：" + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or BadImageFormatException or ArgumentException) { preview.Conflicts.Add(ex.Message); }
        return preview;
    }

    private static List<string> CacheFiles(string directory)
    {
        Updates.UpdatePackage.RejectLinks(directory);
        var result = new List<string>(); var pending = new Queue<string>(); pending.Enqueue(directory); var visited = 0;
        while (pending.TryDequeue(out var folder))
        {
            if (++visited > 128) throw new InvalidOperationException("旧缓存目录过多，请选择范围更小的目录。");
            foreach (var child in Directory.EnumerateDirectories(folder)) { Updates.UpdatePackage.RejectLinks(child); pending.Enqueue(child); }
            foreach (var file in Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".txt" or ".zip"))
            { Updates.UpdatePackage.RejectLinks(file); result.Add(file); if (result.Count > 512) throw new InvalidOperationException("旧缓存文件过多，最多选择 512 个文件。"); }
        }
        return result;
    }
    public object CheckLegacyCache(GameRecord game, string path)
    {
        if (game.Engine != "Unity") throw new InvalidOperationException("此入口用于 Unity XUnity 译文缓存。");
        var directory = Path.GetFullPath(path.Trim().Trim('"'));
        if (!Directory.Exists(directory)) throw new InvalidOperationException("请选择存在的旧译文缓存目录。");
        var files = CacheFiles(directory);
        if (files.Count == 0) throw new InvalidOperationException("目录中没有 TXT 或 ZIP 译文缓存。");
        long size = files.Sum(f => new FileInfo(f).Length);
        if (size > 64L * 1024 * 1024 || files.Any(f => new FileInfo(f).Length > 16 * 1024 * 1024)) throw new InvalidOperationException("缓存超过大小上限：总计 64 MB，单文件 16 MB。");
        return new { path = directory, files = files.Count, size, language = game.TargetLanguage ?? "zh-CN", note = "安装时复制到独立目录；原缓存不会改动。请确认缓存译文使用相同目标语言。" };
    }
    private void AddLegacyCache(GameRecord game, List<PayloadFile> payload, ref string ini)
    {
        if (string.IsNullOrWhiteSpace(game.LegacyCachePath)) return;
        CheckLegacyCache(game, game.LegacyCachePath);
        var language = IniValue(game.TargetLanguage ?? "zh-CN");
        if (!System.Text.RegularExpressions.Regex.IsMatch(language, "^[a-zA-Z0-9_-]{1,24}$")) throw new InvalidOperationException("缓存目录的目标语言无效。");
        var directory = "BepInEx/Translation/" + language + "/Text";
        foreach (var file in CacheFiles(game.LegacyCachePath))
        {
            var relative = Path.GetRelativePath(game.LegacyCachePath, file).Replace('\\', '/');
            payload.Add(new PayloadFile(directory + "/Rika-legacy/" + relative, ReadFileBounded(file, 16 * 1024 * 1024)));
        }
        ini = SetIni(ini, "Files", "Directory", "Translation/" + language + "/Text");
        ini = SetIni(ini, "Files", "OutputFile", "Translation/" + language + "/Text/_RikaAutoGeneratedTranslations.txt");
    }
}
