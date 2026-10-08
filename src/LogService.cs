using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit;

public sealed class LogService
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];
    private readonly HashSet<string> _secrets = [];
    private string? _activeFile;
    public string DirectoryPath { get; }
    public LogService(string dataDirectory)
    {
        DirectoryPath = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(DirectoryPath);
        foreach (var path in Directory.GetFiles(DirectoryPath, "*.jsonl").OrderDescending().Take(3).Reverse())
        {
            try
            {
                foreach (var line in File.ReadLines(path).TakeLast(1500))
                    if (JsonSerializer.Deserialize<LogEntry>(line, DataStore.Json) is { } entry) _entries.Add(entry);
            }
            catch { /* Preserve a partially written old log for manual diagnosis. */ }
        }
        Trim();
    }
    public void RegisterSecret(string? value) { if (!string.IsNullOrEmpty(value)) lock (_gate) _secrets.Add(value); }
    public string Sanitize(string text)
    {
        lock (_gate)
        {
            foreach (var secret in _secrets.OrderByDescending(s => s.Length)) text = text.Replace(secret, "[已脱敏]", StringComparison.Ordinal);
            text = Regex.Replace(text, @"(?i)(Bearer\s+)[^\s\""<>]+", "$1[已脱敏]");
            text = Regex.Replace(text, @"(?i)((?:api[_ -]?key|token|secret|password|authorization)\s*[=:]\s*)[^\s&;,\""<>]+", "$1[已脱敏]");
            text = Regex.Replace(text, "(?i)((?:api[_ -]?key|access[_-]?token|token|secret|password|authorization)[\\\"']?\\s*[=:]\\s*)([\\\"'])(.*?)\\2", "$1$2[已脱敏]$2", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            text = Regex.Replace(text, @"(?i)\bsk-[a-z0-9_-]{12,}\b", "[已脱敏]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            text = Regex.Replace(text, @"(?i)(/bridge/translate/[a-f0-9]{32}/)[a-f0-9]{64}\b", "$1[已脱敏]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            text = Regex.Replace(text, @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[已脱敏]@");
            return text;
        }
    }
    public void Write(string level, string module, string message, string? gameId = null, string? requestId = null, object? details = null)
    {
        lock (_gate)
        {
            var entry = new LogEntry { Level = level.ToLowerInvariant(), Module = module, Message = Sanitize(message), GameId = gameId, RequestId = requestId, Details = details == null ? null : Sanitize(details is string str ? str : JsonSerializer.Serialize(details, DataStore.Json)) };
            _entries.Add(entry); Trim();
            try
            {
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                if (_activeFile == null || !Path.GetFileName(_activeFile).StartsWith(today))
                    _activeFile = Directory.GetFiles(DirectoryPath, today + "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? Path.Combine(DirectoryPath, today + ".jsonl");
                if (File.Exists(_activeFile) && new FileInfo(_activeFile).Length > 5 * 1024 * 1024) _activeFile = Path.Combine(DirectoryPath, $"{today}-{DateTime.Now:HHmmssfff}.jsonl");
                var file = _activeFile;
                File.AppendAllText(file, JsonSerializer.Serialize(entry, new JsonSerializerOptions(DataStore.Json) { WriteIndented = false }) + Environment.NewLine);
                foreach (var old in Directory.GetFiles(DirectoryPath, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Skip(14)) if (old != file) File.Delete(old);
            }
            catch { /* A log disk error must not prevent translation or recovery. */ }
        }
    }
    private void Trim() { if (_entries.Count > 4000) _entries.RemoveRange(0, _entries.Count - 4000); }
    public List<LogEntry> Query(string? gameId = null, string? level = null, string? query = null, string? module = null)
    {
        lock (_gate) return _entries.Where(e => (string.IsNullOrEmpty(gameId) || e.GameId == gameId) && (string.IsNullOrEmpty(level) || e.Level.Equals(level, StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrEmpty(module) || e.Module == module) && (string.IsNullOrEmpty(query) || $"{e.Message} {e.Details} {e.RequestId}".Contains(query, StringComparison.OrdinalIgnoreCase))).TakeLast(1000).Reverse().Select(e => new LogEntry { Time = e.Time, Level = e.Level, Module = e.Module, Message = Sanitize(e.Message), Details = e.Details == null ? null : Sanitize(e.Details), GameId = e.GameId, RequestId = e.RequestId }).ToList();
    }
}
