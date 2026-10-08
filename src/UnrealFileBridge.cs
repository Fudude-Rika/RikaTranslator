using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit;

/// <summary>Bounded local file IPC: Lua never receives an upstream API key or blocks the game thread on HTTP.</summary>
public sealed class UnrealFileBridge(DataStore store, TranslationService translations, EngineService engines, GameRuntimeDiagnostics runtime, LogService logs)
{
    public const int MaxRequestBytes = 24000;
    public static readonly Regex RequestName = new(@"^\d{8,12}-\d{1,10}-\d{1,10}\.req$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _readWarnings = [];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task RunAsync(CancellationToken ct)
    {
        var jobs = new List<(string GameId, Task Task)>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                jobs.RemoveAll(j => j.Task.IsCompleted);
                foreach (var game in store.Snapshot().Games.Where(g => g.Engine == "Unreal Engine" && g.AdapterStatus == "installed"))
                {
                    try
                    {
                        if (!UnrealLayout.IsRunning(game) || engines.AdapterStatus(game) != "installed") continue;
                        var mod = UnrealLayout.ModDirectory(game);
                        if (mod == null) continue;
                        var directory = Path.Combine(mod, "ipc", "requests");
                        if (!Directory.Exists(directory) || !UnrealLayout.SafePath(game.Directory, directory)) continue;
                        var room = 2 - jobs.Count(j => j.GameId == game.Id);
                        if (room <= 0) continue;
                        foreach (var path in Directory.EnumerateFiles(directory, "*.req").Take(64))
                        {
                            if (room == 0) break;
                            if (!RequestName.IsMatch(Path.GetFileName(path)) || !UnrealLayout.SafePath(game.Directory, path) || !_busy.TryAdd(path, 0)) continue;
                            jobs.Add((game.Id, ProcessAsync(game, mod, path, ct)));
                            room--;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        if (_readWarnings.Add(game.Id)) logs.Write("warning", "runtime", "Unreal 本地桥接暂时无法读取，检查游戏目录及组件状态。", game.Id);
                    }
                }
                await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(jobs.Select(j => j.Task)); }
    }

    private async Task ProcessAsync(GameRecord game, string mod, string path, CancellationToken ct)
    {
        var received = false;
        try
        {
            var response = Path.Combine(mod, "ipc", "responses", Path.GetFileNameWithoutExtension(path) + ".res");
            if (!UnrealLayout.SafePath(game.Directory, response)) return;
            string source;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length is <= 0 or > MaxRequestBytes) return;
                var bytes = new byte[(int)stream.Length];
                await stream.ReadExactlyAsync(bytes, ct);
                var request = Utf8.GetString(bytes);
                var separator = request.IndexOf('\n');
                if (separator < 0 || !TokenMatches(request[..separator], game.BridgeToken)) return;
                source = Decode(request[(separator + 1)..]);
                if (string.IsNullOrWhiteSpace(source) || source.Length > 2500) return;
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return; }
            File.Delete(path);
            var data = store.Snapshot();
            var current = data.Games.FirstOrDefault(g => g.Id == game.Id);
            if (current == null || !TokenMatches(current.BridgeToken, game.BridgeToken) || !current.Enabled || !data.Settings.Enabled)
            {
                WriteResponse(game, response, "E");
                return;
            }
            runtime.Receive(game.Id, source.Length); received = true;
            var result = await translations.TranslateAsync(new TranslateInput
            {
                GameId = game.Id, Text = source,
                From = string.IsNullOrEmpty(current.SourceLanguage) ? data.Settings.SourceLanguage : current.SourceLanguage,
                To = string.IsNullOrEmpty(current.TargetLanguage) ? data.Settings.TargetLanguage : current.TargetLanguage
            }, ct);
            runtime.Obtained(game.Id, result);
            // A shutdown/uninstall/pause must not recreate removed IPC files or send a late translation.
            current = store.Snapshot().Games.FirstOrDefault(g => g.Id == game.Id);
            if (current == null || current.AdapterStatus != "installed" || !current.Enabled || !store.Snapshot().Settings.Enabled || !TokenMatches(current.BridgeToken, game.BridgeToken) || !UnrealLayout.IsRunning(current)) return;
            if (WriteResponse(current, response, "O\n" + Convert.ToHexString(Encoding.UTF8.GetBytes(result.Text)))) runtime.Complete(game.Id, result, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (received) runtime.Fail(game.Id, logs.Sanitize(ex.Message));
            // The plugin only needs a failure flag; raw upstream errors never enter the game directory.
            try { WriteResponse(game, Path.Combine(mod, "ipc", "responses", Path.GetFileNameWithoutExtension(path) + ".res"), "E"); } catch { }
        }
        finally
        {
            // Invalid token/encoding/oversized requests must not occupy the bounded queue forever.
            try { if (File.Exists(path) && UnrealLayout.SafePath(game.Directory, path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _busy.TryRemove(path, out _);
        }
    }

    private static bool WriteResponse(GameRecord game, string path, string content)
    {
        if (!UnrealLayout.IsRunning(game) || !Directory.Exists(Path.GetDirectoryName(path)) || !UnrealLayout.SafePath(game.Directory, path) || !UnrealLayout.SafePath(game.Directory, path + ".tmp")) return false;
        File.WriteAllText(path + ".tmp", content, new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
        return true;
    }

    public static string Decode(string hex)
    {
        if (hex.Length is 0 or > 20000 || hex.Length % 2 != 0) throw new FormatException("Invalid IPC text.");
        return Utf8.GetString(Convert.FromHexString(hex));
    }

    private static bool TokenMatches(string supplied, string expected) => expected.Length >= 32 && supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(expected));
}
