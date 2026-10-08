using System.Text.Json;

namespace GameTranslateToolkit;

public class AppData
{
    public List<GameRecord> Games { get; set; } = [];
    public List<ProviderRecord> Providers { get; set; } = [];
    public List<TranslationRecord> Translations { get; set; } = [];
    public List<GlossaryEntry> Glossary { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}

public class AppSettings
{
    public string? ProviderId { get; set; }
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh-CN";
    public string Prompt { get; set; } = "你是一位专业游戏文本译者，将原文从 {from} 翻译为 {to}。只输出译文，不解释、不添加引号。忠实于原意与角色语气，严格保留占位符、变量、控制符、格式标签和换行。";
    public int Concurrency { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    public string Theme { get; set; } = "system";
    public string Accent { get; set; } = "#3b82f6";
    public int Zoom { get; set; } = 100;
}

public class ProviderRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "自定义接口";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public string? KeyEncrypted { get; set; }
    public bool HasKey => !string.IsNullOrEmpty(KeyEncrypted);
    public bool Stream { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public int TimeoutSeconds { get; set; } = 45;
    public Dictionary<string, string> ExtraHeaders { get; set; } = [];
    public string? ExtraHeadersEncrypted { get; set; }
    public Dictionary<string, JsonElement> ExtraBody { get; set; } = [];
    public int Revision { get; set; } = 1;
}

public class GameRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Engine { get; set; } = "Unknown";
    public string EngineVersion { get; set; } = "";
    public string Architecture { get; set; } = "unknown";
    public string Backend { get; set; } = "";
    public string DetectionNotes { get; set; } = "";
    public List<string> Warnings { get; set; } = [];
    public string AdapterStatus { get; set; } = "not-installed";
    public string Capability { get; set; } = "detection-only";
    public string? ProviderId { get; set; }
    public string? SourceLanguage { get; set; }
    public string? TargetLanguage { get; set; }
    public string Context { get; set; } = "";
    public string? LegacyCachePath { get; set; }
    public string? PlayerLogPath { get; set; }
    public GameFontSettings Font { get; set; } = new();
    public GalgameSettings Galgame { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string BridgeToken { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class GalgameSettings
{
    public bool Enabled { get; set; }
    public bool Embed { get; set; }
    public int Codepage { get; set; } = 932;
    public int WaitMs { get; set; } = 2000;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public int FontSize { get; set; } = 20;
    public string FontColor { get; set; } = "#FFFFFF";
    public int EmbedFontSizePercent { get; set; } = 100;
    public bool ShowOriginal { get; set; } = true;
    public bool SystemHooks { get; set; }
    public bool DeduplicateSentences { get; set; } = true;
    public string HookCode { get; set; } = "";
    public string HookName { get; set; } = "";
    public string Context { get; set; } = "";
    public string Context2 { get; set; } = "";
    public List<string> ManualCodes { get; set; } = [];
}

public class GameFontSettings
{
    public string Mode { get; set; } = "auto";
    public string Family { get; set; } = "Microsoft YaHei";
    public string? FileId { get; set; }
    public string? FileName { get; set; }
    public string TmpMode { get; set; } = "fallback";
    public Dictionary<string, string>? ExistingValues { get; set; }
    public TmpFontReport? TmpReport { get; set; }
    public string UnrealPath { get; set; } = "/Engine/EngineFonts/Roboto.Roboto";
    public string UnrealTypeface { get; set; } = "Regular";
}

public class DetectionResult
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Engine { get; set; } = "Unknown";
    public string EngineVersion { get; set; } = "";
    public string Architecture { get; set; } = "unknown";
    public string Backend { get; set; } = "";
    public string DetectionNotes { get; set; } = "";
    public List<string> Warnings { get; set; } = [];
    public string Capability { get; set; } = "detection-only";
}

public class InstallPlan
{
    public string GameId { get; set; } = "";
    public bool Supported { get; set; }
    public string Adapter { get; set; } = "";
    public string Description { get; set; } = "";
    public List<PlannedFile> Files { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public UnityMigrationPreview? Migration { get; set; }
}
public class PlannedFile
{
    public string Path { get; set; } = "";
    public string Action { get; set; } = "";
}

public class TranslateInput
{
    public string? GameId { get; set; }
    public string Text { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
    public string? ProviderId { get; set; }
    public bool Force { get; set; }
}
public class TranslationResult
{
    public string Text { get; set; } = "";
    public bool Cached { get; set; }
    public string RequestId { get; set; } = "";
    public long ElapsedMs { get; set; }
    public long? Tokens { get; set; }
}
public class TranslationStats
{
    public long Received { get; set; }
    public long Queued { get; set; }
    public long InFlight { get; set; }
    public long Completed { get; set; }
    public long Failed { get; set; }
    public long CacheHits { get; set; }
    public long Tokens { get; set; }
    public long LastElapsedMs { get; set; }
}
public class TranslationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? GameId { get; set; }
    public string Source { get; set; } = "";
    public string Translation { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public string CacheKey { get; set; } = "";
    public bool Manual { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public class GlossaryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GameId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Note { get; set; } = "";
}

public class LogEntry
{
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string Level { get; set; } = "info";
    public string Module { get; set; } = "app";
    public string Message { get; set; } = "";
    public string? GameId { get; set; }
    public string? RequestId { get; set; }
    public string? Details { get; set; }
}
