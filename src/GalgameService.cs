using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using GameTranslateToolkit.Updates;

namespace GameTranslateToolkit;

public sealed class HookThread
{
    public string Id { get; set; } = "";
    public string HookCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Context { get; set; } = "";
    public string Context2 { get; set; } = "";
    public bool Embeddable { get; set; }
    public string Preview { get; set; } = "";
    public long Count { get; set; }
    internal string LatestText = "";
}
public sealed record HookProcess(int Pid, string Name, string Path);

/// <summary>Owns one isolated native host. Only selected text enters the existing translation pipeline.</summary>
public sealed class GalgameService(DataStore store, TranslationService translation, GameRuntimeDiagnostics runtime, LogService logs) : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Session? _active;
    private GalgameOverlay? _overlay;
    private string? _overlayGame;
    private readonly string _nativeDir = Path.Combine(UpdatePackage.ApplicationDirectory, "adapters", "galgame", "native");
    public static bool Supported(GameRecord game) => game.Architecture is "x86" or "x64";
    private bool ComponentsPresent => new[] { "LunaHost64.dll", "LunaHook32.dll", "LunaHook64.dll", "LunaSubprocess32.exe", "LunaSubprocess64.exe" }.All(f => File.Exists(Path.Combine(_nativeDir, f))) && File.Exists(WorkerPath);
    private string WorkerPath => Path.Combine(UpdatePackage.ApplicationDirectory, "adapters", "galgame", "RikaHookWorker.exe");
    private sealed class Session(GameRecord game, int pid, Process process)
    {
        public readonly object Gate = new();
        public readonly string GameId = game.Id;
        public readonly int Pid = pid;
        public readonly Process Worker = process;
        public readonly CancellationTokenSource Stop = new();
        public readonly SemaphoreSlim Commands = new(1, 1);
        public readonly Dictionary<string, HookThread> Threads = [];
        public readonly Channel<Work> Queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(6) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
        public CancellationTokenSource Selection = new();
        public string Selected = "", Stage = "connecting", LastError = "", Source = "", Result = "", DisplayStatus = "等待选择对话文本通道";
        public bool Connected, Embed;
        public string LastEmbedRequest = "";
        public bool LastEmbedTimedOut;
        public long Received, Translated, Failed, Sequence, Revision, EmbeddedReplies, EmbeddingTimeouts;
        public DateTimeOffset Started = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastSeen;
    }
    private sealed record Work(string ThreadId, string Text, string? RequestId, long Revision, long Sequence, CancellationToken Selection);
    private GameRecord Game(string id) => store.Snapshot().Games.FirstOrDefault(g => g.Id == id) ?? throw new KeyNotFoundException("游戏记录不存在。");
    public static GalgameSettings ValidateSettings(GalgameSettings value)
    {
        if (value.Codepage is not (932 or 936 or 950 or 65001 or 1200)) throw new ArgumentException("编码请选择日文 Shift-JIS、GBK、Big5、UTF-8 或 UTF-16。");
        if (value.WaitMs is < 200 or > 5000) throw new ArgumentException("内嵌等待时间应为 200–5000 毫秒。");
        if (value.EmbedFontSizePercent is < 50 or > 200) throw new ArgumentException("内嵌字号应为原字号的 50–200%，100% 保持原大小。");
        if (value.FontSize is < 12 or > 48 || string.IsNullOrWhiteSpace(value.FontFamily) || value.FontFamily.Length > 90 || value.FontFamily.Any(char.IsControl)) throw new ArgumentException("浮窗字体名称或字号无效。");
        if (value.FontColor == null || value.FontColor.Length != 7 || !Regex.IsMatch(value.FontColor, @"\A#[0-9a-fA-F]{6}\z", RegexOptions.CultureInvariant)) throw new ArgumentException("浮窗字体颜色应为 #RRGGBB，例如 #FFFFFF。");
        value.FontColor = value.FontColor.ToUpperInvariant();
        if (value.ManualCodes == null || value.ManualCodes.Count > 10 || value.ManualCodes.Any(c => !ValidHookCode(c))) throw new ArgumentException("最多保存 10 条有效 Hook 代码。");
        if (value.HookCode.Length > 1000 || value.HookName.Length > 200 || value.Context.Length > 20 || value.Context2.Length > 20) throw new ArgumentException("文本通道配置无效。");
        return value;
    }
    public static bool ValidHookCode(string code) => code.Length is > 2 and <= 1000 && Regex.IsMatch(code, @"^(?:E[^\s]*|R[^\s]*|H[^\s]*)@[^\s]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static string NormalizeText(string text, bool deduplicate)
    {
        if (!deduplicate || text.Length < 8) return text;
        for (var count = 8; count >= 2; count--)
        {
            if (text.Length % count != 0 || text.Length / count < 4) continue;
            var length = text.Length / count; var unit = text.AsSpan(0, length); var repeated = true;
            for (var i = 1; i < count; i++) if (!text.AsSpan(i * length, length).SequenceEqual(unit)) { repeated = false; break; }
            if (repeated) return text[..length];
        }
        return text;
    }
    public static IReadOnlyList<HookProcess> Processes(GameRecord game)
    {
        var result = new List<HookProcess>();
        if (!Directory.Exists(game.Directory)) return result;
        var root = Path.GetFullPath(game.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.HasExited) continue;
                    var path = process.MainModule?.FileName;
                    if (path == null || !Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Regex.IsMatch(Path.GetFileName(path), @"^(?:unitycrash|crash|unins|setup|rika|luna|renpythief)", RegexOptions.IgnoreCase)) continue;
                    result.Add(new(process.Id, process.ProcessName, path));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return result.Take(32).ToArray();
    }
    public object Snapshot(GameRecord game)
    {
        Session? session; lock (_gate) session = _active?.GameId == game.Id ? _active : null;
        if (session == null) return new { supported = Supported(game), componentsPresent = ComponentsPresent, active = false, stage = "stopped", settings = game.Galgame, threads = Array.Empty<HookThread>(), selectedId = "", embeddable = false, overlayAvailable = ToolkitWindow.Current != null, overlayShown = _overlayGame == game.Id && _overlay?.Visible == true, lastError = "", source = "", translation = "", received = 0, translated = 0, failed = 0, embeddedReplies = 0, embeddingTimeouts = 0 };
        lock (session.Gate) return new { supported = Supported(game), componentsPresent = ComponentsPresent, active = !session.Stop.IsCancellationRequested && !session.Worker.HasExited, stage = session.Stage, connected = session.Connected, pid = session.Pid, settings = game.Galgame, threads = session.Threads.Values.Select(t => new HookThread { Id = t.Id, HookCode = t.HookCode, Name = t.Name, Context = t.Context, Context2 = t.Context2, Embeddable = t.Embeddable, Preview = t.Preview, Count = t.Count }).ToArray(), selectedId = session.Selected, embeddable = session.Threads.GetValueOrDefault(session.Selected)?.Embeddable ?? false, embedding = session.Embed, overlayAvailable = ToolkitWindow.Current != null, overlayShown = _overlayGame == game.Id && _overlay?.Visible == true, lastError = session.LastError, source = session.Source, translation = session.Result, received = session.Received, translated = session.Translated, failed = session.Failed, embeddedReplies = session.EmbeddedReplies, embeddingTimeouts = session.EmbeddingTimeouts, lastSeen = session.LastSeen, started = session.Started, displayStatus = session.DisplayStatus };
    }
    public async Task<object> StartAsync(GameRecord game, int pid, bool launch, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            if (!Supported(game)) throw new InvalidOperationException("Hook 目前只支持 Windows x86/x64 游戏，请重新选择正确的游戏 EXE。");
            if (game.AdapterStatus is "installed" or "conflict") throw new InvalidOperationException("此游戏已有适配组件。请先卸载或恢复适配，再使用 Hook，避免重复翻译。");
            if (!ComponentsPresent) throw new FileNotFoundException("Galgame Hook 组件不完整，请使用完整的 0.7.0 便携版或本地更新包。");
            VerifyNativeComponents();
            lock (_gate) if (_active is { } old && !old.Stop.IsCancellationRequested && !old.Worker.HasExited) throw new InvalidOperationException("已有 Hook 会话，请先停止当前连接后再连接游戏。");
            var candidates = Processes(game);
            if (pid == 0)
            {
                var exact = candidates.Where(p => p.Path.Equals(game.ExecutablePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Length > 1) throw new InvalidOperationException("发现多个游戏进程，请在进程列表中选择要连接的一项。");
                if (exact.Length == 1) pid = exact[0].Pid;
                else if (launch)
                {
                    runtime.BeginLaunch(game);
                    Process.Start(new ProcessStartInfo(game.ExecutablePath) { WorkingDirectory = game.Directory, UseShellExecute = true });
                    for (var attempt = 0; attempt < 40 && pid == 0; attempt++)
                    {
                        await Task.Delay(200, ct);
                        var current = Processes(game).Where(p => p.Path.Equals(game.ExecutablePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (current.Length == 1) pid = current[0].Pid;
                        else if (current.Length > 1) break;
                    }
                }
            }
            var target = Processes(game).FirstOrDefault(p => p.Pid == pid) ?? throw new InvalidOperationException("未找到可连接的游戏进程。请先启动游戏，再刷新并选择进程。");
            var architecture = EngineService.ExecutableArchitecture(target.Path);
            if (architecture is not ("x86" or "x64")) throw new InvalidOperationException("目标进程架构不支持 Hook。");
            await store.UpdateAsync(d => { var settings = d.Games.First(g => g.Id == game.Id).Galgame; settings.Enabled = true; settings.Embed = false; });
            // Worker also rechecks PID/path before injecting, preventing recycled PID attachment.
            var start = new ProcessStartInfo(WorkerPath) { WorkingDirectory = _nativeDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var arg in new[] { _nativeDir, pid.ToString(), target.Path, architecture, (game.Galgame.SystemHooks || game.Engine == "Unknown") ? "true" : "false" }) start.ArgumentList.Add(arg);
            var worker = Process.Start(start) ?? throw new IOException("无法启动 Hook 会话。");
            var session = new Session(game, pid, worker);
            lock (_gate) _active = session;
            runtime.BeginHook(game.Id);
            _ = ReadEventsAsync(session); _ = TranslateQueueAsync(session);
            _ = Task.Run(async () => { while (await worker.StandardError.ReadLineAsync() is { } error) logs.Write("warning", "hook", "Hook 组件输出错误，请检查组件与进程权限。", game.Id, details: error[..Math.Min(1000, error.Length)]); });
            logs.Write("info", "hook", "开始连接游戏 Hook；原游戏目录不写入适配文件。", game.Id, details: new { pid, architecture, nativeVersion = "12.0.1-rika-fontfix1" });
            if (ToolkitWindow.Current != null) try { await ShowOverlayAsync(game.Id, true); } catch (Exception ex) { logs.Write("warning", "hook", "Hook 已连接，但浮窗打开失败，可在游戏详情重新打开。", game.Id, details: ex.Message); }
            return Snapshot(Game(game.Id));
        }
        finally { _lifecycle.Release(); }
    }
    public async Task StopAsync(string gameId)
    {
        await _lifecycle.WaitAsync();
        try
        {
            Session? session; lock (_gate) session = _active?.GameId == gameId ? _active : null;
            if (session == null) return;
            session.Stop.Cancel(); session.Selection.Cancel(); session.Queue.Writer.TryComplete();
            try { await SendAsync(session, new { command = "stop" }); await session.Worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { try { if (!session.Worker.HasExited) session.Worker.Kill(entireProcessTree: true); } catch { } }
            lock (session.Gate) { session.Stage = "stopped"; session.Connected = false; session.Embed = false; }
            if (ToolkitWindow.Current != null && _overlayGame == gameId) await ToolkitWindow.OnUiAsync(() => _overlay?.Hide());
            runtime.EndHook(gameId);
            logs.Write("info", "hook", "Hook 已断开，游戏继续运行；重启游戏可完全清除本次注入状态。", gameId);
        }
        finally { _lifecycle.Release(); }
    }
    private Session Active(string gameId)
    {
        lock (_gate) return _active?.GameId == gameId && !_active.Stop.IsCancellationRequested && !_active.Worker.HasExited ? _active : throw new InvalidOperationException("此游戏尚未连接 Hook。");
    }
    private static async Task SendAsync(Session session, object message)
    {
        await session.Commands.WaitAsync();
        try { if (!session.Worker.HasExited) { await session.Worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)); await session.Worker.StandardInput.FlushAsync(); } }
        finally { session.Commands.Release(); }
    }
    public async Task SelectAsync(string gameId, string threadId)
    {
        var session = Active(gameId); HookThread thread;
        lock (session.Gate)
        {
            thread = session.Threads.GetValueOrDefault(threadId) ?? throw new ArgumentException("文本通道已失效，请刷新列表后重选。");
            session.Selection.Cancel(); session.Selection = new(); session.Revision++;
            session.Selected = threadId; session.Embed = false; session.Source = ""; session.Result = ""; session.LastError = "";
            session.DisplayStatus = "已选择 " + thread.Name + "，等待下一句对话";
        }
        await SendAsync(session, new { command = "select", id = threadId });
        await store.UpdateAsync(d => { var settings = d.Games.First(g => g.Id == gameId).Galgame; settings.HookCode = thread.HookCode; settings.HookName = thread.Name; settings.Context = thread.Context; settings.Context2 = thread.Context2; settings.Embed = false; });
        await ConfigureAsync(gameId);
        logs.Write("info", "hook", "已选择文本通道；仅翻译该通道的新文字。内嵌默认关闭。", gameId, details: new { thread.Name, thread.Embeddable });
        await UpdateOverlayAsync(session);
        if (!string.IsNullOrWhiteSpace(thread.LatestText)) Queue(session, threadId, thread.LatestText, null);
    }
    public async Task ResetTranslationAsync(string? gameId = null)
    {
        Session? session; lock (_gate) session = _active;
        if (session == null || (gameId != null && session.GameId != gameId) || session.Stop.IsCancellationRequested || session.Worker.HasExited) return;
        string text, selected;
        lock (session.Gate)
        {
            session.Selection.Cancel(); session.Selection = new(); session.Revision++;
            selected = session.Selected; text = session.Threads.GetValueOrDefault(selected)?.LatestText ?? "";
            session.Source = ""; session.Result = ""; session.LastError = ""; session.DisplayStatus = "设置已更新，等待下一句对话";
        }
        await SendAsync(session, new { command = "cancel" });
        var data = store.Snapshot(); var game = data.Games.First(g => g.Id == session.GameId);
        if (game.Enabled && data.Settings.Enabled && text.Length > 0) Queue(session, selected, text, null);
        else await UpdateOverlayAsync(session);
    }
    public async Task RemoveCodeAsync(string gameId, string code)
    {
        await store.UpdateAsync(d => d.Games.First(g => g.Id == gameId).Galgame.ManualCodes.Remove(code));
        logs.Write("info", "hook", "已移除保存的 Hook 代码；下次连接不再插入，当前钩子需重启游戏清除。", gameId);
    }
    public async Task ConfigureAsync(string gameId)
    {
        Session? session; lock (_gate) session = _active?.GameId == gameId && !_active.Stop.IsCancellationRequested ? _active : null;
        if (session == null || session.Worker.HasExited) return;
        var settings = ValidateSettings(Game(gameId).Galgame);
        lock (session.Gate)
        {
            if (settings.Embed && session.Threads.GetValueOrDefault(session.Selected)?.Embeddable != true) throw new InvalidOperationException("请先选择标为可内嵌的文本通道。");
            session.Embed = settings.Embed;
        }
        await SendAsync(session, new { command = "configure", codepage = settings.Codepage, waitMs = settings.WaitMs, font = settings.FontFamily, embedFontSizePercent = settings.EmbedFontSizePercent, embed = settings.Embed, systemHooks = settings.SystemHooks || Game(gameId).Engine == "Unknown" });
        await UpdateOverlayAsync(session);
    }
    public async Task InsertAsync(string gameId, string code)
    {
        if (!ValidHookCode(code)) throw new ArgumentException("Hook 代码格式无效；请填写包含 @ 地址部分的 H/R/E 代码。");
        var session = Active(gameId);
        await store.UpdateAsync(d => { var codes = d.Games.First(g => g.Id == gameId).Galgame.ManualCodes; if (!codes.Contains(code)) { if (codes.Count >= 10) throw new ArgumentException("最多保存 10 条手动 Hook 代码。"); codes.Add(code); } });
        await SendAsync(session, new { command = "insert", code });
        logs.Write("info", "hook", "已向 Hook 组件发送手动代码；是否正确取词以文本列表为准。", gameId);
    }
    public async Task ShowOverlayAsync(string gameId, bool show)
    {
        var game = Game(gameId);
        await ToolkitWindow.OnUiAsync(() => {
            if (!show) { if (_overlayGame == gameId) _overlay?.Hide(); return; }
            if (_overlay == null || _overlay.IsDisposed || _overlayGame != gameId) { _overlay?.Dispose(); _overlay = new(game.Name, message => logs.Write("error", "hook", "透明翻译浮窗绘制失败。", game.Id, details: message)); _overlayGame = gameId; }
            _overlay.ApplySettings(game.Galgame);
            if (!_overlay.Visible) _overlay.Show();
        });
        Session? session; lock (_gate) session = _active?.GameId == gameId ? _active : null;
        if (session != null) await UpdateOverlayAsync(session);
    }
    public async Task RefreshOverlaySettingsAsync(string gameId)
    {
        if (ToolkitWindow.Current == null || _overlayGame != gameId) return;
        var settings = Game(gameId).Galgame;
        await ToolkitWindow.OnUiAsync(() => { if (_overlayGame == gameId && _overlay is { IsDisposed: false }) _overlay.ApplySettings(settings); });
    }
    private async Task UpdateOverlayAsync(Session session)
    {
        if (ToolkitWindow.Current == null || _overlayGame != session.GameId) return;
        string source, result, status; lock (session.Gate) { source = session.Source; result = session.Result; status = session.DisplayStatus; }
        try { var settings = Game(session.GameId).Galgame; await ToolkitWindow.OnUiAsync(() => { if (_overlayGame == session.GameId && _overlay is { IsDisposed: false }) _overlay.UpdateText(source, result, status, settings); }); } catch (InvalidOperationException) { }
    }
    private async Task ReadEventsAsync(Session session)
    {
        try
        {
            while (await session.Worker.StandardOutput.ReadLineAsync(session.Stop.Token) is { } line)
            {
                if (line.Length > 500000) continue;
                JsonDocument document; try { document = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (document)
                {
                    var item = document.RootElement;
                    string Text(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                    var eventName = Text("event"); var id = Text("id");
                    if (eventName == "thread")
                    {
                        var thread = new HookThread { Id = id, HookCode = Text("hookCode"), Name = Text("name"), Context = Text("context"), Context2 = Text("context2"), Embeddable = item.GetProperty("embeddable").GetBoolean() };
                        lock (session.Gate) {
                            if (session.Threads.Count >= 128 && (thread.Embeddable || !IsSystemThread(thread.Name))) {
                                var remove = session.Threads.Values.FirstOrDefault(t => IsSystemThread(t.Name) && t.Id != session.Selected);
                                if (remove != null) session.Threads.Remove(remove.Id);
                            }
                            if (session.Threads.Count < 128) session.Threads[id] = thread;
                        }
                        var saved = Game(session.GameId).Galgame;
                        if (session.Selected.Length == 0 && saved.HookCode.Length > 0 && saved.HookCode == thread.HookCode && saved.HookName == thread.Name && saved.Context == thread.Context && saved.Context2 == thread.Context2) await SelectAsync(session.GameId, id);
                    }
                    else if (eventName == "removed")
                    {
                        lock (session.Gate) { session.Threads.Remove(id); if (session.Selected == id) { session.Selected = ""; session.Embed = false; session.Revision++; session.Selection.Cancel(); session.Selection = new(); session.DisplayStatus = "对话通道已消失，请重新选择"; } }
                    }
                    else if (eventName == "text")
                    {
                        var text = Text("text"); bool selected;
                        lock (session.Gate) { if (!session.Threads.TryGetValue(id, out var thread)) continue; thread.Preview = text[..Math.Min(1000, text.Length)]; thread.LatestText = text.Length <= 60000 ? text : ""; thread.Count++; session.LastSeen = DateTimeOffset.UtcNow; selected = session.Selected == id && !session.Embed; }
                        if (selected) Queue(session, id, text, null);
                    }
                    else if (eventName == "embed") Queue(session, id, Text("text"), Text("requestId"));
                    else if (eventName == "embed-timeout") {
                        lock (session.Gate) { session.EmbeddingTimeouts++; if (session.LastEmbedRequest == Text("requestId")) { session.LastEmbedTimedOut = true; session.DisplayStatus = "内嵌等待已超时，游戏保留原文；译文返回后仍可在浮窗查看。"; } }
                        logs.Write("warning", "hook", "内嵌等待超时，已保留原文；后续译文供浮窗与缓存使用。", session.GameId);
                        await UpdateOverlayAsync(session);
                    }
                    else if (eventName == "embed-result") {
                        var sent = item.GetProperty("sent").GetBoolean();
                        lock (session.Gate) { if (sent) session.EmbeddedReplies++; if (session.LastEmbedRequest == Text("requestId") && session.LastError.Length == 0) session.DisplayStatus = sent ? "内嵌组件已接收译文 · 请检查游戏画面" : "本句保留原文，译文可在浮窗查看。"; }
                        if (!item.TryGetProperty("fallback", out var fallback) || !fallback.GetBoolean()) runtime.CompleteHook(session.GameId, sent);
                        await UpdateOverlayAsync(session);
                    }
                    else if (eventName is "connected" or "ready")
                    {
                        lock (session.Gate) { session.Connected |= eventName == "connected"; session.Stage = session.Connected ? "connected" : "connecting"; }
                        if (eventName == "connected") { runtime.HookConnected(session.GameId); await ConfigureAsync(session.GameId); }
                        if (eventName == "ready") foreach (var code in Game(session.GameId).Galgame.ManualCodes) await SendAsync(session, new { command = "insert", code });
                    }
                    else if (eventName == "disconnected") { lock (session.Gate) { session.Connected = false; session.Stage = "disconnected"; session.DisplayStatus = "游戏进程已断开"; } runtime.EndHook(session.GameId); }
                    else if (eventName == "error")
                    {
                        lock (session.Gate) { session.LastError = Text("message"); session.Stage = "error"; session.DisplayStatus = session.LastError; }
                        logs.Write("error", "hook", session.LastError, session.GameId); runtime.Fail(session.GameId, session.LastError);
                    }
                    else if (eventName == "log") logs.Write(item.TryGetProperty("type", out var type) && type.GetInt32() is 1 or 2 ? "warning" : "info", "hook", "Hook 组件记录", session.GameId, details: Text("message")[..Math.Min(1000, Text("message").Length)]);
                }
            }
            if (!session.Stop.IsCancellationRequested) { lock (session.Gate) { session.Stage = "disconnected"; session.Connected = false; if (session.LastError.Length == 0) session.LastError = "Hook 会话已结束，请重新连接游戏。"; session.DisplayStatus = session.LastError; } runtime.EndHook(session.GameId); await UpdateOverlayAsync(session); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (session.Gate) { session.Connected = false; session.Stage = "error"; session.LastError = "Hook 事件读取失败，请停止后重新连接。"; } runtime.EndHook(session.GameId); logs.Write("error", "hook", session.LastError, session.GameId, details: ex.Message); await UpdateOverlayAsync(session); }
        finally { session.Queue.Writer.TryComplete(); }
    }
    private void Queue(Session session, string id, string text, string? requestId)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 60000) return;
        text = NormalizeText(text, Game(session.GameId).Galgame.DeduplicateSentences);
        lock (session.Gate)
        {
            if (session.Selected != id || session.Stop.IsCancellationRequested) return;
            if (session.Source == text && requestId == null) return;
            session.Source = text; session.Result = ""; session.DisplayStatus = "正在翻译…"; session.Received++; session.Stage = "receiving";
            session.LastEmbedRequest = requestId ?? ""; session.LastEmbedTimedOut = false;
            var work = new Work(id, text, requestId, session.Revision, ++session.Sequence, session.Selection.Token);
            session.Queue.Writer.TryWrite(work);
        }
        _ = UpdateOverlayAsync(session);
    }
    private async Task TranslateQueueAsync(Session session)
    {
        try
        {
            await foreach (var work in session.Queue.Reader.ReadAllAsync(session.Stop.Token))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token, work.Selection);
                if (work.Selection.IsCancellationRequested) continue;
                runtime.Receive(session.GameId, work.Text.Length);
                try
                {
                    var result = await translation.TranslateAsync(new TranslateInput { GameId = session.GameId, Text = work.Text }, cancellation.Token);
                    bool current; lock (session.Gate) { current = session.Selected == work.ThreadId && session.Revision == work.Revision; if (current) { session.Translated++; session.LastError = ""; session.Stage = "translated"; if (session.Sequence == work.Sequence) { session.Result = result.Text; session.DisplayStatus = work.RequestId == null ? "译文已获得，可在浮窗查看" : session.LastEmbedTimedOut ? "内嵌已超时，译文供浮窗和缓存使用" : "译文已返回，正在交给内嵌组件…"; } } }
                    if (!current || cancellation.IsCancellationRequested) continue;
                    runtime.Obtained(session.GameId, result);
                    if (work.RequestId != null) await SendAsync(session, new { command = "reply", requestId = work.RequestId, text = result.Text });
                    if (work.RequestId == null) runtime.CompleteHook(session.GameId, false);
                    await UpdateOverlayAsync(session);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (work.RequestId != null) await SendAsync(session, new { command = "reply", requestId = work.RequestId, fallback = true, text = "" });
                    var paused = ex.Message is "翻译已暂停。" or "该游戏翻译已暂停。";
                    lock (session.Gate) { if (session.Revision != work.Revision) continue; session.Failed++; session.LastError = paused ? "翻译已暂停，请启用全局和此游戏的翻译开关。" : "翻译失败，请查看日志与 API 配置，可点击重新翻译。"; session.DisplayStatus = session.LastError; }
                    runtime.Fail(session.GameId, ex.Message); logs.Write("error", "hook", "选定的游戏文本翻译失败。", session.GameId, details: ex.Message);
                    await UpdateOverlayAsync(session);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        Session? session; lock (_gate) session = _active;
        if (session != null) { session.Stop.Cancel(); try { SendAsync(session, new { command = "stop" }).GetAwaiter().GetResult(); if (!session.Worker.WaitForExit(1500)) session.Worker.Kill(true); } catch { } }
    }
    private static bool IsSystemThread(string name) => name is "MultiByteToWideChar" or "WideCharToMultiByte" or "lstrlenW" or "lstrlenA" or "lstrcpyW" or "lstrcpyA" or "lstrcpynW" or "lstrcpynA" or "GetGlyphOutlineW" or "GetGlyphOutlineA" or "TextOutW" or "TextOutA" or "ExtTextOutW" or "ExtTextOutA";
    private void VerifyNativeComponents()
    {
        var manifest = Path.Combine(Path.GetDirectoryName(_nativeDir)!, "native-manifest.json");
        UpdatePackage.RejectLinks(manifest); UpdatePackage.RejectLinks(WorkerPath);
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        var expected = new HashSet<string>(["LunaHost64.dll", "LunaHook32.dll", "LunaHook64.dll", "LunaSubprocess32.exe", "LunaSubprocess64.exe"], StringComparer.Ordinal);
        var names = json.RootElement.EnumerateArray().Select(i => i.GetProperty("name").GetString() ?? "").ToArray();
        if (names.Length != expected.Count || !expected.SetEquals(names)) throw new InvalidDataException("Hook 组件校验清单不完整。");
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            if (Path.GetFileName(name) != name) throw new InvalidDataException("Hook 组件校验清单无效。");
            var path = Path.Combine(_nativeDir, name); UpdatePackage.RejectLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length != item.GetProperty("size").GetInt64()) throw new InvalidDataException("Hook 组件缺失或大小不符，请重新安装完整更新包。");
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            if (!hash.Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Hook 原生组件校验失败，请重新安装完整更新包。");
        }
    }
}
