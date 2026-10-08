using System.Diagnostics;
using System.Text.Json;

namespace GameTranslateToolkit.Updates;

public sealed class UpdatePlan
{
    public string Id { get; set; } = "";
    public string TargetDirectory { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string PackageHash { get; set; } = "";
    public int ParentProcessId { get; set; }
    public long ParentStartTimeUtcTicks { get; set; }
    public string[] RestartArguments { get; set; } = [];
}

public sealed class UpdateBackup
{
    public string Path { get; set; } = "";
    public string? OriginalHash { get; set; }
    public string NewHash { get; set; } = "";
    public bool Remove { get; set; }
}

public sealed class UpdateJournal
{
    public string State { get; set; } = "backed-up";
    public List<UpdateBackup> Files { get; set; } = [];
}

public sealed class UpdateResult
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string Message { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public int? RestartedProcessId { get; set; }
}

public sealed class UpdateInstaller
{
    private readonly Func<UpdatePlan, string, CancellationToken, Task<bool>> _health;
    private readonly Action<string>? _progress;
    public UpdateInstaller(Func<UpdatePlan, string, CancellationToken, Task<bool>> health, Action<string>? progress = null) { _health = health; _progress = progress; }

    public static string Session(UpdatePlan plan)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(plan.Id, "^[a-f0-9]{32}$")) throw new InvalidDataException("更新任务标识无效。");
        var root = Path.GetFullPath(plan.TargetDirectory);
        if (root.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不能更新磁盘根目录。");
        if (!File.Exists(Path.Combine(root, "RikaTranslator.exe"))) throw new InvalidDataException("目标目录没有 Rika Translator 启动程序。");
        if (UpdatePackage.Within(plan.DataDirectory, root)) throw new InvalidDataException("程序目录不能位于用户数据目录中。");
        UpdatePackage.RejectLinks(root); UpdatePackage.RejectLinks(plan.DataDirectory);
        return Path.Combine(Path.GetFullPath(plan.DataDirectory), "updates", plan.Id);
    }

    private static string Target(UpdatePlan plan, string relative)
    {
        var target = UpdatePackage.Target(plan.TargetDirectory, relative);
        if (UpdatePackage.Within(plan.DataDirectory, target)) throw new InvalidDataException("更新文件不能覆盖用户数据。");
        return target;
    }

