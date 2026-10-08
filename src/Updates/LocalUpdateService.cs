using System.Diagnostics;
using System.Text.Json;

namespace GameTranslateToolkit.Updates;

public sealed class UpdatePreview
{
    public string Id { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public int FileCount { get; set; }
    public long PackageSize { get; set; }
    public long ExpandedSize { get; set; }
}

public sealed class LocalUpdateService
{
    private readonly string _root, _data;
    private readonly LogService _logs;
    private readonly string[] _restartArguments;
    private readonly bool _server;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _requests = new();
    private UpdatePlan? _pending;
    private int _writers;
    private bool _scheduled;
    public LocalUpdateService(string root, string data, LogService logs, bool server, int port)
    {
        _root = Path.GetFullPath(root); _data = Path.GetFullPath(data); _logs = logs; _server = server;
        _restartArguments = server ? ["--server", "--data-dir", _data, "--port", port.ToString()] : ["--data-dir", _data, "--port", port.ToString()];
    }

    public void EnterMutation()
    {
        lock (_requests)
        {
            if (_scheduled) throw new InvalidOperationException("正在退出并安装更新，请等待工具重新打开。");
            _writers++;
        }
    }
    public void ExitMutation() { lock (_requests) _writers--; }
    public bool Scheduled { get { lock (_requests) return _scheduled; } }

