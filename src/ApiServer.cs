using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameTranslateToolkit.Updates;

namespace GameTranslateToolkit;

public static class ApiServer
{
    public static void Map(WebApplication app, DataStore store, LogService logs, SecretVault vault, TranslationService translation, EngineService engines, LocalUpdateService updates, string uiToken, int port)
    {
        var gameLocks = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>();
        var runtime = new GameRuntimeDiagnostics(logs);
        var gameLogs = new GameLogService(store.DirectoryPath, logs);
        var verification = new GameVerificationService(GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory);
        var unrealBridge = new UnrealFileBridge(store, translation, engines, runtime, logs);
        var galgame = new GalgameService(store, translation, runtime, logs);
        app.Lifetime.ApplicationStopping.Register(galgame.Dispose);
        app.Lifetime.ApplicationStarted.Register(() => _ = unrealBridge.RunAsync(app.Lifetime.ApplicationStopping));
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var expectedOrigin = $"http://127.0.0.1:{port}";
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) || (request.Host.Host != "127.0.0.1" && request.Host.Host != "localhost") || request.Host.Port != port)
            { context.Response.StatusCode = 403; return; }
            var bridge = request.Path.StartsWithSegments("/bridge");
            var origin = request.Headers.Origin.ToString();
            if (!bridge && ((!string.IsNullOrEmpty(origin) && origin != expectedOrigin && origin != $"http://localhost:{port}") || request.Headers["Sec-Fetch-Site"] == "cross-site"))
            { context.Response.StatusCode = 403; return; }
            if (bridge && origin == "null")
            {
                context.Response.Headers.AccessControlAllowOrigin = "null";
                context.Response.Headers.AccessControlAllowMethods = "POST, GET, OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
                if (request.Method == "OPTIONS") { context.Response.StatusCode = 204; return; }
            }
            if (request.Path.StartsWithSegments("/api") && request.Path != "/api/session" && !SecureEquals(request.Headers["X-Toolkit-Token"].ToString(), uiToken))
            { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "页面会话已失效，请重新打开工具。" }); return; }
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            // UI resources change during local updates; an old script can hide new capabilities.
            context.Response.Headers.CacheControl = "no-store";
            var mutation = bridge || (request.Path.StartsWithSegments("/api") && request.Method != "GET" && !request.Path.StartsWithSegments("/api/updates"));
            bool entered = false;
            try { if (mutation) { updates.EnterMutation(); entered = true; } await next(); }
            catch (OperationCanceledException) { if (!context.Response.HasStarted) { context.Response.StatusCode = 408; await context.Response.WriteAsJsonAsync(new { error = "操作已取消或请求超时。" }); } }
            catch (Exception ex)
            {
                var error = logs.Sanitize(ex.Message);
                var bridgeGameId = context.Items["ToolkitBridgeGameId"] as string;
                var paused = ex is InvalidOperationException && (ex.Message is "翻译已暂停。" or "该游戏翻译已暂停。");
                if (paused) error = ex.Message == "该游戏翻译已暂停。"
                    ? "此游戏翻译已暂停，请在游戏详情启用此游戏翻译后重试。"
                    : "全局翻译已暂停，请在 AI 翻译页启用翻译后重试。";
                logs.Write(paused ? "warning" : "error", request.Path.StartsWithSegments("/api/updates") ? "update" : bridgeGameId == null ? "app" : "runtime", error, bridgeGameId);
                if (!context.Response.HasStarted) { context.Response.StatusCode = ex is KeyNotFoundException ? 404 : 400; await context.Response.WriteAsJsonAsync(new { error }); }
            }
            finally { if (entered) updates.ExitMutation(); }
        });
        app.UseDefaultFiles(); app.UseStaticFiles();
        app.MapGet("/api/session", () => new { token = uiToken });
        app.MapGet("/api/fonts", FontCatalog.Installed);
        app.MapGet("/api/updates/status", () => updates.Status());
        app.MapPost("/api/updates/pick", async () => new { path = await ToolkitWindow.PickUpdateAsync() });
        app.MapPost("/api/updates/inspect", async (JsonElement body, CancellationToken ct) => await updates.InspectAsync(Required(body, "path"), ct));
        app.MapPost("/api/updates/cancel", async (JsonElement body, CancellationToken ct) => { await updates.CancelAsync(Required(body, "id"), ct); return new { cancelled = true }; });
        async Task<IResult> ScheduleUpdate(JsonElement body, HttpContext context, bool restore)
        {
            await updates.ScheduleAsync(Required(body, "id"), Boolean(body, "confirmed") == true, translation.Stats, restore, context.RequestAborted);
            context.Response.OnCompleted(() =>
            {
                // Let the response reach WebView2 before closing the host window.
                _ = Task.Run(async () => { await Task.Delay(700); ToolkitWindow.CloseForUpdate(); app.Lifetime.StopApplication(); });
                return Task.CompletedTask;
            });
            return Results.Ok(new { scheduled = true });
        }
        app.MapPost("/api/updates/install", (JsonElement body, HttpContext context) => ScheduleUpdate(body, context, false));
        app.MapPost("/api/updates/restore", (JsonElement body, HttpContext context) => ScheduleUpdate(body, context, true));
        app.MapGet("/api/bootstrap", () => { var data = store.Snapshot(); var s = translation.Stats; return new { games = data.Games.Select(PublicGame), providers = data.Providers.Select(PublicProvider), settings = data.Settings, stats = new { s.Received, s.Queued, s.InFlight, s.Completed, s.Failed, s.CacheHits, s.Tokens, s.LastElapsedMs, translationCount = data.Translations.Count }, version = Program.Version }; });
        app.MapGet("/api/diagnostics", () => new { version = Program.Version, dataDirectory = store.DirectoryPath, logDirectory = logs.DirectoryPath, port, apiKeyStorage = "Windows DPAPI（当前 Windows 用户）", engines = new[] { new { name = "Unity Mono", status = "experimental", description = "BepInEx 5 + XUnity.AutoTranslator；具体游戏验证范围见详情" }, new { name = "Ren’Py 7/8", status = "experimental", description = "独立运行时插件，实际游戏效果待验证" }, new { name = "RPG Maker MV/MZ", status = "experimental", description = "JavaScript 插件；已验证 MZ 1.5.0 游戏副本的设置页和开场文字，其他游戏与控件待验证" }, new { name = "Unity IL2CPP", status = "experimental", description = "BepInEx 6 IL2CPP + XUnity IL2CPP；分别检查加载、取词和译文回传" }, new { name = "Unreal UE 5.7 Win64", status = "experimental", description = "UE4SS + UMG 实时翻译；已验证游戏和范围见详情" }, new { name = "Galgame / 文本 Hook", status = "experimental", description = "LunaHook 32/64 位取词 + 翻译浮窗；已验证 nine_new_chs.exe 开场对白英文内嵌，其他游戏逐一验证" }, new { name = "Unreal 其他版本 / 其他引擎", status = "detection-only", description = "目前仅识别，尚未开放对应运行时适配" } } });
        app.MapGet("/api/providers", () => store.Snapshot().Providers.Select(PublicProvider));
        app.MapPost("/api/providers", async (JsonElement body) =>
        {
            var provider = new ProviderRecord(); ApplyProvider(provider, body, vault, logs);
            await store.UpdateAsync(d => { d.Providers.Add(provider); d.Settings.ProviderId ??= provider.Id; });
            logs.Write("info", "config", "已添加翻译服务配置。"); return PublicProvider(provider);
        });
        app.MapPut("/api/providers/{id}", async (string id, JsonElement body) =>
        {
            ProviderRecord? provider = null;
            await store.UpdateAsync(d => { provider = d.Providers.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException("服务配置不存在。"); ApplyProvider(provider, body, vault, logs); provider.Revision++; });
            await galgame.ResetTranslationAsync();
            logs.Write("info", "config", "翻译服务配置已更新。"); return PublicProvider(provider!);
        });
        app.MapDelete("/api/providers/{id}", async (string id) =>
        {
            FindProvider(id);
            await store.UpdateAsync(d => { d.Providers.RemoveAll(p => p.Id == id); if (d.Settings.ProviderId == id) d.Settings.ProviderId = null; foreach (var g in d.Games.Where(g => g.ProviderId == id)) g.ProviderId = null; }); return Results.Ok(new { success = true });
        });
        app.MapPost("/api/providers/{id}/test", async (string id, JsonElement body, CancellationToken ct) => await translation.TestProviderAsync(id, Text(body, "text") ?? "Hello, welcome to the game.", ct));
        app.MapPost("/api/providers/{id}/models", async (string id, CancellationToken ct) => await translation.GetModelsAsync(id, ct));
        app.MapPost("/api/providers/models/preview", async (JsonElement body, CancellationToken ct) =>
        {
            // Work on a snapshot only: discovery must not save a draft or change the selected service.
            var providerId = Text(body, "providerId");
            var provider = string.IsNullOrWhiteSpace(providerId) ? new ProviderRecord() : FindProvider(providerId);
            ApplyProvider(provider, body, vault, logs);
            return await translation.GetModelsAsync(provider, ct);
        });
        app.MapGet("/api/settings", () => store.Snapshot().Settings);
        app.MapPut("/api/settings", async (JsonElement body) =>
        {
            AppSettings? result = null;
            await store.UpdateAsync(d => {
            var settings = d.Settings;
            if (body.TryGetProperty("providerId", out var p)) { settings.ProviderId = p.ValueKind == JsonValueKind.Null ? null : p.GetString(); if (!string.IsNullOrEmpty(settings.ProviderId)) FindProvider(settings.ProviderId); }
            settings.SourceLanguage = Text(body, "sourceLanguage") ?? settings.SourceLanguage;
            settings.TargetLanguage = Text(body, "targetLanguage") ?? settings.TargetLanguage;
            settings.Prompt = Text(body, "prompt") ?? settings.Prompt;
            settings.Concurrency = Math.Clamp(Integer(body, "concurrency") ?? settings.Concurrency, 1, 16);
            settings.Enabled = Boolean(body, "enabled") ?? settings.Enabled;
            var theme = Text(body, "theme"); if (theme is "dark" or "light" or "system") settings.Theme = theme;
            var accent = Text(body, "accent"); if (accent != null && Regex.IsMatch(accent, "^#[0-9a-fA-F]{6}$")) settings.Accent = accent;
            settings.Zoom = Math.Clamp(Integer(body, "zoom") ?? settings.Zoom, 75, 150);
            if (settings.Prompt.Length > 16000) throw new ArgumentException("提示词过长。");
            result = settings;
            });
            if (new[] { "providerId", "sourceLanguage", "targetLanguage", "prompt", "enabled" }.Any(key => body.TryGetProperty(key, out _))) await galgame.ResetTranslationAsync();
            return result;
        });
        app.MapGet("/api/games", () => store.Snapshot().Games.Select(PublicGame));
        app.MapPost("/api/games/pick", async () => new { path = await ToolkitWindow.PickExecutableAsync() });
        app.MapPost("/api/games/inspect", (JsonElement body) => engines.Inspect(Required(body, "path")));
        app.MapPost("/api/games", async (JsonElement body) =>
        {
            var detection = engines.Inspect(Required(body, "path"));
            if (store.Snapshot().Games.Any(g => g.ExecutablePath.Equals(detection.ExecutablePath, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("这个游戏已在游戏库中。");
            var game = new GameRecord { Name = Text(body, "name") ?? detection.Name, ExecutablePath = detection.ExecutablePath, Directory = detection.Directory, Engine = detection.Engine, EngineVersion = detection.EngineVersion, Architecture = detection.Architecture, Backend = detection.Backend, DetectionNotes = detection.DetectionNotes, Warnings = detection.Warnings, Capability = detection.Capability, BridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
            await store.UpdateAsync(d => { if (d.Games.Any(g => g.ExecutablePath.Equals(game.ExecutablePath, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("这个游戏已在游戏库中。"); d.Games.Add(game); }); logs.RegisterSecret(game.BridgeToken); logs.Write("info", "engine", "游戏已添加并完成引擎识别。", game.Id, details: new { game.Engine, game.Architecture, game.Backend }); return PublicGame(game);
        });
        app.MapGet("/api/games/{id}", (string id) => PublicGame(FindGame(id)));
        app.MapGet("/api/games/{id}/hook", (string id) => galgame.Snapshot(FindGame(id)));
        app.MapGet("/api/games/{id}/hook/processes", (string id) => GalgameService.Processes(FindGame(id)));
        app.MapPost("/api/games/{id}/hook/start", async (string id, JsonElement body, CancellationToken ct) => await galgame.StartAsync(FindGame(id), Integer(body, "pid") ?? 0, Boolean(body, "launch") ?? false, ct));
        app.MapPost("/api/games/{id}/hook/stop", async (string id) => { FindGame(id); await galgame.StopAsync(id); return new { stopped = true }; });
        app.MapPost("/api/games/{id}/hook/select", async (string id, JsonElement body) => { FindGame(id); await galgame.SelectAsync(id, Required(body, "threadId")); return galgame.Snapshot(FindGame(id)); });
        app.MapPost("/api/games/{id}/hook/insert", async (string id, JsonElement body) => { FindGame(id); await galgame.InsertAsync(id, Required(body, "code")); return new { sent = true }; });
        app.MapPost("/api/games/{id}/hook/remove-code", async (string id, JsonElement body) => { FindGame(id); await galgame.RemoveCodeAsync(id, Required(body, "code")); return new { removed = true }; });
        app.MapPost("/api/games/{id}/hook/retry", async (string id) => { FindGame(id); await galgame.ResetTranslationAsync(id); return galgame.Snapshot(FindGame(id)); });
        app.MapPost("/api/games/{id}/hook/overlay", async (string id, JsonElement body) => { FindGame(id); await galgame.ShowOverlayAsync(id, Boolean(body, "show") ?? true); return galgame.Snapshot(FindGame(id)); });
        app.MapPut("/api/games/{id}/hook/settings", async (string id, JsonElement body) =>
        {
            var game = FindGame(id);
            var requested = body.Deserialize<GalgameSettings>(DataStore.Json) ?? throw new ArgumentException("Hook 设置无效。");
            // Older clients omit this new field; retain the game's existing scale.
            if (!body.TryGetProperty("embedFontSizePercent", out _)) requested.EmbedFontSizePercent = game.Galgame.EmbedFontSizePercent;
            if (!body.TryGetProperty("fontColor", out _)) requested.FontColor = game.Galgame.FontColor;
            GalgameService.ValidateSettings(requested);
            if (requested.Embed)
            {
                var current = JsonSerializer.SerializeToElement(galgame.Snapshot(game), DataStore.Json);
                if (!current.TryGetProperty("embeddable", out var capability) || !capability.GetBoolean()) throw new InvalidOperationException("请先连接 Hook 并选择可内嵌的文本通道。");
            }
            // Channel identity and manual codes are controlled by their dedicated endpoints.
            requested.HookCode = game.Galgame.HookCode; requested.HookName = game.Galgame.HookName;
            requested.Context = game.Galgame.Context; requested.Context2 = game.Galgame.Context2; requested.ManualCodes = game.Galgame.ManualCodes;
            await store.UpdateAsync(d => d.Games.First(g => g.Id == id).Galgame = requested);
            try { if (!requested.Enabled) await galgame.StopAsync(id); else await galgame.ConfigureAsync(id); await galgame.RefreshOverlaySettingsAsync(id); }
            catch { await store.UpdateAsync(d => d.Games.First(g => g.Id == id).Galgame = game.Galgame); throw; }
            logs.Write("info", "hook", "Galgame 字体、编码与显示设置已保存。", id, details: new { requested.FontFamily, requested.FontSize, requested.FontColor, requested.EmbedFontSizePercent });
            return galgame.Snapshot(FindGame(id));
        });
        app.MapGet("/api/games/{id}/verification", (string id) => verification.Get(FindGame(id)));
        GameDiagnosticReport DiagnosticReport(string id, HttpRequest request)
        {
            var game = FindGame(id);
            return gameLogs.Get(game, runtime.Get(game, engines.AdapterStatus(game), store.Snapshot().Settings.Enabled), Program.Version, request.Query["source"], request.Query["level"], request.Query["q"], request.Query["scope"]);
        }
        app.MapGet("/api/games/{id}/diagnostics", DiagnosticReport);
        app.MapGet("/api/games/{id}/diagnostics/export", (string id, HttpRequest request) => Results.File(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(DiagnosticReport(id, request), DataStore.Json)), "application/json", $"游戏诊断-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
        app.MapPost("/api/games/{id}/player-log/pick", async (string id) => { FindGame(id); return new { path = await ToolkitWindow.PickPlayerLogAsync() }; });
        app.MapPut("/api/games/{id}/player-log", async (string id, JsonElement body) =>
        {
            var game = FindGame(id);
            if (game.Engine != "Unity") throw new ArgumentException("此入口用于 Unity Player.log。");
            var input = Text(body, "path");
            var path = string.IsNullOrWhiteSpace(input) ? null : GameLogService.ValidatePlayerPath(input);
            await store.UpdateAsync(d => d.Games.First(g => g.Id == id).PlayerLogPath = path);
            logs.Write("info", "diagnostics", path == null ? "已取消手动 Player.log 关联。" : "已手动关联此游戏的 Player.log。历史内容不作为本次启动证据。", id);
            return new { saved = true };
        });
        app.MapGet("/api/games/{id}/runtime", (string id) =>
        {
            var game = FindGame(id);
            return runtime.Get(game, engines.AdapterStatus(game), store.Snapshot().Settings.Enabled);
        });
        app.MapGet("/api/games/{id}/font", (string id) =>
        {
            var game = FindGame(id);
            using var fonts = new System.Drawing.Text.InstalledFontCollection();
            return new { supported = FontAssets.Supported(game), game.Engine, settings = game.Font, migration = game.Engine == "Unity" ? engines.MigrationPreview(InstallationGame(id)) : null, installed = engines.AdapterStatus(game) == "installed", systemFonts = game.Engine == "Unity" ? fonts.Families.Select(f => f.Name).Distinct().Order().ToArray() : [] };
        });
        app.MapGet("/api/games/{id}/migration", (string id) => engines.MigrationPreview(InstallationGame(id)));
        app.MapPost("/api/games/{id}/font/inspect", (string id, JsonElement body) =>
        {
            var game = FindGame(id);
            var settings = new GameFontSettings { Mode = "custom", Family = Text(body, "family") ?? "Microsoft YaHei", TmpMode = "fallback" };
            return engines.PrepareFont(game, settings, Required(body, "path")).TmpReport;
        });
        app.MapPost("/api/games/{id}/legacy-cache/pick", async () => new { path = await ToolkitWindow.PickCacheDirectoryAsync() });
        app.MapPut("/api/games/{id}/legacy-cache", async (string id, JsonElement body, CancellationToken ct) =>
        {
            var gate = gameLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(ct);
            try
            {
                var game = InstallationGame(id);
                if (engines.AdapterStatus(game) != "not-installed") throw new InvalidOperationException("请先卸载本工具适配，再选择旧缓存目录；安装时会复制并备份。");
                var path = Text(body, "path");
                object? preview = null;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (Boolean(body, "confirmedLanguage") != true) throw new InvalidOperationException("请先确认所选缓存与当前游戏目标语言一致。");
                    preview = engines.CheckLegacyCache(game, path);
                    path = Path.GetFullPath(path.Trim().Trim('"'));
                }
                else path = null;
                await store.UpdateAsync(d => d.Games.First(g => g.Id == id).LegacyCachePath = path);
                logs.Write("info", "migration", path == null ? "已取消旧缓存目录选择。" : "已选择旧译文缓存目录，等待安装时复制。原缓存保持原样。", id);
                return new { saved = true, preview };
            }
            finally { gate.Release(); }
        });
        app.MapPost("/api/games/{id}/font/pick", async (string id) =>
        {
            var game = FindGame(id);
            if (!FontAssets.Supported(game) || game.Engine == "Unreal Engine") throw new InvalidOperationException("此引擎不支持直接选择字体文件。");
            return new { path = await ToolkitWindow.PickFontAsync(game.Engine == "Unity") };
        });
        app.MapPut("/api/games/{id}/font", async (string id, JsonElement body, CancellationToken ct) =>
        {
            var gate = gameLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(ct);
            try
            {
                var game = FindGame(id);
                var oldFont = game.Font;
                if (!body.TryGetProperty("settings", out var value) || value.ValueKind != JsonValueKind.Object) throw new ArgumentException("缺少字体设置。");
                var requested = value.Deserialize<GameFontSettings>(DataStore.Json) ?? throw new ArgumentException("字体设置无效。");
                game.Font = engines.PrepareFont(game, requested, Text(body, "filePath"));
                var applied = await engines.ApplyFontAsync(game, ct);
                GameRecord? saved = null;
                try { await store.UpdateAsync(d => { saved = d.Games.FirstOrDefault(g => g.Id == id) ?? throw new KeyNotFoundException("游戏记录不存在。"); saved.Font = game.Font; }); }
                catch { if (applied) { game.Font = oldFont; await engines.ApplyFontAsync(game, CancellationToken.None); } throw; }
                logs.Write("info", "font", applied ? "字体设置已保存并应用，重启游戏后生效。" : "字体设置已保存，安装适配时生效。", id);
                return new { game = PublicGame(saved!), applied };
            }
            finally { gate.Release(); }
        });
        app.MapPut("/api/games/{id}", async (string id, JsonElement body) =>
        {
            GameRecord? result = null;
            await store.UpdateAsync(d => {
            var game = d.Games.FirstOrDefault(g => g.Id == id) ?? throw new KeyNotFoundException("游戏记录不存在。"); game.Name = Text(body, "name") ?? game.Name;
            if (body.TryGetProperty("providerId", out var p)) { game.ProviderId = p.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(p.GetString()) ? null : p.GetString(); if (game.ProviderId != null) FindProvider(game.ProviderId); }
            game.SourceLanguage = Text(body, "sourceLanguage") ?? game.SourceLanguage;
            game.TargetLanguage = Text(body, "targetLanguage") ?? game.TargetLanguage;
            game.Context = Text(body, "context") ?? game.Context;
            game.Enabled = Boolean(body, "enabled") ?? game.Enabled;
            if (game.Name.Length > 200 || game.Context.Length > 16000) throw new ArgumentException("游戏名称或背景说明过长。");
            result = game;
            });
            if (new[] { "providerId", "sourceLanguage", "targetLanguage", "context", "enabled" }.Any(key => body.TryGetProperty(key, out _))) await galgame.ResetTranslationAsync(id);
            return PublicGame(result!);
        });
        app.MapDelete("/api/games/{id}", async (string id) =>
        {
            var gate = gameLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync();
            try {
            var game = FindGame(id);
            if (game.AdapterStatus == "installed" || engines.AdapterStatus(game) != "not-installed") throw new InvalidOperationException("请先卸载或恢复本工具适配，再从游戏库移除，避免留下无法管理的组件。");
            await galgame.StopAsync(id);
            await store.UpdateAsync(d => d.Games.RemoveAll(g => g.Id == id)); return Results.Ok(new { success = true });
            } finally { gate.Release(); }
        });
        app.MapPost("/api/games/{id}/launch", async (string id, CancellationToken ct) =>
        {
            var game = FindGame(id);
            if (!File.Exists(game.ExecutablePath) || !game.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("游戏启动程序不存在。");
            if (game.Galgame.Enabled) { await galgame.StartAsync(game, 0, true, ct); return Results.Ok(new { success = true, mode = "hook" }); }
            if (runtime.Get(game, engines.AdapterStatus(game), store.Snapshot().Settings.Enabled).GameRunning) throw new InvalidOperationException("游戏正在运行，请先退出游戏后再开始新的启动诊断。");
            runtime.BeginLaunch(game);
            var playerPath = gameLogs.BeginLaunch(game);
            var executable = game.Engine == "Unreal Engine" ? UnrealLayout.ShippingExecutable(game.Directory, game.ExecutablePath) ?? throw new InvalidOperationException("无法定位唯一的 Unreal Shipping 游戏程序。") : game.ExecutablePath;
            var start = new ProcessStartInfo { FileName = executable, WorkingDirectory = game.Engine == "Unreal Engine" ? Path.GetDirectoryName(executable)! : game.Directory, UseShellExecute = true };
            if (playerPath != null) { start.ArgumentList.Add("-logFile"); start.ArgumentList.Add(playerPath); }
            Process.Start(start); logs.Write("info", "engine", "已请求启动游戏。", id, details: new { playerLog = playerPath }); return Results.Ok(new { success = true });
        });
        app.MapGet("/api/games/{id}/install-plan", (string id, HttpRequest request) =>
        {
            var game = InstallationGame(id);
            if (request.Query["fontMode"].FirstOrDefault() is { Length: > 0 } mode) { game.Font.Mode = mode; game.Font = engines.PrepareFont(game, game.Font, null); }
            return engines.Plan(game);
        });
        app.MapPost("/api/games/{id}/install", async (string id, HttpRequest request, CancellationToken ct) =>
        {
            string? fontMode = null;
            if (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
            {
                var body = await request.ReadFromJsonAsync<JsonElement>(ct);
                if (body.ValueKind != JsonValueKind.Object) throw new ArgumentException("安装选项无效。");
                fontMode = Text(body, "fontMode");
            }
            return await Adapt(id, true, ct, fontMode);
        });
        app.MapPost("/api/games/{id}/uninstall", async (string id, CancellationToken ct) => await Adapt(id, false, ct));
        app.MapPost("/api/games/{id}/recheck", async (string id) =>
        {
            var game = FindGame(id); var detection = engines.Inspect(game.ExecutablePath);
            GameRecord? result = null;
            var adapterStatus = engines.AdapterStatus(game);
            await store.UpdateAsync(d => { var current = d.Games.FirstOrDefault(g => g.Id == id) ?? throw new KeyNotFoundException("游戏记录不存在。"); current.Engine = detection.Engine; current.EngineVersion = detection.EngineVersion; current.Architecture = detection.Architecture; current.Backend = detection.Backend; current.DetectionNotes = detection.DetectionNotes; current.Warnings = detection.Warnings; current.Capability = detection.Capability; current.AdapterStatus = adapterStatus; result = current; }); return PublicGame(result!);
        });
        app.MapPost("/api/translate", async (TranslateInput input, CancellationToken ct) => await translation.TranslateAsync(input, ct));
        app.MapGet("/api/stats", () => translation.Stats);
        app.MapGet("/api/translations", (HttpRequest request) =>
        {
            var gameId = request.Query["gameId"].ToString(); var q = request.Query["q"].ToString();
            var rows = store.Snapshot().Translations.Where(t => (string.IsNullOrEmpty(gameId) || t.GameId == gameId) && (string.IsNullOrEmpty(q) || t.Source.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Translation.Contains(q, StringComparison.OrdinalIgnoreCase))).OrderByDescending(t => t.UpdatedAt).ToList();
            int.TryParse(request.Query["page"], out var page); page = Math.Max(page, 1);
            return new { items = rows.Skip((page - 1) * 50).Take(50), total = rows.Count, page, pageSize = 50 };
        });
        app.MapPut("/api/translations/{id}", async (string id, JsonElement body) =>
        {
            var text = Required(body, "translation"); if (text.Length > 60000) throw new ArgumentException("译文过长。");
            TranslationRecord? result = null;
            await store.UpdateAsync(d => { result = d.Translations.FirstOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException("译文不存在。"); result.Translation = TranslationService.ValidateManualTranslation(result.Source, text); result.Manual = true; result.UpdatedAt = DateTimeOffset.UtcNow; }); return result;
        });
        app.MapDelete("/api/translations/{id}", async (string id) => { await store.UpdateAsync(d => d.Translations.RemoveAll(t => t.Id == id)); return Results.Ok(new { success = true }); });
        app.MapGet("/api/glossary", (HttpRequest request) => store.Snapshot().Glossary.Where(g => string.IsNullOrEmpty(request.Query["gameId"]) || g.GameId == request.Query["gameId"].ToString()));
        app.MapPost("/api/glossary", async (JsonElement body) => { var entry = new GlossaryEntry(); ApplyTerm(entry, body); if (!string.IsNullOrEmpty(entry.GameId)) FindGame(entry.GameId); await store.UpdateAsync(d => d.Glossary.Add(entry)); return entry; });
        app.MapPut("/api/glossary/{id}", async (string id, JsonElement body) =>
        {
            GlossaryEntry? entry = null;
            await store.UpdateAsync(d => { entry = d.Glossary.FirstOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException("词条不存在。"); ApplyTerm(entry, body); if (!string.IsNullOrEmpty(entry.GameId) && !d.Games.Any(g => g.Id == entry.GameId)) throw new KeyNotFoundException("游戏记录不存在。"); }); return entry;
        });
        app.MapDelete("/api/glossary/{id}", async (string id) => { await store.UpdateAsync(d => d.Glossary.RemoveAll(t => t.Id == id)); return Results.Ok(new { success = true }); });
        app.MapGet("/api/logs", (HttpRequest r) => logs.Query(r.Query["gameId"], r.Query["level"], r.Query["q"], r.Query["module"]));
        app.MapGet("/api/logs/export", (HttpRequest r) => Results.File(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(logs.Query(r.Query["gameId"], r.Query["level"], r.Query["q"], r.Query["module"]), DataStore.Json)), "application/json", $"日志-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
        app.MapPost("/api/data/export", () => { var d = store.Snapshot(); return Results.File(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = Program.Version, exportedAt = DateTimeOffset.Now, games = d.Games.Select(PublicGame), providers = d.Providers.Select(PublicProvider), settings = d.Settings, translations = d.Translations, glossary = d.Glossary }, DataStore.Json)), "application/json", $"翻译数据-{DateTime.Now:yyyyMMdd-HHmmss}.json"); });
        app.MapPost("/api/folders/open", (JsonElement body) =>
        {
            var path = Required(body, "kind") switch { "data" => store.DirectoryPath, "logs" => logs.DirectoryPath, "game" => FindGame(Required(body, "gameId")).Directory, _ => throw new ArgumentException("不支持的目录类型。") };
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); return Results.Ok(new { success = true });
        });
        app.MapPost("/bridge/translate", async (JsonElement body, HttpContext context, CancellationToken ct) =>
        {
            var gameId = Required(body, "gameId");
            var game = VerifyBridge(gameId, Required(body, "token"), context);
            return await BridgeTranslate(game, Required(body, "text"), context, ct);
        });
        app.MapGet("/bridge/translate/{gameId}/{token}", async (string gameId, string token, HttpRequest r, CancellationToken ct) =>
        {
            var game = VerifyBridge(gameId, token, r.HttpContext); var result = await BridgeTranslate(game, r.Query["text"].ToString(), r.HttpContext, ct); return Results.Text(result.Text, "text/plain", Encoding.UTF8);
        });
        app.MapGet("/bridge/translate", async (HttpRequest r, CancellationToken ct) =>
        {
            var id = r.Query["gameId"].ToString(); var game = VerifyBridge(id, r.Query["token"].ToString(), r.HttpContext); var result = await BridgeTranslate(game, r.Query["text"].ToString(), r.HttpContext, ct); return Results.Text(result.Text, "text/plain", Encoding.UTF8);
        });
        app.MapPost("/bridge/status", (JsonElement body, HttpContext context) =>
        {
            var game = VerifyBridge(Required(body, "gameId"), Required(body, "token"), context);
            if (game.Engine is not ("RPG Maker MV" or "RPG Maker MZ")) throw new ArgumentException("此状态入口用于 RPG Maker 插件。");
            var session = Required(body, "session");
            if (!Regex.IsMatch(session, "^[a-zA-Z0-9-]{1,64}$")) throw new ArgumentException("运行状态会话无效。");
            var fontStatus = Required(body, "fontStatus");
            if (fontStatus is not ("loaded" or "unavailable" or "original" or "unknown")) throw new ArgumentException("字体状态无效。");
            var applied = body.TryGetProperty("applied", out var value) && value.TryGetInt64(out var n) ? n : 0;
            runtime.ReportRpgStatus(game.Id, session, applied, fontStatus);
            return Results.Ok(new { received = true });
        });
        app.MapFallbackToFile("index.html");

        GameRecord FindGame(string id) => store.Snapshot().Games.FirstOrDefault(g => g.Id == id) ?? throw new KeyNotFoundException("游戏记录不存在。");
        GameRecord InstallationGame(string id)
        {
            // Snapshot is detached; inheritance is resolved for this installation only.
            // Persisted nullable game settings continue to inherit future global changes.
            var data = store.Snapshot();
            var game = data.Games.FirstOrDefault(g => g.Id == id) ?? throw new KeyNotFoundException("游戏记录不存在。");
            if (string.IsNullOrEmpty(game.SourceLanguage)) game.SourceLanguage = data.Settings.SourceLanguage;
            if (string.IsNullOrEmpty(game.TargetLanguage)) game.TargetLanguage = data.Settings.TargetLanguage;
            return game;
        }
        async Task<TranslationResult> BridgeTranslate(GameRecord game, string text, HttpContext context, CancellationToken ct)
        {
            var settings = store.Snapshot().Settings;
            runtime.Receive(game.Id, text.Length);
            try
            {
                if (!game.Enabled) throw new InvalidOperationException("该游戏翻译已暂停。");
                if (!settings.Enabled) throw new InvalidOperationException("翻译已暂停。");
                var result = await translation.TranslateAsync(new TranslateInput { GameId = game.Id, Text = text, From = string.IsNullOrEmpty(game.SourceLanguage) ? settings.SourceLanguage : game.SourceLanguage, To = string.IsNullOrEmpty(game.TargetLanguage) ? settings.TargetLanguage : game.TargetLanguage }, ct);
                runtime.Obtained(game.Id, result);
                context.Response.OnCompleted(() => { if (!context.RequestAborted.IsCancellationRequested && context.Response.StatusCode < 400) runtime.Complete(game.Id, result, true); return Task.CompletedTask; });
                return result;
            }
            catch (Exception ex) { runtime.Fail(game.Id, logs.Sanitize(ex.Message)); throw; }
        }
        ProviderRecord FindProvider(string id) => store.Snapshot().Providers.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException("服务配置不存在。");
        GameRecord VerifyBridge(string id, string supplied, HttpContext context)
        {
            var game = FindGame(id);
            if (!SecureEquals(supplied, game.BridgeToken)) throw new InvalidOperationException("游戏适配连接凭据无效。");
            context.Items["ToolkitBridgeGameId"] = game.Id;
            return game;
        }
        async Task<object> Adapt(string id, bool install, CancellationToken ct, string? fontMode = null)
        {
            var gate = gameLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(ct);
            try
            {
                var game = install ? InstallationGame(id) : FindGame(id); logs.RegisterSecret(game.BridgeToken);
                if (install && fontMode != null)
                {
                    if (game.Engine != "Unity") throw new ArgumentException("此安装选项仅用于 Unity 字体迁移。");
                    game.Font.Mode = fontMode;
                    game.Font = engines.PrepareFont(game, game.Font, null);
                }
                if (install && string.IsNullOrEmpty(game.BridgeToken)) game.BridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                GameRecord result;
                try { result = install ? await engines.InstallAsync(game, port, game.BridgeToken, ct) : await engines.UninstallAsync(game, ct); }
                catch
                {
                    try { var failedStatus = engines.AdapterStatus(game); await store.UpdateAsync(d => { var current = d.Games.FirstOrDefault(g => g.Id == id); if (current != null) current.AdapterStatus = failedStatus; }); } catch { }
                    throw;
                }
                GameRecord? merged = null;
                await store.UpdateAsync(d => { merged = d.Games.FirstOrDefault(g => g.Id == id) ?? throw new KeyNotFoundException("游戏记录不存在。"); merged.AdapterStatus = result.AdapterStatus; merged.Capability = result.Capability; merged.BridgeToken = result.BridgeToken; if (fontMode != null) merged.Font = result.Font; }); runtime.Reset(id); return PublicGame(merged!);
            }
            finally { gate.Release(); }
        }
    }
    private static bool SecureEquals(string a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    private static object PublicGame(GameRecord g) => new { g.Id, g.Name, g.ExecutablePath, g.Directory, g.Engine, g.EngineVersion, g.Architecture, g.Backend, g.DetectionNotes, g.Warnings, g.AdapterStatus, g.Capability, g.ProviderId, g.SourceLanguage, g.TargetLanguage, g.Context, g.Font, g.Galgame, g.LegacyCachePath, g.PlayerLogPath, g.Enabled, g.CreatedAt };
    private static object PublicProvider(ProviderRecord p) => new { p.Id, p.Name, p.BaseUrl, p.Model, p.HasKey, p.Stream, p.Temperature, p.MaxTokens, p.TimeoutSeconds, extraHeaders = p.ExtraHeaders.ToDictionary(e => e.Key, e => ""), p.ExtraBody, p.Revision };
    private static void ApplyProvider(ProviderRecord p, JsonElement body, SecretVault vault, LogService logs)
    {
        var oldAddress = p.BaseUrl;
        p.Name = Text(body, "name") ?? p.Name; p.BaseUrl = (Text(body, "baseUrl") ?? p.BaseUrl).Trim(); p.Model = (Text(body, "model") ?? p.Model).Trim();
        if (!Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment)) throw new ArgumentException("接口地址必须是 HTTP/HTTPS 地址，不包含用户名、密码、查询参数或片段。");
        if (p.Name.Length > 100 || p.Model.Length > 200 || p.BaseUrl.Length > 2000) throw new ArgumentException("服务名称、地址或模型过长。");
        // Additional authentication belongs to its original destination too.
        if (!oldAddress.Equals(p.BaseUrl, StringComparison.Ordinal)) p.ExtraHeaders = [];
        var key = Text(body, "apiKey");
        if (Boolean(body, "clearKey") == true) p.KeyEncrypted = null;
        else if (!string.IsNullOrEmpty(key)) { p.KeyEncrypted = vault.Protect(key); logs.RegisterSecret(key); }
        else if (oldAddress != p.BaseUrl && p.HasKey) throw new ArgumentException("更改服务地址时，请重新填写该地址的 Key，或明确清除旧 Key。");
        p.Stream = Boolean(body, "stream") ?? p.Stream;
        if (body.TryGetProperty("temperature", out var temperature)) p.Temperature = temperature.ValueKind == JsonValueKind.Null ? null : temperature.GetDouble();
        if (p.Temperature is < 0 or > 2) throw new ArgumentException("temperature 应在 0–2 之间，或留空不发送。");
        if (body.TryGetProperty("maxTokens", out var max)) p.MaxTokens = max.ValueKind == JsonValueKind.Null ? null : max.GetInt32();
        if (p.MaxTokens is <= 0) throw new ArgumentException("输出长度应为正整数，或留空不发送。");
        p.TimeoutSeconds = Math.Clamp(Integer(body, "timeoutSeconds") ?? p.TimeoutSeconds, 5, 180);
        if (body.TryGetProperty("extraHeaders", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            var incoming = JsonSerializer.Deserialize<Dictionary<string, string>>(headers.GetRawText()) ?? [];
            foreach (var name in incoming.Keys)
                if (!Regex.IsMatch(name, "^[A-Za-z0-9-]+$") || name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("额外请求头名称不合法。");
            p.ExtraHeaders = incoming.ToDictionary(k => k.Key, k => string.IsNullOrEmpty(k.Value) && p.ExtraHeaders.TryGetValue(k.Key, out var old) ? old : k.Value);
            foreach (var value in p.ExtraHeaders.Values) { if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("请求头不能包含换行。"); logs.RegisterSecret(value); }
        }
        if (body.TryGetProperty("extraBody", out var extra))
        {
            if (extra.ValueKind != JsonValueKind.Object) throw new ArgumentException("额外参数应为 JSON 对象。");
            p.ExtraBody = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extra.GetRawText()) ?? [];
            if (p.ExtraBody.Keys.Any(k => k is "model" or "messages" or "stream")) throw new ArgumentException("额外参数不能覆盖 model、messages 或 stream。");
        }
    }
    private static void ApplyTerm(GlossaryEntry term, JsonElement body)
    {
        if (body.TryGetProperty("gameId", out var gameId)) term.GameId = gameId.ValueKind == JsonValueKind.Null ? "" : gameId.GetString() ?? "";
        term.Source = Text(body, "source") ?? term.Source; term.Target = Text(body, "target") ?? term.Target; term.Note = Text(body, "note") ?? term.Note;
        if (string.IsNullOrWhiteSpace(term.Source) || string.IsNullOrWhiteSpace(term.Target)) throw new ArgumentException("原词和译法不能为空。");
        if (term.Source.Length > 2000 || term.Target.Length > 2000 || term.Note.Length > 4000) throw new ArgumentException("词条内容过长。");
    }
    public static string? Text(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    public static string Required(JsonElement body, string name) => Text(body, name) is { Length: > 0 } text ? text : throw new ArgumentException($"缺少 {name}。");
    private static int? Integer(JsonElement body, string name) => body.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
    private static bool? Boolean(JsonElement body, string name) => body.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;
}