    public async Task<UpdateResult> RunAsync(UpdatePlan plan, CancellationToken ct = default)
    {
        var session = Session(plan);
        UpdatePackage.RejectLinks(session);
        var journalPath = Path.Combine(session, "journal.json");
        if (File.Exists(journalPath)) throw new InvalidDataException("此更新任务已执行。请先恢复或重新选择更新包。");
        var result = new UpdateResult { Id = plan.Id, FromVersion = plan.CurrentVersion, TargetVersion = plan.TargetVersion, BackupDirectory = Path.Combine(session, "backup") };
        var journal = new UpdateJournal();
        bool applying = false;
        try
        {
            _progress?.Invoke("等待原程序退出…");
            await WaitForParentAsync(plan, ct);
            var archive = Path.Combine(session, "package.zip");
            if (UpdatePackage.Hash(archive) != plan.PackageHash) throw new InvalidDataException("已确认的更新包发生变化，未安装。");
            _progress?.Invoke("再次校验更新包…");
            var manifest = await UpdatePackage.ValidateAsync(archive, plan.CurrentVersion, ct);
            if (manifest.Version != plan.TargetVersion) throw new InvalidDataException("更新包目标版本发生变化。");
            var nativeVersion = FileVersionInfo.GetVersionInfo(Path.Combine(plan.TargetDirectory, "RikaTranslator.exe"));
            if ($"{nativeVersion.FileMajorPart}.{nativeVersion.FileMinorPart}.{nativeVersion.FileBuildPart}" != plan.CurrentVersion) throw new InvalidDataException("当前程序版本发生变化，请重新选择更新包。");
            foreach (var file in manifest.Files.Concat(manifest.RemoveFiles))
            {
                var target = Target(plan, file.Path);
                if (manifest.RemoveFiles.Contains(file) && File.Exists(target) && (new FileInfo(target).Length != file.Size || UpdatePackage.Hash(target) != file.Sha256)) throw new IOException("旧运行文件已被修改，未整理：" + file.Path);
                if (File.Exists(target)) { using var probe = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); }
            }
            var payload = Path.Combine(session, "payload");
            await UpdatePackage.ExtractAsync(archive, manifest, payload, ct);
            _progress?.Invoke("备份旧程序…");
            foreach (var file in manifest.Files.Concat(manifest.RemoveFiles))
            {
                var target = Target(plan, file.Path);
                if (Directory.Exists(target)) throw new IOException("程序文件位置存在同名目录：" + file.Path);
                var record = new UpdateBackup { Path = file.Path, NewHash = file.Sha256, Remove = manifest.RemoveFiles.Contains(file) };
                if (File.Exists(target))
                {
                    var backup = UpdatePackage.Target(result.BackupDirectory, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, false);
                    record.OriginalHash = UpdatePackage.Hash(backup);
                    if (UpdatePackage.Hash(target) != record.OriginalHash) throw new IOException("备份期间程序文件发生变化。");
                    if (record.Remove && record.OriginalHash != file.Sha256) throw new IOException("旧运行文件在备份前发生变化：" + file.Path);
                }
                journal.Files.Add(record);
            }
            UpdatePackage.WriteJson(journalPath, journal);
            journal.State = "applying";
            UpdatePackage.WriteJson(journalPath, journal); applying = true;
            _progress?.Invoke("安装新版程序…");
            foreach (var record in journal.Files)
            {
                ct.ThrowIfCancellationRequested();
                var target = Target(plan, record.Path);
                if ((record.OriginalHash == null && File.Exists(target)) || (record.OriginalHash != null && (!File.Exists(target) || UpdatePackage.Hash(target) != record.OriginalHash)))
                    throw new IOException("安装期间程序文件发生变化：" + record.Path);
                if (record.Remove) { if (File.Exists(target)) File.Delete(target); }
                else ReplaceFile(UpdatePackage.Target(payload, record.Path), target);
            }
            _progress?.Invoke("检查新版能否启动…");
            if (!await _health(plan, session, ct)) throw new IOException("新版启动检查未通过。");
            foreach (var file in manifest.RemoveFiles)
            {
                for (var folder = Path.GetDirectoryName(Target(plan, file.Path)); folder != null && !folder.Equals(Path.GetFullPath(plan.TargetDirectory), StringComparison.OrdinalIgnoreCase); folder = Path.GetDirectoryName(folder))
                {
                    if (!UpdatePackage.Within(plan.TargetDirectory, folder)) break;
                    UpdatePackage.RejectLinks(folder);
                    if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) break;
                    Directory.Delete(folder, false);
                }
            }
            journal.State = "complete"; UpdatePackage.WriteJson(journalPath, journal);
            result.Status = "success"; result.Message = "本地更新已完成，新版启动检查通过。";
        }
        catch (Exception ex)
        {
            result.Message = "更新失败：" + ex.Message;
            if (applying)
            {
                _progress?.Invoke("恢复旧程序…");
                try { Restore(plan, journal, result.BackupDirectory); journal.State = "rolled-back"; result.Status = "rolled-back"; result.Message += " 已恢复旧程序。"; }
                catch (Exception rollback) { journal.State = "rollback-failed"; result.Status = "rollback-failed"; result.Message += " 恢复尚未完成：" + rollback.Message; }
                UpdatePackage.WriteJson(journalPath, journal);
            }
            else { result.Status = "failed"; result.Message += " 当前程序文件未替换。"; }
        }
        SaveResult(plan, result);
        return result;
    }

    public static UpdateResult RestorePending(UpdatePlan plan)
    {
        var session = Session(plan);
        UpdatePackage.RejectLinks(session);
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(session, "journal.json")), UpdatePackage.Json) ?? throw new InvalidDataException("恢复记录无效。");
        if (journal.State is not ("applying" or "rollback-failed")) throw new InvalidDataException("此任务不需要故障恢复。");
        Restore(plan, journal, Path.Combine(session, "backup"));
        journal.State = "rolled-back"; UpdatePackage.WriteJson(Path.Combine(session, "journal.json"), journal);
        var result = new UpdateResult { Id = plan.Id, Status = "rolled-back", FromVersion = plan.CurrentVersion, TargetVersion = plan.TargetVersion, BackupDirectory = Path.Combine(session, "backup"), Message = "中断的更新已恢复旧程序。" };
        SaveResult(plan, result); return result;
    }

    private static void Restore(UpdatePlan plan, UpdateJournal journal, string backupRoot)
    {
        var errors = new List<string>();
        foreach (var record in journal.Files.AsEnumerable().Reverse())
        {
            try
            {
                var target = Target(plan, record.Path);
                var hash = File.Exists(target) ? UpdatePackage.Hash(target) : null;
                if (hash == record.OriginalHash) continue;
                if (hash != null && hash != record.NewHash) throw new IOException("文件已被外部修改：" + record.Path);
                if (record.OriginalHash == null) { if (File.Exists(target)) File.Delete(target); }
                else
                {
                    var backup = UpdatePackage.Target(backupRoot, record.Path);
                    if (UpdatePackage.Hash(backup) != record.OriginalHash) throw new IOException("旧文件备份校验失败：" + record.Path);
                    ReplaceFile(backup, target);
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        if (errors.Count != 0) throw new IOException(string.Join("；", errors.Take(3)));
    }

    private static void ReplaceFile(string source, string target)
    {
        UpdatePackage.RejectLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".rika-update-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(source, temp, false); File.Move(temp, target, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task WaitForParentAsync(UpdatePlan plan, CancellationToken ct)
    {
        if (plan.ParentProcessId <= 0) return;
        Process parent;
        try { parent = Process.GetProcessById(plan.ParentProcessId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTimeUtcTicks) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await parent.WaitForExitAsync(timeout.Token);
        }
    }

    public static void SaveResult(UpdatePlan plan, UpdateResult result)
    {
        UpdatePackage.WriteJson(Path.Combine(plan.DataDirectory, "updates", "last-result.json"), result);
        var logRoot = Path.Combine(plan.DataDirectory, "logs"); UpdatePackage.RejectLinks(logRoot); Directory.CreateDirectory(logRoot);
        var entry = new { time = result.Time, level = result.Status == "success" ? "info" : "error", module = "update", message = result.Message, details = JsonSerializer.Serialize(new { result.FromVersion, result.TargetVersion, result.Status }, UpdatePackage.Json) };
        File.AppendAllText(Path.Combine(logRoot, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"), JsonSerializer.Serialize(entry, new JsonSerializerOptions(UpdatePackage.Json) { WriteIndented = false }) + Environment.NewLine);
    }
}