    public object Status()
    {
        UpdateResult? result = null;
        try
        {
            var path = Path.Combine(_data, "updates", "last-result.json"); UpdatePackage.RejectLinks(path);
            if (File.Exists(path) && new FileInfo(path).Length < 65536)
                result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path), UpdatePackage.Json);
        }
        catch { /* A damaged report must not hide the update entry point. */ }
        var recovery = new List<object>();
        var sessions = Path.Combine(_data, "updates");
        if (Directory.Exists(sessions))
            foreach (var folder in Directory.EnumerateDirectories(sessions).Take(200))
                try
                {
                    var plan = ReadPlan(Path.GetFileName(folder));
                    var path = Path.Combine(folder, "journal.json"); UpdatePackage.RejectLinks(path);
                    if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) continue;
                    var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(path), UpdatePackage.Json);
                    if (journal?.State is "applying" or "rollback-failed") recovery.Add(new { plan.Id, plan.CurrentVersion, plan.TargetVersion });
                }
                catch { }
        return new { currentVersion = Program.Version, architecture = "win-x64", lastResult = result, recovery, scheduled = Scheduled };
    }

    public async Task<UpdatePreview> InspectAsync(string input, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Scheduled) throw new InvalidOperationException("更新已经开始。");
            DiscardPending();
            var path = Path.GetFullPath(input.Trim().Trim('"'));
            if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new InvalidDataException("请选择存在的 ZIP 更新包。");
            UpdatePackage.RejectLinks(path);
            var plan = new UpdatePlan { Id = Guid.NewGuid().ToString("N"), TargetDirectory = _root, DataDirectory = _data, CurrentVersion = Program.Version, RestartArguments = _restartArguments };
            var session = UpdateInstaller.Session(plan); UpdatePackage.RejectLinks(session); Directory.CreateDirectory(session);
            var package = Path.Combine(session, "package.zip");
            try
            {
                await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                {
                    if (source.Length > UpdatePackage.MaxArchiveSize) throw new InvalidDataException("更新包超过 1 GB。");
                    await using var destination = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await UpdatePackage.CopyBoundedAsync(source, destination, source.Length, ct);
                }
                var manifest = await UpdatePackage.ValidateAsync(package, Program.Version, ct);
                foreach (var file in manifest.Files.Concat(manifest.RemoveFiles))
                    if (UpdatePackage.Within(_data, UpdatePackage.Target(_root, file.Path))) throw new InvalidDataException("更新包不能覆盖当前用户数据目录。");
                plan.TargetVersion = manifest.Version; plan.PackageHash = UpdatePackage.Hash(package);
                _pending = plan;
                _logs.Write("info", "update", "本地更新包校验通过，等待用户确认。", details: new { fromVersion = plan.CurrentVersion, targetVersion = plan.TargetVersion, files = manifest.Files.Count });
                return new UpdatePreview { Id = plan.Id, CurrentVersion = plan.CurrentVersion, TargetVersion = manifest.Version, ReleaseNotes = manifest.ReleaseNotes, FileCount = manifest.Files.Count, PackageSize = new FileInfo(package).Length, ExpandedSize = manifest.Files.Sum(f => f.Size) };
            }
            catch { if (File.Exists(package)) File.Delete(package); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task CancelAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { if (Scheduled) throw new InvalidOperationException("更新已经开始。"); if (_pending?.Id == id) DiscardPending(); }
        finally { _gate.Release(); }
    }
    private void DiscardPending()
    {
        if (_pending == null) return;
        var package = Path.Combine(UpdateInstaller.Session(_pending), "package.zip"); UpdatePackage.RejectLinks(package);
        if (File.Exists(package)) File.Delete(package);
        _logs.Write("info", "update", "已取消待确认的本地更新。"); _pending = null;
    }

    public async Task ScheduleAsync(string id, bool confirmed, TranslationStats stats, bool restore, CancellationToken ct)
    {
        if (!confirmed) throw new InvalidOperationException("请先查看更新说明，再确认更新并重启。");
        await _gate.WaitAsync(ct);
        try
        {
            var plan = restore ? ReadPlan(id) : _pending?.Id == id ? _pending : throw new InvalidOperationException("更新校验已失效，请重新选择更新包。");
            lock (_requests)
            {
                if (_scheduled) throw new InvalidOperationException("更新已经开始。");
                if (_writers > 0 || stats.Queued > 0 || stats.InFlight > 0) throw new InvalidOperationException("当前还有操作或翻译任务正在执行，请关闭游戏并等待任务完成后更新。");
                _scheduled = true;
            }
            try
            {
                var session = UpdateInstaller.Session(plan);
                var runner = Path.Combine(session, "runner");
                if (!restore)
                {
                    var manifest = await UpdatePackage.ValidateAsync(Path.Combine(session, "package.zip"), plan.CurrentVersion, ct);
                    if (UpdatePackage.Hash(Path.Combine(session, "package.zip")) != plan.PackageHash) throw new InvalidDataException("更新包发生变化，请重新校验。");
                    var runtimeLayout = UpdatePackage.DetectLayout(_root) == "runtime";
                    var runnerFiles = runtimeLayout ? Directory.GetFiles(Path.Combine(_root, "runtime"), "*", SearchOption.AllDirectories).Append(Path.Combine(_root, "RikaUpdater.exe")).ToArray() : Directory.GetFiles(_root, "*.dll").Concat(new[] { "RikaUpdater.exe", "RikaUpdater.deps.json", "RikaUpdater.runtimeconfig.json" }.Select(n => Path.Combine(_root, n))).ToArray();
                    if (runnerFiles.Any(f => !File.Exists(f))) throw new InvalidOperationException("独立更新程序不完整，请先解压完整便携版。");
                    var oldBytes = manifest.Files.Concat(manifest.RemoveFiles).Sum(f => File.Exists(UpdatePackage.Target(_root, f.Path)) ? new FileInfo(UpdatePackage.Target(_root, f.Path)).Length : 0L);
                    var required = manifest.Files.Sum(f => f.Size) + oldBytes + runnerFiles.Sum(f => new FileInfo(f).Length) + 64L * 1024 * 1024;
                    if (new DriveInfo(Path.GetPathRoot(_data)!).AvailableFreeSpace < required) throw new IOException("数据目录所在磁盘空间不足，无法保存解包文件和旧程序备份。");
                    if (new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace < manifest.Files.Max(f => f.Size) + 64L * 1024 * 1024) throw new IOException("程序所在磁盘空间不足。");
                    UpdatePackage.RejectLinks(runner); Directory.CreateDirectory(runner);
                    foreach (var file in runnerFiles) { var target = UpdatePackage.Target(runner, Path.GetRelativePath(_root, file).Replace('\\', '/')); UpdatePackage.RejectLinks(file); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true); }
                }
                else
                {
                    var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(session, "journal.json")), UpdatePackage.Json);
                    if (journal?.State is not ("applying" or "rollback-failed")) throw new InvalidOperationException("此任务不需要恢复。");
                }
                using var current = Process.GetCurrentProcess();
                plan.ParentProcessId = current.Id; plan.ParentStartTimeUtcTicks = current.StartTime.ToUniversalTime().Ticks;
                var planPath = Path.Combine(session, "plan.json"); UpdatePackage.WriteJson(planPath, plan);
                var start = new ProcessStartInfo(Path.Combine(runner, "RikaUpdater.exe")) { UseShellExecute = false, WorkingDirectory = runner, CreateNoWindow = _server };
                start.ArgumentList.Add("--plan"); start.ArgumentList.Add(planPath);
                if (_server) start.ArgumentList.Add("--quiet");
                if (restore) start.ArgumentList.Add("--restore");
                using var process = Process.Start(start) ?? throw new IOException("无法启动独立更新程序。");
                try
                {
                    var readyPath = Path.Combine(session, "runner-ready.json");
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    bool ready = false;
                    while (DateTime.UtcNow < deadline && !process.HasExited)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (File.Exists(readyPath))
                        {
                            var marker = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(readyPath));
                            if (marker.GetProperty("id").GetString() == id && marker.GetProperty("processId").GetInt32() == process.Id) { ready = true; break; }
                        }
                        await Task.Delay(150, ct);
                    }
                    if (!ready) throw new IOException("独立更新程序未能就绪，当前程序继续运行。请查看更新日志。");
                }
                catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } throw; }
                _logs.Write("info", "update", restore ? "已确认恢复旧程序，将退出并交给独立更新程序。" : "已确认本地更新，将退出并交给独立更新程序。", details: new { plan.CurrentVersion, plan.TargetVersion });
            }
            catch { lock (_requests) _scheduled = false; throw; }
        }
        finally { _gate.Release(); }
    }

    private UpdatePlan ReadPlan(string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new InvalidDataException("更新任务标识无效。");
        var path = Path.Combine(_data, "updates", id, "plan.json"); UpdatePackage.RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 65536) throw new InvalidDataException("没有找到更新任务记录。");
        var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(path), UpdatePackage.Json) ?? throw new InvalidDataException("更新任务记录无效。");
        if (plan.Id != id || !Path.GetFullPath(plan.TargetDirectory).Equals(_root, StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(plan.DataDirectory).Equals(_data, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新记录不属于当前程序。");
        return plan;
    }
}
