using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameTranslateToolkit.Updates;

namespace GameTranslateToolkit;

public sealed class GameLogEntry
{
    public string GameId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public string TimeBasis { get; set; } = "文件修改时间，行内时间未知";
    public string Scope { get; set; } = "historical";
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
    public string? RequestId { get; set; }
}
public sealed class GameLogSource
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string State { get; set; } = "missing";
    public string Note { get; set; } = "";
    public long Bytes { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public bool Truncated { get; set; }
}
public sealed class GameDiagnosticIssue
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Level { get; set; } = "warning";
    public string Scope { get; set; } = "current";
    public string Suggestion { get; set; } = "";
    public int Count { get; set; }
    public List<GameLogEntry> Evidence { get; set; } = [];
}
public sealed class GameDiagnosticReport
{
    public string Version { get; set; } = "";
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string Engine { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public string Architecture { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LaunchedAt { get; set; }
    public string Summary { get; set; } = "";
    public string VisualStatus { get; set; } = "画面未验证；加载、HTTP 发送和绘制调用均不代表字形显示正常。";
    public GameRuntimeSnapshot Runtime { get; set; } = new();
    public List<GameLogSource> Sources { get; set; } = [];
    public List<GameDiagnosticIssue> Issues { get; set; } = [];
    public List<GameLogEntry> Entries { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

/// <summary>Bounded, read-only engine logs; source byte boundaries distinguish each tool launch.</summary>
public sealed class GameLogService(string dataDirectory, LogService logs)
{
    private const int TailLimit = 256 * 1024;
    private readonly ConcurrentDictionary<string, Launch> _launches = new();
    private readonly string _root = Path.Combine(Path.GetFullPath(dataDirectory), "game-logs");
    private sealed class Launch
    {
        public DateTimeOffset Time = DateTimeOffset.UtcNow;
        public string? PlayerPath;
        public readonly Dictionary<string, Boundary> Boundaries = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed record Boundary(long Position, long Created, string Anchor);

    public string? BeginLaunch(GameRecord game)
    {
        var launch = new Launch();
        foreach (var (_, path) in Paths(game))
        {
            try
            {
                UpdatePackage.RejectLinks(path);
                if (!File.Exists(path)) continue;
                using var stream = Open(path);
                launch.Boundaries[path] = new(stream.Length, File.GetCreationTimeUtc(path).Ticks, Anchor(stream, stream.Length));
            }
            catch (Exception ex) when (ReadError(ex)) { }
        }
        if (game.Engine == "Unity")
        {
            var directory = UpdatePackage.Target(_root, game.Id);
            UpdatePackage.RejectLinks(directory); Directory.CreateDirectory(directory);
            foreach (var old in Directory.EnumerateFiles(directory, "Player-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Skip(4).Take(64))
            {
                try { UpdatePackage.RejectLinks(old); File.Delete(old); } catch (Exception ex) when (ReadError(ex)) { }
            }
            launch.PlayerPath = Path.Combine(directory, $"Player-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        }
        _launches[game.Id] = launch;
        return launch.PlayerPath;
    }

    public static string ValidatePlayerPath(string path)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        if (Path.GetExtension(full).ToLowerInvariant() is not (".log" or ".txt")) throw new ArgumentException("请选择 LOG 或 TXT 日志。");
        UpdatePackage.RejectLinks(full);
        if (!File.Exists(full)) throw new FileNotFoundException("所选日志不存在。");
        using var stream = Open(full);
        return full;
    }

    private List<(string Name, string Path)> Paths(GameRecord game)
    {
        var result = new List<(string, string)>();
        if (game.Engine == "Unity")
        {
            var bep = Path.Combine(game.Directory, "BepInEx");
            try
            {
                UpdatePackage.RejectLinks(bep);
                if (Directory.Exists(bep)) result.AddRange(Directory.EnumerateFiles(bep, "LogOutput*").Take(64)
                    .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".log" or ".txt" || p.EndsWith(".log.1", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(8).Select(p => ("BepInEx", p)));
            }
            catch (Exception ex) when (ReadError(ex)) { }
            if (result.Count == 0) result.Add(("BepInEx", Path.Combine(bep, "LogOutput.log")));
            if (_launches.TryGetValue(game.Id, out var launch) && launch.PlayerPath != null) result.Add(("Player.log", launch.PlayerPath));
            if (!string.IsNullOrWhiteSpace(game.PlayerLogPath)) result.Add(("Player.log（手动关联）", game.PlayerLogPath));
            var local = Path.Combine(game.Directory, "Player.log");
            if (File.Exists(local)) result.Add(("Player.log（游戏目录）", local));
            var own = UpdatePackage.Target(_root, game.Id);
            try
            {
                UpdatePackage.RejectLinks(own);
                if (Directory.Exists(own)) result.AddRange(Directory.EnumerateFiles(own, "Player-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(5).Select(p => ("Player.log", p)));
            }
            catch (Exception ex) when (ReadError(ex)) { }
            if (!result.Any(p => p.Item1.StartsWith("Player.log"))) result.Add(("Player.log", ""));
        }
        else if (game.Engine == "Ren'Py")
        {
            result.Add(("Ren’Py", Path.Combine(game.Directory, "log.txt")));
            result.Add(("Ren’Py traceback", Path.Combine(game.Directory, "traceback.txt")));
        }
        else if (game.Engine == "Unreal Engine")
        {
            var mod = UnrealLayout.ModDirectory(game);
            if (mod != null) result.Add(("UE4SS", Path.GetFullPath(Path.Combine(mod, "../../UE4SS.log"))));
        }
        return result.DistinctBy(p => p.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public GameDiagnosticReport Get(GameRecord game, GameRuntimeSnapshot runtime, string version, string? source = null, string? level = null, string? query = null, string? scope = null)
    {
        _launches.TryGetValue(game.Id, out var launch);
        var launchedAt = launch?.Time ?? runtime.LaunchedAt;
        var report = new GameDiagnosticReport { Version = version, GameId = game.Id, GameName = logs.Sanitize(game.Name), Engine = game.Engine, EngineVersion = game.EngineVersion, Architecture = game.Architecture, Runtime = runtime, LaunchedAt = launchedAt };
        var entries = new List<GameLogEntry>();
        foreach (var (name, path) in Paths(game)) ReadSource(game, launch, name, path, report.Sources, entries);
        if (runtime.PluginHeartbeatAt.HasValue)
        {
            report.Sources.Add(new() { Name = "RPG 适配器状态", State = runtime.PluginStatusCurrent ? "reported" : "stale", ModifiedAt = runtime.PluginHeartbeatAt, Note = runtime.PluginStatusCurrent ? "当前插件心跳；只上报计数和字体加载状态。" : "上次插件心跳，当前未收到实时状态；计数保留供排查。" });
            entries.Add(new() { GameId = game.Id, Source = "RPG 适配器", Time = runtime.PluginHeartbeatAt.Value, TimeBasis = "工具收到状态时间", Scope = launch != null && runtime.PluginHeartbeatAt >= launch.Time ? "current" : "historical", Message = $"适配器 {runtime.AdapterVersion}：已调用译文绘制 {runtime.PluginApplied} 次；字体检查 {runtime.FontStatus}。不代表字形画面已验证。" });
        }
        foreach (var entry in logs.Query(game.Id).Take(400))
            entries.Add(new GameLogEntry { GameId = game.Id, Source = "工具 / " + entry.Module, Path = logs.Sanitize(logs.DirectoryPath), Time = entry.Time, TimeBasis = "日志记录时间", Scope = launchedAt != null && entry.Time >= launchedAt ? "current" : "historical", Level = entry.Level, Message = logs.Sanitize(entry.Message + (entry.Details == null ? "" : "\n" + entry.Details)), RequestId = entry.RequestId });
        foreach (var entry in entries) Classify(entry, report.Issues);
        void Issue(string code, string title, string suggestion, string severity = "warning")
        { if (!report.Issues.Any(i => i.Code == code && i.Scope == "current")) report.Issues.Add(new() { Code = code, Title = title, Suggestion = suggestion, Level = severity }); }
        if (runtime.PauseReason != null) Issue("paused", runtime.PauseReason, "启用全局与此游戏的翻译开关后重试。");
        if (runtime.GameRunning && runtime.Received == 0 && runtime.LaunchedAt < DateTimeOffset.UtcNow.AddSeconds(-30))
            Issue("no-text", "本次启动尚未收到游戏原文", runtime.DeliveryMode is "floating" or "hook-embedded" ? "先显示一段对话，检查 Hook 是否连接，选择预览能正确显示原文的通道；必要时开启通用取词并重新连接，或填写匹配版本的手动 Hook 代码。" : "先打开菜单或对话。检查插件加载、文字控件类型和适配状态；图片文字及自定义控件可能无法取词。");
        if (runtime.PluginStatusCurrent && runtime.FontStatus == "unavailable") Issue("font-load", "游戏字体检查未通过", "等待字体加载，核对所选字库文件。字体加载完成也不能证明字符覆盖完整。");
        foreach (var warning in game.Font.TmpReport?.Warnings ?? []) Issue("font-static", warning, "此结果来自字体文件静态检查；选择匹配版本的 TMP 后备字体，并在游戏中验证。", "info");
        report.Issues = report.Issues.OrderBy(i => i.Scope != "current").ThenBy(i => i.Level != "error").Take(40).ToList();
        var currentIssue = report.Issues.FirstOrDefault(i => i.Scope == "current" && i.Level is "error" or "warning");
        report.Summary = currentIssue?.Title ?? (runtime.Delivered > 0 ? $"已获得 {runtime.Translated} 条译文，发送回适配器 {runtime.Delivered} 条；画面待验证。" : "等待本次游戏取词和翻译证据。");
        report.Entries = entries.Where(e => (string.IsNullOrEmpty(source) || e.Source.Contains(source, StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrEmpty(level) || e.Level == level) && (string.IsNullOrEmpty(scope) || e.Scope == scope) && (string.IsNullOrEmpty(query) || (e.Message + e.Source + e.Path).Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.Time).Take(800).ToList();
        report.Notes.Add("每个来源最多读取末尾 256 KB、1,200 行，报告最多显示 800 条；同类问题合并，保留至多 3 条原始证据。历史日志仅供排查，不作为本次运行成功或失败证据。");
        if (launch == null) report.Notes.Add("尚未在当前工具进程中启动本次游戏；日志标为历史记录。通过工具启动后记录新的日志边界。");
        if (game.Engine == "Unity") report.Notes.Add("通过工具启动 Unity 时用 -logFile 写入本地数据目录。外部启动的默认 Player.log 无法可靠定位时，可手动关联；文件时间不能代替逐行发生时间。");
        return report;
    }

    private void ReadSource(GameRecord game, Launch? launch, string name, string path, List<GameLogSource> sources, List<GameLogEntry> entries)
    {
        var info = new GameLogSource { Name = name, Path = logs.Sanitize(path) }; sources.Add(info);
        if (path.Length == 0) { info.State = "unknown"; info.Note = "尚无可靠的 Player.log 路径。通过工具启动，或手动关联此游戏的日志。"; return; }
        try
        {
            UpdatePackage.RejectLinks(path);
            if (!File.Exists(path)) { info.Note = "日志尚未生成；启动后刷新。"; return; }
            using var stream = Open(path);
            info.State = "read"; info.Bytes = stream.Length; info.ModifiedAt = File.GetLastWriteTimeUtc(path);
            var boundary = stream.Length;
            if (launch != null)
            {
                var created = File.GetCreationTimeUtc(path).Ticks;
                var original = launch.Boundaries.GetValueOrDefault(path);
                // A log may be renamed during rotation; retain the original byte boundary.
                original ??= launch.Boundaries.Values.FirstOrDefault(b => b.Position > 0 && b.Created == created && stream.Length >= b.Position && Anchor(stream, b.Position) == b.Anchor);
                if (original != null && original.Created == created && stream.Length >= original.Position && Anchor(stream, original.Position) == original.Anchor) boundary = original.Position;
                else if (path == launch.PlayerPath || File.GetLastWriteTimeUtc(path) >= launch.Time.UtcDateTime) boundary = 0;
            }
            var position = Math.Max(0, stream.Length - TailLimit);
            info.Truncated = position > 0;
            stream.Position = position;
            var bytes = new byte[(int)Math.Min(TailLimit, stream.Length - position)]; stream.ReadExactly(bytes);
            var offset = 0;
            if (position > 0) { var firstNewline = Array.IndexOf(bytes, (byte)'\n'); offset = firstNewline >= 0 ? firstNewline + 1 : bytes.Length; }
            var count = 0;
            while (offset < bytes.Length && count++ < 1200)
            {
                var end = Array.IndexOf(bytes, (byte)'\n', offset); if (end < 0) end = bytes.Length;
                var text = Encoding.UTF8.GetString(bytes.AsSpan(offset, Math.Min(end - offset, 8192))).TrimEnd('\r').TrimStart('\ufeff');
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var record = new GameLogEntry { GameId = game.Id, Source = name, Path = info.Path, Scope = launch != null && position + offset >= boundary ? "current" : "historical", Time = info.ModifiedAt.Value, Level = Level(text), Message = logs.Sanitize(text) };
                    // Only an explicit offset-bearing timestamp is accepted; engine-local times are ambiguous.
                    var prefix = text.TrimStart('[', ' '); var separator = prefix.IndexOf(' ');
                    var stamp = separator >= 0 ? prefix[..separator] : "";
                    var hasOffset = stamp.EndsWith('Z') || stamp.Length >= 6 && stamp[^3] == ':' && stamp[^6] is '+' or '-';
                    if (hasOffset && stamp.Contains('T') && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) { record.Time = parsed; record.TimeBasis = "行内时间"; }
                    entries.Add(record);
                }
                offset = end + 1;
            }
            if (offset < bytes.Length) info.Truncated = true;
            info.Note = info.Truncated ? "仅展示读取上限内的日志片段。" : "已读取；逐行时间未知时使用文件修改时间。";
        }
        catch (UnauthorizedAccessException) { info.State = "denied"; info.Note = "没有读取权限。可退出游戏后重试或选择可读的日志副本。"; }
        catch (Exception ex) when (ReadError(ex)) { info.State = "unreadable"; info.Note = "路径含重解析点、文件被占用或无法读取。"; }
    }
    private static bool ReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException;
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    private static string Anchor(FileStream stream, long position)
    {
        if (position == 0) return "";
        stream.Position = Math.Max(0, position - 256); var bytes = new byte[(int)Math.Min(256, position)]; stream.ReadExactly(bytes); return Convert.ToHexString(SHA256.HashData(bytes));
    }
    private static bool Has(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
    private static string Level(string text) => Has(text, "error") || Has(text, "exception") || Has(text, "fatal") ? "error" : Has(text, "warning") || Has(text, "warn ") || Has(text, "not found") || Has(text, "missing") ? "warning" : "info";
    private static void Classify(GameLogEntry e, List<GameDiagnosticIssue> issues)
    {
        var text = e.Message;
        void Add(string code, string title, string suggestion, string level = "warning")
        {
            var issue = issues.FirstOrDefault(i => i.Code == code && i.Scope == e.Scope);
            if (issue == null) { issue = new() { Code = code, Title = title, Suggestion = suggestion, Level = level, Scope = e.Scope }; issues.Add(issue); }
            issue.Count++; if (issue.Evidence.Count < 3) issue.Evidence.Add(e);
        }
        if (Has(text, "HTTP 401") || Has(text, "HTTP 403") || Has(text, "Unauthorized")) Add("authentication", "翻译服务鉴权或权限失败", "检查服务地址、API Key、模型权限及额外请求头；使用 AI 翻译页的主动测试。", "error");
        if (Has(text, "HTTP 429") || Has(text, "rate limit")) Add("rate-limit", "翻译服务限流", "降低并发并等待额度恢复；避免重复触发同一文本请求。");
        if (Has(text, "内嵌等待超时")) Add("hook-deadline", "内嵌等待已超时，游戏保留原文", "API 响应较慢时先使用翻译浮窗；可将内嵌等待上限调高到最多 5000 毫秒。迟到译文仍可缓存，回看同句时使用缓存。", "warning");
        else if (Has(text, "timeout") || Has(text, "timed out") || Has(text, "超时")) Add("timeout", "请求或组件操作超时", "查看来源确定是服务超时还是加载超时；核对网络、接口地址和超时设置。");
        if (Has(text, "Unable to execute IL2CPP chainloader") || Has(text, "Error loading [XUnity") || Has(text, "Error occurred loading plugins") || Has(text, "Failed to generate Il2Cpp interop") || Has(text, "Could not load file or assembly")) Add("loader", "加载器、组件或依赖加载失败", "检查引擎架构与对应 BepInEx/XUnity 版本；先退出游戏，再检查安装计划和冲突。", "error");
        if (Has(text, "character") && (Has(text, "not found") || Has(text, "missing")) || Has(text, "glyph") && (Has(text, "missing") || Has(text, "not found"))) Add("missing-glyph", "日志报告字体缺字", "为 TMP 选择覆盖目标字符的后备字库；普通系统字体只处理相应 UGUI 控件。方框本身不能证明翻译速度问题。");
        if (Has(text, "not readable") || Has(text, "isReadable") && (Has(text, "false") || Has(text, "error"))) Add("atlas-readable", "字体或纹理图集不可读", "检查 TMP 字体包的动态图集可读性；选择兼容字库，并在副本验证动态补字。");
        if (Has(text, "TryAddCharacters") && (Has(text, "failed") || Has(text, "false") || Has(text, "error")) || Has(text, "Failed to add") && Has(text, "character")) Add("dynamic-glyph", "动态补字失败", "检查内嵌源字体、图集可读性和容量；使用覆盖字符的字库或后备链。");
        if ((Has(text, "font") || Has(text, "字体")) && (Has(text, "failed to load") || Has(text, "unable to load") || Has(text, "could not load") || Has(text, "字体不可用"))) Add("font-load", "字体加载失败或不可用", "核对字体资源名称、文件路径、Unity/TMP 版本；重新检查字体包并重启游戏。");
        if (Has(text, "已暂停") || Has(text, "翻译开关已关闭")) Add("paused", "翻译处于暂停状态", "启用全局与游戏翻译开关后重试。");
    }
}
