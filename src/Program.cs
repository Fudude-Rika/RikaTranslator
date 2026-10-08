using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using GameTranslateToolkit.Updates;

namespace GameTranslateToolkit;

internal static class Program
{
    public static string Version => typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.7.3";
    public const string DisplayName = "Rika Translator（梨花翻译工具）";
    [STAThread]
    static void Main(string[] args)
    {
        var serverOnly = args.Contains("--server");
        string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var healthFile = Option("--update-health-file");
        var dataDirectory = Path.GetFullPath(Option("--data-dir") ?? Path.Combine(GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory, "data"));
        try
        {
            if (Option("--inspect-tmp-input") is { } fontInput)
            {
                Updates.UpdatePackage.RejectLinks(fontInput);
                if (new FileInfo(fontInput).Length > 64L * 1024 * 1024) throw new InvalidDataException("字体包超过 64 MB。");
                var report = TmpFontInspector.Inspect(File.ReadAllBytes(fontInput), Option("--unity-version") ?? "");
                Updates.UpdatePackage.WriteJson(Option("--inspect-tmp-result") ?? throw new InvalidDataException("字体检查缺少输出路径。"), report);
                return;
            }
            var port = int.TryParse(Option("--port"), out var customPort) ? customPort : 17865;
            if (healthFile != null)
            {
                VerifyUpdateAsync(dataDirectory, healthFile, Option("--expected-version") ?? "").GetAwaiter().GetResult();
                return;
            }
            if (port < 1024 || port > 65535) throw new ArgumentException("端口应为 1024–65535。");
            var vault = new SecretVault();
            var store = new DataStore(dataDirectory, vault);
            var logs = new LogService(dataDirectory);
            foreach (var provider in store.Snapshot().Providers)
            {
                logs.RegisterSecret(vault.Unprotect(provider.KeyEncrypted));
                foreach (var header in provider.ExtraHeaders.Values) logs.RegisterSecret(header);
            }
            var translations = new TranslationService(store, logs, vault);
            var engines = new EngineService(dataDirectory, logs);
            var updates = new LocalUpdateService(GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory, dataDirectory, logs, serverOnly, port);
            foreach (var game in store.Snapshot().Games)
            {
                logs.RegisterSecret(game.BridgeToken);
                var status = engines.AdapterStatus(game);
                if (game.AdapterStatus != status) store.UpdateAsync(d => d.Games.First(g => g.Id == game.Id).AdapterStatus = status).GetAwaiter().GetResult();
                if (game.Capability == "detection-only" || game.Engine == "Unknown")
                {
                    try
                    {
                        var detected = engines.Inspect(game.ExecutablePath);
                        if (detected.Engine == game.Engine && detected.Backend == game.Backend || game.Engine is "Godot" or "Unknown" && EngineService.IsGalgameEngine(detected.Engine))
                            store.UpdateAsync(d => {
                                var current = d.Games.First(g => g.Id == game.Id);
                                current.Engine = detected.Engine; current.Backend = detected.Backend;
                                current.EngineVersion = detected.EngineVersion;
                                current.Architecture = detected.Architecture;
                                current.DetectionNotes = detected.DetectionNotes;
                                current.Warnings = detected.Warnings;
                                current.Capability = detected.Capability;
                            }).GetAwaiter().GetResult();
                    }
                    catch (Exception ex) { logs.Write("warning", "engine", "旧游戏适配能力重新检测失败，可在游戏详情重新检测。", game.Id, details: ex.Message); }
                }
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory, WebRootPath = Path.Combine(GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory, "wwwroot") });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, port); k.Limits.MaxRequestBodySize = 2 * 1024 * 1024; k.Limits.MaxRequestLineSize = 32 * 1024; });
            builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase; });
            var app = builder.Build();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var url = $"http://127.0.0.1:{port}";
            ApiServer.Map(app, store, logs, vault, translations, engines, updates, token, port);
            app.StartAsync().GetAwaiter().GetResult();
            logs.Write("info", "app", $"{DisplayName} {Version} 已启动，仅监听本机。", details: new { port });
            if (serverOnly) app.WaitForShutdownAsync().GetAwaiter().GetResult();
            else
            {
                ApplicationConfiguration.Initialize();
                using var shell = new ToolkitWindow(url, dataDirectory, logs);
                Application.Run(shell);
                app.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            logs.Write("info", "app", "工具已退出。");
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var safeMessage = ex is InvalidDataException ? ex.Message : ex is IOException || ex is System.Net.Sockets.SocketException ? "无法启动本地服务或写入数据目录。请检查目录权限，确认工具没有重复打开，或检查端口是否被占用。" : ex.Message;
            try { Directory.CreateDirectory(dataDirectory); File.WriteAllText(Path.Combine(dataDirectory, "startup-error.txt"), $"{DateTimeOffset.Now:O}\n{safeMessage}"); } catch { }
            if (!serverOnly && healthFile == null) MessageBox.Show(safeMessage, "Rika Translator 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }

    private static async Task VerifyUpdateAsync(string data, string marker, string expectedVersion)
    {
        _ = typeof(AssetsTools.NET.Extra.AssetsManager).Assembly;
        if (Version != expectedVersion) throw new InvalidDataException("新版程序版本与更新包声明不一致。");
        foreach (var file in UpdatePackage.RequiredFor(UpdatePackage.DetectLayout(UpdatePackage.ApplicationDirectory)))
            if (!File.Exists(UpdatePackage.Target(GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory, file)) || new FileInfo(UpdatePackage.Target(GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory, file)).Length == 0) throw new InvalidDataException("新版缺少启动文件：" + file);
        // Read a copy of settings without migrating games, starting bridges or writing to real user data.
        var store = new DataStore(data, new SecretVault());
        _ = store.Snapshot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapGet("/health", () => new { version = Version });
        await app.StartAsync();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var response = await client.GetAsync(app.Urls.Single() + "/health");
        response.EnsureSuccessStatusCode();
        await app.StopAsync();
        UpdatePackage.WriteJson(Path.GetFullPath(marker), new { version = Version });
    }
}
