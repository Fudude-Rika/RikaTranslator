using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using GameTranslateToolkit.Updates;

namespace GameTranslateToolkit;

public sealed class GameVerificationRecord
{
    public string Fingerprint { get; set; } = "";
    public string Game { get; set; } = "";
    public string Engine { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string ToolVersion { get; set; } = "";
    public string Adapter { get; set; } = "";
    public string Status { get; set; } = "partial";
    public string Date { get; set; } = "";
    public string Method { get; set; } = "游戏副本、本机模拟 API";
    public List<VerificationStep> Steps { get; set; } = [];
    public List<string> Limits { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
}
public sealed class VerificationStep
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string Note { get; set; } = "";
}
public sealed class GameVerificationService
{
    private readonly List<GameVerificationRecord> _records;
    private static readonly ConcurrentDictionary<string, string> Hashes = new();
    public GameVerificationService(string directory)
    {
        var path = Path.Combine(directory, "validation", "verified-games.json");
        _records = File.Exists(path) && new FileInfo(path).Length < 256 * 1024 ? JsonSerializer.Deserialize<List<GameVerificationRecord>>(File.ReadAllText(path), DataStore.Json) ?? [] : [];
    }
    public object Get(GameRecord game)
    {
        string fingerprint;
        try { fingerprint = Fingerprint(game); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return new { status = "unverified", note = "无法核对游戏文件身份，尚无对应验证记录。" }; }
        var records = _records.Where(r => r.Fingerprint == fingerprint && r.Engine == game.Engine && r.EngineVersion == game.EngineVersion && r.Architecture == game.Architecture).ToList();
        return new { status = records.FirstOrDefault()?.Status ?? "unverified", note = records.Count == 0 ? "此游戏或当前文件版本未验证。引擎适配路线不代表所有游戏、控件和字体已通过。" : "记录只适用于匹配的游戏文件与下列测试范围；工具升级后未回归的步骤保留原测试版本。", records };
    }
    public static string Fingerprint(GameRecord game)
    {
        var exe = game.Engine == "Unreal Engine" ? UnrealLayout.ShippingExecutable(game.Directory, game.ExecutablePath) ?? game.ExecutablePath : game.ExecutablePath;
        var paths = new List<string> { exe };
        if (game.Engine == "Unity")
        {
            var data = Path.Combine(game.Directory, Path.GetFileNameWithoutExtension(game.ExecutablePath) + "_Data");
            var manager = Path.Combine(data, "globalgamemanagers");
            paths.Add(File.Exists(manager) ? manager : Path.Combine(data, "data.unity3d"));
        }
        else if (game.Engine is "RPG Maker MV" or "RPG Maker MZ")
        {
            var root = File.Exists(Path.Combine(game.Directory, "data", "System.json")) ? game.Directory : Path.Combine(game.Directory, "www");
            paths.Add(Path.Combine(root, "data", "System.json"));
            paths.Add(Path.Combine(root, "js", game.Engine == "RPG Maker MZ" ? "rmmz_core.js" : "rpg_core.js"));
        }
        var combined = string.Join("/", paths.Select(FileHash));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined))).ToLowerInvariant();
    }
    private static string FileHash(string path)
    {
        path = Path.GetFullPath(path); UpdatePackage.RejectLinks(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 512L * 1024 * 1024) throw new IOException("无法核对验证文件。");
        var key = path + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        if (Hashes.TryGetValue(key, out var cached)) return cached;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (Hashes.Count > 64) Hashes.Clear(); Hashes[key] = hash; return hash;
    }
}
