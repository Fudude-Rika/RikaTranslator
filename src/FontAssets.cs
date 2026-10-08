using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit;

/// <summary>Copies user-selected fonts into the local data directory, independently of the source file.</summary>
public sealed class FontAssets
{
    private readonly string _root;
    public FontAssets(string dataDirectory) => _root = Path.Combine(Path.GetFullPath(dataDirectory), "fonts");
    public static bool Supported(GameRecord game) => game.Engine switch
    {
        "Unity" => game.Backend is "Mono" or "IL2CPP",
        "Ren'Py" or "RPG Maker MV" or "RPG Maker MZ" => true,
        "Unreal Engine" => game.Architecture == "x64" && UnrealLayout.SupportedVersion(game.EngineVersion),
        _ => false
    };

    public GameFontSettings Prepare(GameRecord game, GameFontSettings settings, string? sourcePath)
    {
        if (!Supported(game)) throw new InvalidOperationException("此引擎尚未提供字体适配。");
        var next = JsonSerializer.Deserialize<GameFontSettings>(JsonSerializer.Serialize(settings, DataStoreOptions), DataStoreOptions)!;
        next.TmpReport = null;
        if (next.Mode != "custom") { next.FileId = null; next.FileName = null; }
        else if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            if (game.Engine == "Unreal Engine") throw new InvalidOperationException("Unreal 本版使用游戏内已加载的 Font 资源，不能直接导入 TTF/OTF。");
            var path = Path.GetFullPath(sourcePath.Trim().Trim('"'));
            RejectLinks(path);
            var extension = game.Engine == "Unity" ? ".bundle" : Path.GetExtension(path).ToLowerInvariant();
            if (game.Engine != "Unity" && extension is not (".ttf" or ".otf")) throw new InvalidOperationException("请选择 TTF 或 OTF 字体文件。");
            var bytes = ReadBounded(path);
            ValidateBytes(bytes, extension);
            if (game.Engine == "Unity") next.TmpReport = TmpFontInspector.InspectSafe(bytes, game.EngineVersion);
            next.FileId = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + extension;
            next.FileName = Path.GetFileName(path);
            Validate(game.Engine, next);
            Directory.CreateDirectory(_root); RejectLinks(_root);
            var destination = AssetPath(next.FileId);
            if (!File.Exists(destination))
            {
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, destination, false); }
                catch (IOException) when (File.Exists(destination)) { }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        Validate(game.Engine, next);
        if (next.FileId != null)
        {
            var saved = Read(next.FileId);
            if (game.Engine == "Unity") next.TmpReport ??= TmpFontInspector.InspectSafe(saved, game.EngineVersion);
        }
        return next;
    }

    public static void Validate(string engine, GameFontSettings font)
    {
        if (font.Mode is not ("auto" or "original" or "custom" or "existing")) throw new InvalidOperationException("字体模式无效。");
        if (font.Mode == "existing" && (engine != "Unity" || font.ExistingValues == null || font.ExistingValues.Count != 3 || font.ExistingValues.Keys.Any(k => k is not ("OverrideFont" or "OverrideFontTextMeshPro" or "FallbackFontTextMeshPro")) || font.ExistingValues.Values.Any(v => v.Length > 1024 || v.Any(char.IsControl)))) throw new InvalidOperationException("没有可沿用的既有 Unity 字体配置，请重新检测。");
        if (font.Mode != "custom") return;
        if (font.TmpMode is not ("fallback" or "override")) throw new InvalidOperationException("TMP 字体模式无效。");
        if (engine == "Unity")
        {
            if (string.IsNullOrWhiteSpace(font.Family) || font.Family.Length > 128 || font.Family.Any(c => char.IsControl(c) || ";=[]".Contains(c)))
                throw new InvalidOperationException("请填写有效的系统字体名称。");
            if (font.FileId != null && !ValidId(font.FileId, "bundle")) throw new InvalidOperationException("TMP 字体文件标识无效。");
        }
        else if (engine == "Unreal Engine")
        {
            if (!Regex.IsMatch(font.UnrealPath ?? "", @"^/(?:Game|Engine)/[A-Za-z0-9_/-]+\.[A-Za-z0-9_-]+$")) throw new InvalidOperationException("字体资源路径应类似 /Engine/EngineFonts/Roboto.Roboto。");
            if (!Regex.IsMatch(font.UnrealTypeface ?? "", @"^[A-Za-z0-9_-]{1,64}$")) throw new InvalidOperationException("请填写有效的字形名称，例如 Regular。");
            if (font.FileId != null) throw new InvalidOperationException("Unreal 暂不支持普通字体文件导入。");
        }
        else if (engine is "Ren'Py" or "RPG Maker MV" or "RPG Maker MZ")
        {
            if (font.FileId == null || !ValidId(font.FileId, "ttf|otf")) throw new InvalidOperationException("请先选择 TTF/OTF 字体文件。");
        }
        else throw new InvalidOperationException("此引擎尚未提供字体适配。");
    }

    public byte[] Read(string id)
    {
        var bytes = ReadBounded(AssetPath(id));
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(id[..64], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("已保存字体的校验失败，请重新选择文件。");
        ValidateBytes(bytes, Path.GetExtension(id));
        return bytes;
    }
    private string AssetPath(string id)
    {
        if (!ValidId(id, "ttf|otf|bundle")) throw new InvalidOperationException("字体文件标识无效。");
        var path = Path.Combine(_root, id); RejectLinks(path); return path;
    }
    private static bool ValidId(string id, string extensions) => Regex.IsMatch(id, "^[a-f0-9]{64}\\.(?:" + extensions + ")$");
    private static readonly JsonSerializerOptions DataStoreOptions = new(JsonSerializerDefaults.Web);
    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 12 or > 64 * 1024 * 1024) throw new InvalidOperationException("字体文件大小无效，最大支持 64 MB。");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private static void ValidateBytes(byte[] bytes, string extension)
    {
        if (extension == ".bundle")
        {
            if (!bytes.AsSpan().StartsWith("UnityFS\0"u8) || bytes.Length < 32) throw new InvalidOperationException("这不是 UnityFS 字体包。TMP 需要与游戏版本匹配的字体 AssetBundle，不能使用普通 TTF/OTF。");
            return;
        }
        var signature = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (signature != 0x00010000 && signature != 0x4f54544f) throw new InvalidOperationException("无法识别 TTF/OTF 文件头。");
        var tables = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        if (tables is < 1 or > 256 || 12 + 16 * tables > bytes.Length) throw new InvalidOperationException("字体表目录损坏。");
        var cmap = false;
        for (var i = 0; i < tables; i++)
        {
            var row = bytes.AsSpan(12 + i * 16, 16);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(row[8..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(row[12..]);
            if ((ulong)offset + length > (ulong)bytes.Length) throw new InvalidOperationException("字体表超出文件边界。");
            cmap |= row[..4].SequenceEqual("cmap"u8) && length >= 4;
        }
        if (!cmap) throw new InvalidOperationException("字体缺少字符映射表。");
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current) ?? "")
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("字体路径包含符号链接或目录联接。"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
}
