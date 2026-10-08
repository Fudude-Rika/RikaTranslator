using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit;

public sealed class GameRuntimeSnapshot
{
    public bool AdapterInstalled { get; set; }
    public bool GameRunning { get; set; }
    public bool GlobalTranslationEnabled { get; set; } = true;
    public bool GameTranslationEnabled { get; set; } = true;
    public string? PauseReason { get; set; }
    public bool LoaderLogFound { get; set; }
    public bool TranslatorAttempted { get; set; }
    public long Received { get; set; }
    public long Translated { get; set; }
    public long Delivered { get; set; }
    public long Failed { get; set; }
    public long CacheHits { get; set; }
    public long PluginApplied { get; set; }
    public long PluginVerified { get; set; }
    public int VisibleWidgets { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public DateTimeOffset? LaunchedAt { get; set; }
    public string? AdapterVersion { get; set; }
    public string? FontStatus { get; set; }
    public DateTimeOffset? PluginHeartbeatAt { get; set; }
    public bool PluginStatusCurrent { get; set; }
    public string? LastError { get; set; }
    public string Stage { get; set; } = "not-installed";
    public string DeliveryMode { get; set; } = "embedded";
    public List<string> Notes { get; set; } = [];
}

/// <summary>
/// Tracks runtime bridge activity separately from the manual translation screen. Log evidence is
/// scoped to the launch requested through this process; it never asserts that a returned string was
/// drawn by a game. BepInEx prints "Loading [...]" before LoadPlugin, and startup complete can also
/// follow caught failures: https://github.com/BepInEx/BepInEx/blob/master/BepInEx.Core/Bootstrap/BaseChainloader.cs
/// </summary>
public sealed class GameRuntimeDiagnostics(LogService logs)
{
    private const int MaxFiles = 16;
    private const int MaxBytesPerFile = 128 * 1024;
    private const int MaxBytesPerPoll = 512 * 1024;
    private const int MaxLineLength = 8192;
    private const int AnchorBytes = 256;
    private static readonly Regex LogFileName = new(@"^LogOutput(?:[._-][\w.-]{1,64})?\.(?:log|txt)(?:\.\d{1,3})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex HttpCode = new(@"\bHTTP\s*([1-5]\d\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private readonly ConcurrentDictionary<string, Activity> _activities = new(StringComparer.Ordinal);

    public void BeginLaunch(GameRecord game)
    {
        var activity = _activities.GetOrAdd(game.Id, _ => new());
        lock (activity.Gate)
        {
            activity.Clear();
            activity.LaunchedAt = DateTimeOffset.UtcNow;
            // The offsets and anchors survive append mode, overwrite mode, and numbered log files.
            foreach (var path in FindLogs(game, activity))
            {
                try
                {
                    using var stream = OpenSafe(game, path);
                    var cursor = NewCursor(path, stream, stream.Length);
                    activity.Cursors[path] = cursor;
                    activity.InitialBoundaries.Add(new(cursor.Position, cursor.CreatedTicks, cursor.Anchor));
                }
                catch (Exception ex) when (IsReadError(ex)) { activity.UnreadableLog = true; }
            }
        }
        logs.Write("info", "runtime", "开始本次运行诊断；历史加载日志和计数不计入本次。", game.Id);
    }

    public void Receive(string gameId, int length)
    {
        var activity = _activities.GetOrAdd(gameId, _ => new());
        lock (activity.Gate)
        {
            activity.Received++;
            activity.LastSeen = DateTimeOffset.UtcNow;
            if (activity.Received == 1) logs.Write("info", "runtime", "已收到第一条游戏运行时翻译请求。", gameId, details: new { length = Math.Max(0, length) });
        }
    }

    public void BeginHook(string gameId)
    {
        var activity = _activities.GetOrAdd(gameId, _ => new());
        lock (activity.Gate) { activity.Clear(); activity.HookActive = true; activity.LaunchedAt = DateTimeOffset.UtcNow; }
    }
    public void HookConnected(string gameId)
    {
        var activity = _activities.GetOrAdd(gameId, _ => new());
        lock (activity.Gate) { activity.HookActive = true; activity.HookConnected = true; }
        logs.Write("info", "hook", "游戏 Hook 已连接，等待选择对话文本通道。", gameId);
    }
    public void EndHook(string gameId)
    {
        if (_activities.TryGetValue(gameId, out var activity)) lock (activity.Gate) { activity.HookActive = false; activity.HookConnected = false; }
    }
    public void CompleteHook(string gameId, bool embed)
    {
        var activity = _activities.GetOrAdd(gameId, _ => new());
        lock (activity.Gate) { activity.Delivered++; activity.HookEmbedded = embed; activity.BridgeError = null; activity.BridgePaused = false; activity.LastSeen = DateTimeOffset.UtcNow; }
        if (activity.Delivered == 1) logs.Write("info", "hook", embed ? "已向内嵌通道发送译文；是否应用需查看游戏画面。" : "已更新翻译浮窗的译文。", gameId);
    }

    public void Obtained(string gameId, TranslationResult result)
    {
        var activity = _activities.GetOrAdd(gameId, _ => new());
        lock (activity.Gate)
        {
            activity.Translated++;
            if (result.Cached) activity.CacheHits++;
            activity.LastSeen = DateTimeOffset.UtcNow;
            if (activity.Translated == 1) logs.Write("info", "runtime", "已获得第一条游戏译文，等待回传适配器。", gameId);
        }
    }
    public void Complete(string gameId, TranslationResult result, bool alreadyObtained = false)
    {
        var activity = _activities.GetOrAdd(gameId, _ => new());
        lock (activity.Gate)
        {
            if (!alreadyObtained) Obtained(gameId, result);
            activity.Delivered++;
            activity.LastSeen = DateTimeOffset.UtcNow;
            activity.BridgeError = null;
            activity.BridgePaused = false;
            if (activity.Delivered == 1) logs.Write("info", "runtime", "已向游戏适配器回传第一条译文；画面显示仍需在游戏内核验。", gameId);
        }
    }

    public void ReportRpgStatus(string gameId, string session, long applied, string fontStatus)
    {
        var activity = _activities.GetOrAdd(gameId, _ => new());
        lock (activity.Gate)
        {
            if (activity.AdapterSession != session) { activity.AdapterSession = session; activity.PluginApplied = 0; }
            activity.PluginApplied = Math.Max(activity.PluginApplied, Math.Clamp(applied, 0, 1000000000));
            activity.FontStatus = fontStatus;
            activity.AdapterVersion = "0.2.0";
            activity.StatusAt = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(string gameId, string error)
    {
        var activity = _activities.GetOrAdd(gameId, _ => new());
        lock (activity.Gate)
        {
            activity.Failed++;
            activity.LastSeen = DateTimeOffset.UtcNow;
            // Never expose upstream output, source text, an arbitrary exception, or a bridge URL here.
            // Detailed errors remain in the existing sanitized application log.
            var code = HttpCode.Match(error ?? "");
            activity.BridgePaused = error is "翻译已暂停。" or "该游戏翻译已暂停。";
            activity.BridgeError = activity.BridgePaused ? "翻译开关已暂停，请启用后重试。" : code.Success
                ? $"游戏运行时翻译请求失败（HTTP {code.Groups[1].Value}），请查看工具日志。"
                : "游戏运行时翻译请求失败，请查看工具日志。";
        }
    }

    public void Reset(string gameId)
    {
        if (_activities.TryGetValue(gameId, out var activity)) lock (activity.Gate) activity.Clear();
    }

    public GameRuntimeSnapshot Get(GameRecord game, string adapterStatus, bool globalTranslationEnabled = true)
    {
        var activity = _activities.GetOrAdd(game.Id, _ => new());
        var running = IsRunning(game);
        lock (activity.Gate)
        {
            if (globalTranslationEnabled && game.Enabled && activity.BridgePaused)
            {
                activity.BridgeError = null;
                activity.BridgePaused = false;
            }
            if (game.Engine == "Unity" && activity.LaunchedAt.HasValue) ReadNewLogs(game, activity);
            var snapshot = new GameRuntimeSnapshot
            {
                AdapterInstalled = adapterStatus == "installed" || activity.HookActive,
                GameRunning = running,
                GlobalTranslationEnabled = globalTranslationEnabled,
                GameTranslationEnabled = game.Enabled,
                PauseReason = !globalTranslationEnabled ? activity.HookActive ? "全局翻译已暂停，启用后会继续处理当前 Hook 原文。" : "全局翻译已暂停。游戏仍可发送文字，但不会调用翻译服务。请启用全局翻译后重新启动游戏。"
                    : !game.Enabled ? activity.HookActive ? "此游戏的翻译开关已关闭，启用后会继续处理当前 Hook 原文。" : "此游戏的翻译开关已关闭。请启用此游戏翻译后重新启动游戏。" : null,
                LoaderLogFound = activity.LoaderLogFound || activity.HookConnected,
                TranslatorAttempted = activity.TranslatorAttempted || activity.HookConnected,
                DeliveryMode = activity.HookActive ? activity.HookEmbedded ? "hook-embedded" : "floating" : "embedded",
                Received = activity.Received,
                Translated = activity.Translated,
                Delivered = activity.Delivered,
                Failed = activity.Failed,
                CacheHits = activity.CacheHits,
                LastSeen = activity.LastSeen,
                LaunchedAt = activity.LaunchedAt,
                LastError = activity.BridgeError ?? activity.LoaderError,
            };
            if (snapshot.AdapterInstalled && snapshot.PauseReason != null && activity.LoggedPauseReason != snapshot.PauseReason)
            {
                logs.Write("warning", "runtime", snapshot.PauseReason, game.Id);
                activity.LoggedPauseReason = snapshot.PauseReason;
            }
            if (snapshot.PauseReason == null) activity.LoggedPauseReason = null;
            if (game.Engine == "Unreal Engine" && running && snapshot.AdapterInstalled) ReadUnrealStatus(game, activity, snapshot);
            if (game.Engine is "RPG Maker MV" or "RPG Maker MZ" && activity.StatusAt.HasValue)
            {
                snapshot.PluginApplied = activity.PluginApplied; snapshot.FontStatus = activity.FontStatus; snapshot.AdapterVersion = activity.AdapterVersion;
                snapshot.PluginHeartbeatAt = activity.StatusAt;
                snapshot.PluginStatusCurrent = running && activity.StatusAt > DateTimeOffset.UtcNow.AddSeconds(-15);
                if (snapshot.PluginStatusCurrent) snapshot.TranslatorAttempted = true;
                snapshot.Notes.Add($"{(snapshot.PluginStatusCurrent ? "游戏插件" : "上次游戏插件状态")}报告已调用译文绘制 {activity.PluginApplied} 次；不代表像素显示或全部字形已验证。");
                if (activity.FontStatus == "unavailable") snapshot.Notes.Add("上次游戏浏览器字体检查未通过，可能仍在加载或所选字体不可用。请结合状态时间检查字库文件与画面。");
            }
            snapshot.Stage = !snapshot.AdapterInstalled ? "not-installed"
                : snapshot.PauseReason != null ? "paused"
                : snapshot.LastError != null ? "error"
                : snapshot.Delivered > 0 ? "delivered"
                : snapshot.Received > 0 ? "receiving"
                : snapshot.TranslatorAttempted ? "translator-attempted"
                : snapshot.LoaderLogFound ? "loader-detected"
                : activity.LaunchedAt.HasValue || running ? "launched" : "ready";
            if (!activity.LaunchedAt.HasValue) snapshot.Notes.Add("尚未通过本工具启动本次诊断；已有加载日志不会作为本次加载证据。直接启动游戏收到的接口请求仍会计数。");
            if (!activity.HookActive && game.Engine == "Unity" && activity.LoaderLogFound) snapshot.Notes.Add("发现本次启动后的 BepInEx 日志内容；这只说明加载器产生了日志。");
            if (!activity.HookActive && game.Engine == "Unity" && activity.TranslatorAttempted) snapshot.Notes.Add("日志出现 XUnity.AutoTranslator 加载尝试；Loading 或 Chainloader startup complete 均不能单独证明插件加载成功。");
            if (snapshot.Delivered > 0) snapshot.Notes.Add("译文回传只证明接口链路工作，不证明游戏已显示译文或中文字体正常。");
            if (activity.InteropWork) snapshot.Notes.Add("IL2CPP 正在准备运行所需的 interop 或基础库，首次启动可能较慢。官方加载器可能下载 Unity 基础库；这不使用翻译服务的 API Key。");
            else if (activity.InteropPrepared) snapshot.Notes.Add("本次日志曾记录 IL2CPP interop 或基础库准备，随后已进入插件加载阶段。官方基础库下载不使用翻译服务的 API Key。");
            snapshot.Notes.AddRange(activity.LoaderWarnings);
            if (activity.UnsafeLog) snapshot.Notes.Add("日志路径含符号链接、目录连接或其他重解析点，已跳过读取。");
            if (activity.UnreadableLog) snapshot.Notes.Add("部分加载日志暂时无法读取，可在游戏退出后重试。");
            if (activity.ReadLimit) snapshot.Notes.Add("本次诊断按大小上限分批读取日志，后续刷新会继续读取。");
            if (adapterStatus == "conflict") snapshot.Notes.Add("适配器文件存在冲突，请先查看安装检查或恢复提示。");
            if (!globalTranslationEnabled && !game.Enabled) snapshot.Notes.Add("此游戏的翻译开关也已关闭；需要同时启用全局翻译和此游戏翻译。");
            if (activity.HookActive) snapshot.Notes.Add(activity.HookEmbedded ? "当前使用 Hook 内嵌通道，发送译文不代表游戏已画出中文；超过等待时间会保留原文。" : "当前使用 Hook 翻译浮窗，译文没有写回游戏文本；仅翻译已选择的通道。");
            return snapshot;
        }
    }

    private static bool IsRunning(GameRecord game)
    {
        if (game.Engine == "Unreal Engine") return UnrealLayout.IsRunning(game);
        try
        {
            var expected = Path.GetFullPath(game.ExecutablePath);
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && process.MainModule?.FileName is { } file && Path.GetFullPath(file).Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return false;
    }

    private static void ReadUnrealStatus(GameRecord game, Activity activity, GameRuntimeSnapshot snapshot)
    {
        try
        {
            var mod = UnrealLayout.ModDirectory(game);
            if (mod == null) return;
            var path = Path.Combine(mod, "status.json");
            if (!File.Exists(path) || !UnrealLayout.SafePath(game.Directory, path)) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > 4096) return;
            using var doc = JsonDocument.Parse(stream);
            var status = doc.RootElement;
            if (status.ValueKind != JsonValueKind.Object) return;
            if (!status.TryGetProperty("heartbeat", out var beat) || beat.ValueKind != JsonValueKind.Number || !beat.TryGetInt64(out var seconds)) return;
            var heartbeat = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if ((DateTimeOffset.UtcNow - heartbeat).TotalSeconds is < -2 or > 15 || (activity.LaunchedAt.HasValue && heartbeat < activity.LaunchedAt.Value.AddSeconds(-1))) return;
            snapshot.LoaderLogFound = status.TryGetProperty("loaded", out var loaded) && loaded.ValueKind == JsonValueKind.True;
            long Count(string field) => status.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? Math.Clamp(n, 0, 1000000000) : 0;
            snapshot.TranslatorAttempted = Count("hooks") > 0;
            snapshot.PluginApplied = Count("applied"); snapshot.PluginVerified = Count("verified"); snapshot.VisibleWidgets = (int)Count("visible");
            snapshot.Notes.Add($"UE4SS 翻译插件正在上报：可见文字控件 {snapshot.VisibleWidgets} 个，写入译文 {snapshot.PluginApplied} 次，文字属性回读一致 {snapshot.PluginVerified} 次。画面及字体效果仍需游戏内核验。");
            if (Count("fontMissing") > 0) snapshot.Notes.Add("所选 Unreal Font 资源尚未被游戏加载，当前保留原字体；请检查字体资源路径和游戏字库。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException) { }
    }

    private static List<string> FindLogs(GameRecord game, Activity activity)
    {
        try
        {
            var directory = Path.Combine(Path.GetFullPath(game.Directory), "BepInEx");
            if (!Directory.Exists(directory)) return [];
            if (!PathIsSafe(game, directory)) { activity.UnsafeLog = true; return []; }
            var files = new List<(string Path, DateTime Time)>();
            var inspected = 0;
            foreach (var path in Directory.EnumerateFiles(directory, "LogOutput*", SearchOption.TopDirectoryOnly))
            {
                if (++inspected > 64) { activity.ReadLimit = true; break; }
                if (!LogFileName.IsMatch(Path.GetFileName(path))) continue;
                if (!PathIsSafe(game, path)) { activity.UnsafeLog = true; continue; }
                files.Add((path, File.GetLastWriteTimeUtc(path)));
            }
            if (files.Count > MaxFiles) activity.ReadLimit = true;
            return files.OrderByDescending(f => f.Time).Take(MaxFiles).Select(f => f.Path).ToList();
        }
        catch (Exception ex) when (IsReadError(ex)) { activity.UnreadableLog = true; return []; }
    }

    private static FileStream OpenSafe(GameRecord game, string path)
    {
        if (!PathIsSafe(game, path)) throw new IOException("Unsafe log path.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
    }

    private static bool PathIsSafe(GameRecord game, string path)
    {
        var root = Path.GetFullPath(game.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        // Check ancestors as well: an apparently regular file below a junction is still outside scope.
        var cursor = full;
        while (!string.IsNullOrEmpty(cursor))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) return false;
            cursor = Path.GetDirectoryName(cursor);
        }
        return true;
    }

    private static bool IsReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static Cursor NewCursor(string path, FileStream stream, long position) => new()
    {
        Position = position,
        CreatedTicks = File.GetCreationTimeUtc(path).Ticks,
        Anchor = Anchor(stream, position),
    };

    private static string Anchor(FileStream stream, long position)
    {
        if (position <= 0) return "";
        var count = (int)Math.Min(AnchorBytes, position);
        stream.Position = position - count;
        var bytes = new byte[count];
        var read = stream.Read(bytes, 0, count);
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, read)));
    }

    private static void ReadNewLogs(GameRecord game, Activity activity)
    {
        var budget = MaxBytesPerPoll;
        activity.ReadLimit = false;
        foreach (var path in FindLogs(game, activity))
        {
            if (budget <= 0) { activity.ReadLimit = true; break; }
            try
            {
                using var stream = OpenSafe(game, path);
                if (!activity.Cursors.TryGetValue(path, out var cursor))
                {
                    // Carry the original byte boundary across a rename. Filesystem timestamps can
                    // have less precision than UtcNow, so creation time alone must not reject a new
                    // log a few milliseconds after launch or admit a just-renamed historical log.
                    var created = File.GetCreationTimeUtc(path);
                    var boundary = activity.InitialBoundaries.FirstOrDefault(b => b.CreatedTicks == created.Ticks && b.Position <= stream.Length && Anchor(stream, b.Position) == b.Anchor);
                    if (boundary != null) cursor = NewCursor(path, stream, boundary.Position);
                    else
                    {
                        if (created < activity.LaunchedAt!.Value.UtcDateTime.AddSeconds(-2)) continue;
                        cursor = NewCursor(path, stream, 0);
                    }
                    activity.Cursors[path] = cursor;
                }
                if (stream.Length < cursor.Position || File.GetCreationTimeUtc(path).Ticks != cursor.CreatedTicks || Anchor(stream, cursor.Position) != cursor.Anchor)
                {
                    cursor = NewCursor(path, stream, 0);
                    activity.Cursors[path] = cursor;
                }
                if (stream.Length <= cursor.Position) continue;
                var count = (int)Math.Min(Math.Min(stream.Length - cursor.Position, MaxBytesPerFile), budget);
                stream.Position = cursor.Position;
                var bytes = new byte[count];
                var read = stream.Read(bytes, 0, count);
                if (read <= 0) continue;
                cursor.Position += read;
                cursor.Anchor = Anchor(stream, cursor.Position);
                budget -= read;
                activity.LoaderLogFound = true;
                activity.LastSeen = DateTimeOffset.UtcNow;
                // Use a persistent UTF-8 decoder because a multibyte character can straddle a poll.
                var buffer = new char[read + 2];
                var characters = cursor.Decoder.GetChars(bytes, 0, read, buffer, 0, false);
                ReadLines(new string(buffer, 0, characters), cursor, activity);
                if (stream.Length > cursor.Position) activity.ReadLimit = true;
            }
            catch (Exception ex) when (IsReadError(ex)) { activity.UnreadableLog = true; }
        }
    }

    private static void ReadLines(string text, Cursor cursor, Activity activity)
    {
        var combined = cursor.PartialLine + text;
        var start = 0;
        while (start < combined.Length)
        {
            var end = combined.IndexOf('\n', start);
            if (end < 0) break;
            InspectLine(combined.AsSpan(start, Math.Min(end - start, MaxLineLength)), activity);
            start = end + 1;
        }
        var remaining = combined.AsSpan(start);
        // Inspect an unfinished line for flushed logs without a trailing newline, but retain only
        // a bounded fragment so a malicious single-line file cannot accumulate in memory.
        InspectLine(remaining[..Math.Min(remaining.Length, MaxLineLength)], activity);
        cursor.PartialLine = remaining.Length > MaxLineLength ? "" : remaining.ToString();
    }

    private static void InspectLine(ReadOnlySpan<char> line, Activity activity)
    {
        var xunity = line.Contains("XUnity", StringComparison.OrdinalIgnoreCase);
        if (xunity && (line.Contains("Loading [", StringComparison.OrdinalIgnoreCase) || line.Contains("[XUnity", StringComparison.OrdinalIgnoreCase))) activity.TranslatorAttempted = true;
        var error = line.Contains("[Error", StringComparison.OrdinalIgnoreCase) || line.Contains("[Fatal", StringComparison.OrdinalIgnoreCase);
        if (line.Contains("Unable to execute IL2CPP chainloader", StringComparison.OrdinalIgnoreCase))
            activity.LoaderError = "IL2CPP 加载器无法执行，插件未加载，请查看游戏 BepInEx 日志。";
        else if (line.Contains("Failed to generate Il2Cpp interop assemblies", StringComparison.OrdinalIgnoreCase))
            activity.LoaderError = "IL2CPP interop 生成失败，请查看游戏 BepInEx 日志。";
        else if (error && xunity && (line.Contains("Error loading [", StringComparison.OrdinalIgnoreCase) || line.Contains("Could not load [", StringComparison.OrdinalIgnoreCase)))
            activity.LoaderError = "加载日志报告 XUnity.AutoTranslator 插件加载失败，请查看游戏 BepInEx 日志。";
        else if (error && line.Contains("Error occurred loading plugins", StringComparison.OrdinalIgnoreCase))
            activity.LoaderError = "加载日志报告插件加载流程失败，请查看游戏 BepInEx 日志。";
        else if (error && line.Contains("Il2CppInterop", StringComparison.OrdinalIgnoreCase))
            activity.LoaderWarnings.Add("日志包含 IL2CPP 类型解析或 interop 错误，但没有识别到致命加载失败；请结合实际取词、回传和游戏画面排查。此提示不会覆盖已收到的运行时译文计数。");
        else if (error && xunity)
            activity.LoaderWarnings.Add("日志包含 XUnity.AutoTranslator 错误，但没有识别到明确插件加载失败；请查看游戏日志并核验实际翻译效果。");
        else if (error && line.Contains("Preloader", StringComparison.OrdinalIgnoreCase))
            activity.LoaderWarnings.Add("日志包含 Preloader 错误，但没有识别到明确加载失败；请查看游戏日志并核验插件加载和取词情况。");
        if ((line.Contains("Generating", StringComparison.OrdinalIgnoreCase) || line.Contains("Downloading", StringComparison.OrdinalIgnoreCase)) &&
            (line.Contains("interop", StringComparison.OrdinalIgnoreCase) || line.Contains("Unity base", StringComparison.OrdinalIgnoreCase) || line.Contains("Il2Cpp", StringComparison.OrdinalIgnoreCase)))
        {
            activity.InteropPrepared = true;
            if (!activity.ChainloaderEntered) activity.InteropWork = true;
        }
        if (line.Contains("Chainloader initialized", StringComparison.OrdinalIgnoreCase) || line.Contains("Chainloader startup complete", StringComparison.OrdinalIgnoreCase))
        {
            activity.ChainloaderEntered = true;
            activity.InteropWork = false;
        }
    }

    private sealed class Cursor
    {
        public long Position;
        public long CreatedTicks;
        public string Anchor = "";
        public string PartialLine = "";
        public Decoder Decoder = Encoding.UTF8.GetDecoder();
    }

    private sealed class Activity
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, Cursor> Cursors = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<InitialBoundary> InitialBoundaries = [];
        public DateTimeOffset? LaunchedAt, LastSeen, StatusAt;
        public long Received, Translated, Delivered, Failed, CacheHits, PluginApplied;
        public string? AdapterSession, AdapterVersion, FontStatus;
        public bool LoaderLogFound, TranslatorAttempted, InteropWork, InteropPrepared, ChainloaderEntered, UnsafeLog, UnreadableLog, ReadLimit;
        public readonly HashSet<string> LoaderWarnings = new(StringComparer.Ordinal);
        public string? BridgeError, LoaderError, LoggedPauseReason;
        public bool BridgePaused;
        public bool HookActive, HookConnected, HookEmbedded;
        public void Clear()
        {
            Cursors.Clear(); InitialBoundaries.Clear(); LaunchedAt = LastSeen = StatusAt = null;
            Received = Translated = Delivered = Failed = CacheHits = PluginApplied = 0;
            AdapterSession = AdapterVersion = FontStatus = null;
            LoaderLogFound = TranslatorAttempted = InteropWork = InteropPrepared = ChainloaderEntered = UnsafeLog = UnreadableLog = ReadLimit = false;
            LoaderWarnings.Clear();
            BridgeError = LoaderError = LoggedPauseReason = null;
            BridgePaused = false;
            HookActive = HookConnected = HookEmbedded = false;
        }
    }

    private sealed record InitialBoundary(long Position, long CreatedTicks, string Anchor);
}
