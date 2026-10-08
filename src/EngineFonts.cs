using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit;

public sealed partial class EngineService
{
    private readonly FontAssets _fonts;
    public GameFontSettings PrepareFont(GameRecord game, GameFontSettings font, string? path)
    {
        if (font.Mode == "existing") font.ExistingValues = ExistingFontValues(game);
        else font.ExistingValues = null;
        return _fonts.Prepare(game, font, path);
    }
    private static bool KnownInstalledHash(InstalledFile file, string hash) => hash == file.InstalledHash || file.SupersededHashes.Contains(hash);
    private static bool HasBundledTmp(GameRecord game) => game.EngineVersion.StartsWith("2022.", StringComparison.Ordinal) && (game.TargetLanguage ?? "zh-CN").StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    private static string CustomFontName(GameRecord game) => "GameTranslateToolkit-custom" + Path.GetExtension(game.Font.FileId!);
    private static string UnityFontIni(GameRecord game, string ini)
    {
        FontAssets.Validate(game.Engine, game.Font);
        var font = game.Font;
        if (font.Mode == "existing")
        {
            foreach (var pair in font.ExistingValues!) ini = SetIni(ini, "Behaviour", pair.Key, pair.Value);
            return ini;
        }
        var family = font.Mode == "original" ? "" : font.Mode == "custom" ? font.Family : "Microsoft YaHei";
        var tmp = font.Mode == "auto" && HasBundledTmp(game) ? "BepInEx/fonts/" + Unity2022Font :
            font.Mode == "custom" && font.FileId != null ? "BepInEx/fonts/" + CustomFontName(game) : "";
        ini = SetIni(ini, "Behaviour", "OverrideFont", family);
        ini = SetIni(ini, "Behaviour", "OverrideFontTextMeshPro", font.Mode == "custom" && font.TmpMode == "override" ? tmp : "");
        return SetIni(ini, "Behaviour", "FallbackFontTextMeshPro", font.Mode != "custom" || font.TmpMode == "fallback" ? tmp : "");
    }
    private static byte[] RpgFontScript(GameRecord game)
    {
        FontAssets.Validate(game.Engine, game.Font);
        var text = Encoding.UTF8.GetString(Asset("GameTranslateToolkit.js"));
        const string marker = "/*__GTT_FONT_SETTINGS__*/ { mode: \"auto\", file: \"GameTranslateToolkitCJK.otf\" }";
        if (!text.Contains(marker)) throw new InvalidOperationException("RPG 字体适配资源不完整。");
        var value = JsonSerializer.Serialize(new { mode = game.Font.Mode, file = game.Font.Mode == "custom" ? CustomFontName(game) : "GameTranslateToolkitCJK.otf" });
        return Encoding.UTF8.GetBytes(text.Replace(marker, value));
    }
    private static byte[] RenpyBridge(GameRecord game, string bridge, string token) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        endpoint = bridge, token, gameId = game.Id, from = game.SourceLanguage ?? "auto", to = game.TargetLanguage ?? "zh-CN",
        fontMode = game.Font.Mode, fontFile = "GameTranslateToolkit/" + (game.Font.Mode == "custom" ? CustomFontName(game) : FontFile)
    }, Json);
    private static string LuaFontFields(GameRecord game)
    {
        FontAssets.Validate(game.Engine, game.Font);
        var path = game.Font.Mode == "custom" ? game.Font.UnrealPath : "/Engine/EngineFonts/Roboto.Roboto";
        var typeface = game.Font.Mode == "custom" ? game.Font.UnrealTypeface : "Regular";
        return $", fontMode = \"{game.Font.Mode}\", fontPath = \"{path}\", fontTypeface = \"{typeface}\" ";
    }
    private static byte[] UnrealConfig(GameRecord game, string token) => Encoding.ASCII.GetBytes($"return {{ gameId = \"{game.Id}\", token = \"{token}\", source = \"{IniValue(game.SourceLanguage ?? "auto")}\", target = \"{IniValue(game.TargetLanguage ?? "zh-CN")}\"{LuaFontFields(game)} }}\n");

    /// <summary>Updates only owned font/configuration files, preserving the original installation backups.</summary>
    public async Task<bool> ApplyFontAsync(GameRecord game, CancellationToken ct = default)
    {
        FontAssets.Validate(game.Engine, game.Font);
        var gate = _locks.GetOrAdd(Path.GetFullPath(game.Directory), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            EnsureStopped(game.Directory);
            var manifest = ActiveManifest(game);
            if (manifest == null) return false; // Preferences take effect on the next installation.
            if (manifest.State != "installed") throw new InvalidOperationException("安装清单尚未恢复正常，请先处理组件冲突或卸载恢复。");
            if (!Path.GetFullPath(manifest.Root).Equals(Path.GetFullPath(game.Directory), StringComparison.OrdinalIgnoreCase) || manifest.Adapter != AdapterName(game)) throw new InvalidOperationException("游戏与安装清单不匹配，未修改字体。");
            // Reject tampering before writing even the first file. Normal XUnity INI expansion is archived.
            foreach (var owned in manifest.Files)
            {
                var path = SafeTarget(manifest.Root, owned.RelativePath);
                if (!File.Exists(path) || (!KnownInstalledHash(owned, FileHash(path)) && !IsUnityRuntimeConfig(manifest, owned))) throw new InvalidOperationException("适配器文件缺失或已被外部修改，未替换字体：" + owned.RelativePath);
            }
            var files = FontPayload(game, manifest);
            var changes = new List<(PayloadFile Payload, byte[]? Before, InstalledFile Record)>();
            foreach (var file in files)
            {
                var path = SafeTarget(manifest.Root, file.RelativePath);
                var owned = manifest.Files.FirstOrDefault(f => f.RelativePath.Replace('\\', '/').Equals(file.RelativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                if (owned == null && File.Exists(path))
                {
                    if (game.Engine == "Unity" && FileHash(path) == Hash(file.Bytes)) continue;
                    throw new InvalidOperationException("字体目标位置已有未知文件，未覆盖：" + file.RelativePath);
                }
                if (System.IO.Directory.Exists(path)) throw new InvalidOperationException("字体目标位置已有同名目录。");
                var before = File.Exists(path) ? ReadFileBounded(path, 64 * 1024 * 1024) : null;
                if (before != null && Hash(before) == Hash(file.Bytes)) continue;
                changes.Add((file, before, owned ?? new InstalledFile { RelativePath = file.RelativePath }));
            }
            if (changes.Count == 0) return true;
            var directory = ManifestDirectory(game.Id);
            var previous = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            // Journal old/new hashes before mutation, so an interrupted update remains uninstallable.
            foreach (var change in changes)
            {
                if (!manifest.Files.Contains(change.Record)) manifest.Files.Add(change.Record);
                if (change.Before != null)
                {
                    var oldHash = Hash(change.Before);
                    if (!change.Record.SupersededHashes.Contains(oldHash)) change.Record.SupersededHashes.Add(oldHash);
                    var archive = $"generated/font-{Guid.NewGuid():N}/{Path.GetFileName(change.Payload.RelativePath)}";
                    var path = SafeTarget(directory, archive); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    AtomicWrite(path, change.Before); manifest.GeneratedArchives.Add(archive);
                }
                change.Record.InstalledHash = Hash(change.Payload.Bytes);
            }
            manifest.State = "font-updating"; SaveManifest(directory, manifest);
            var applied = new List<(PayloadFile Payload, byte[]? Before, InstalledFile Record)>();
            try
            {
                foreach (var change in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = SafeTarget(manifest.Root, change.Payload.RelativePath);
                    if (File.Exists(path) != (change.Before != null) || (change.Before != null && FileHash(path) != Hash(change.Before))) throw new InvalidOperationException("字体替换期间文件被其他程序修改，已停止。");
                    CreateParents(manifest, Path.GetDirectoryName(path)!); SaveManifest(directory, manifest);
                    AtomicWrite(path, change.Payload.Bytes); applied.Add(change); change.Record.Applied = true;
                    SaveManifest(directory, manifest);
                }
                manifest.State = "installed"; SaveManifest(directory, manifest);
            }
            catch
            {
                var conflict = false;
                foreach (var change in applied.AsEnumerable().Reverse())
                {
                    try
                    {
                        var path = SafeTarget(manifest.Root, change.Payload.RelativePath);
                        if (!File.Exists(path) || FileHash(path) != Hash(change.Payload.Bytes)) { conflict = true; continue; }
                        if (change.Before == null) File.Delete(path); else AtomicWrite(path, change.Before);
                    }
                    catch { conflict = true; }
                }
                if (!conflict) { RemoveEmptyParents(manifest); AtomicWrite(Path.Combine(directory, "manifest.json"), previous); }
                else { manifest.State = "conflict"; SaveManifest(directory, manifest); }
                throw;
            }
            _logs.Write("info", "font", "游戏字体设置已应用，重新启动游戏后生效。原始安装备份继续保留。", game.Id, details: new { game.Font.Mode, FileCount = changes.Count });
            return true;
        }
        finally { gate.Release(); }
    }

    private List<PayloadFile> FontPayload(GameRecord game, InstallationManifest manifest)
    {
        var files = new List<PayloadFile>();
        void Add(string path, byte[] bytes) => files.Add(new(path, bytes));
        if (game.Engine == "Unity")
        {
            const string iniPath = "BepInEx/config/AutoTranslatorConfig.ini";
            if (!manifest.Files.Any(f => f.RelativePath.Replace('\\', '/') == iniPath)) throw new InvalidOperationException("安装清单未包含 Unity 配置。");
            Add(iniPath, Encoding.UTF8.GetBytes(UnityFontIni(game, Encoding.UTF8.GetString(ReadFileBounded(SafeTarget(manifest.Root, iniPath), 1024 * 1024)).TrimStart('\ufeff'))));
            if (game.Font.Mode == "auto" && HasBundledTmp(game))
            {
                Add("BepInEx/fonts/" + Unity2022Font, Package(Unity2022Font));
                Add("BepInEx/fonts/GameTranslateToolkitCJK-LICENSE.txt", Asset("packages/Yozai.LICENSE"));
            }
            if (game.Font.Mode == "custom" && game.Font.FileId != null) Add("BepInEx/fonts/" + CustomFontName(game), _fonts.Read(game.Font.FileId));
        }
        else if (game.Engine is "RPG Maker MV" or "RPG Maker MZ")
        {
            var rpg = RpgRoot(manifest.Root) ?? throw new InvalidOperationException("RPG 插件目录不存在。");
            var prefix = Path.GetRelativePath(manifest.Root, rpg).Replace('\\', '/'); prefix = prefix == "." ? "" : prefix + "/";
            Add(prefix + "js/plugins/" + RpgPlugin + ".js", RpgFontScript(game));
            if (game.Font.Mode == "custom") Add(prefix + "fonts/" + CustomFontName(game), _fonts.Read(game.Font.FileId!));
        }
        else if (game.Engine == "Ren'Py")
        {
            const string path = "game/GameTranslateToolkit/bridge.json";
            var config = JsonNode.Parse(ReadFileBounded(SafeTarget(manifest.Root, path), 1024 * 1024)) as JsonObject ?? throw new InvalidOperationException("Ren'Py 桥接配置损坏。");
            config["fontMode"] = game.Font.Mode;
            config["fontFile"] = "GameTranslateToolkit/" + (game.Font.Mode == "custom" ? CustomFontName(game) : FontFile);
            Add(path, Encoding.UTF8.GetBytes(config.ToJsonString(Json)));
            Add("game/" + RenpyScript, Asset(RenpyScript));
            if (game.Font.Mode == "custom") Add("game/GameTranslateToolkit/" + CustomFontName(game), _fonts.Read(game.Font.FileId!));
        }
        else if (game.Engine == "Unreal Engine")
        {
            var mod = UnrealLayout.ModDirectory(game) ?? throw new InvalidOperationException("Unreal Shipping 程序不存在。");
            var prefix = Path.GetRelativePath(manifest.Root, mod).Replace('\\', '/') + "/";
            var configPath = prefix + "Scripts/config.lua";
            var config = Encoding.ASCII.GetString(ReadFileBounded(SafeTarget(manifest.Root, configPath), 16384));
            config = Regex.Replace(config, @",\s*font(?:Mode|Path|Typeface)\s*=\s*""[^""]*""", "");
            var end = config.LastIndexOf('}'); if (end < 0) throw new InvalidOperationException("Unreal 字体配置格式损坏。");
            Add(configPath, Encoding.ASCII.GetBytes(config[..end].TrimEnd() + LuaFontFields(game) + "}\n"));
            Add(prefix + "Scripts/main.lua", Asset("unreal/main.lua"));
        }
        else throw new InvalidOperationException("此引擎尚未提供字体适配。");
        return files;
    }
}
