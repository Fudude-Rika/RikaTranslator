using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameTranslateToolkit;

public sealed class SecretVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GameTranslateToolkit-v1-local");
    public string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    public string Unprotect(string? value) => string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
}

public sealed class DataStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _file;
    private readonly SecretVault _vault;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly object _gate = new();
    private AppData _data;
    public string DirectoryPath { get; }

    public DataStore(string dataDirectory, SecretVault vault)
    {
        DirectoryPath = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DirectoryPath);
        _file = Path.Combine(DirectoryPath, "settings.json");
        _vault = vault;
        if (File.Exists(_file))
        {
            try { _data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(_file), Json) ?? throw new InvalidDataException("配置数据为空"); }
            catch (Exception ex) { throw new InvalidDataException("配置文件无法读取，原文件已保留。请从备份恢复或使用新的数据目录。", ex); }
        }
        else _data = new();
        foreach (var p in _data.Providers) p.ExtraHeaders = DecryptHeaders(p);
    }
    private Dictionary<string, string> DecryptHeaders(ProviderRecord provider) => string.IsNullOrEmpty(provider.ExtraHeadersEncrypted) ? provider.ExtraHeaders : JsonSerializer.Deserialize<Dictionary<string, string>>(_vault.Unprotect(provider.ExtraHeadersEncrypted)) ?? [];
    public AppData Snapshot() { lock (_gate) return JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(_data, Json), Json)!; }
    public async Task UpdateAsync(Action<AppData> update)
    {
        await _write.WaitAsync();
        try
        {
            AppData next;
            lock (_gate) next = JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(_data, Json), Json)!;
            update(next);
            var disk = JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(next, Json), Json)!;
            foreach (var p in disk.Providers)
            {
                p.ExtraHeadersEncrypted = p.ExtraHeaders.Count == 0 ? null : _vault.Protect(JsonSerializer.Serialize(p.ExtraHeaders));
                p.ExtraHeaders = [];
            }
            var temporary = _file + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(disk, Json), new UTF8Encoding(false));
            if (File.Exists(_file)) File.Copy(_file, _file + ".bak", true);
            File.Move(temporary, _file, true);
            lock (_gate) _data = next;
        }
        finally { _write.Release(); }
    }
}
