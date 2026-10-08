using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameTranslateToolkit.Updates;

namespace RikaUpdater;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var quiet = args.Contains("--quiet");
        var index = Array.IndexOf(args, "--plan");
        try
        {
            if (index < 0 || index + 1 >= args.Length) throw new InvalidDataException("请从 Rika Translator 的设置页开始本地更新。");
            var path = Path.GetFullPath(args[index + 1]); UpdatePackage.RejectLinks(path);
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("更新任务记录过大。");
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(path), UpdatePackage.Json) ?? throw new InvalidDataException("更新任务无效。");
            var session = UpdateInstaller.Session(plan);
            if (!path.Equals(Path.Combine(session, "plan.json"), StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.Combine(session, "runner"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新程序必须从对应任务的隔离目录运行。");
            var name = "Local\\RikaUpdate-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(plan.TargetDirectory).ToUpperInvariant())))[..24];
            using var mutex = new Mutex(false, name);
            bool owned;
            try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new InvalidOperationException("此程序目录的另一次更新还在执行。");
            try
            {
                UpdatePackage.WriteJson(Path.Combine(session, "runner-ready.json"), new { plan.Id, processId = Environment.ProcessId });
                if (quiet) RunAsync(plan, args.Contains("--restore"), _ => { }).GetAwaiter().GetResult();
                else
                {
                    ApplicationConfiguration.Initialize();
                    Application.Run(new UpdateWindow(plan, args.Contains("--restore")));
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show(ex.Message, "Rika Translator 本地更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }

    public static async Task<UpdateResult> RunAsync(UpdatePlan plan, bool restore, Action<string> progress)
    {
        UpdateResult result;
        if (restore)
        {
            progress("等待原程序退出并恢复旧程序…");
            await UpdateInstaller.WaitForParentAsync(plan, CancellationToken.None);
            result = UpdateInstaller.RestorePending(plan);
        }
        else result = await new UpdateInstaller(CheckHealthAsync, progress).RunAsync(plan);
        if (result.Status is "success" or "rolled-back" or "failed")
        {
            progress(result.Status == "success" ? "重新打开 Rika Translator…" : "重新打开旧程序…");
            try
            {
                var start = new ProcessStartInfo(Path.Combine(plan.TargetDirectory, "RikaTranslator.exe")) { UseShellExecute = false, WorkingDirectory = plan.TargetDirectory };
                foreach (var argument in plan.RestartArguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("未能启动程序。");
                result.RestartedProcessId = process.Id;
                result.Message += " 已重新启动程序。";
            }
            catch (Exception ex) { result.Message += " 请手动启动程序：" + ex.Message; Environment.ExitCode = 1; }
            result.Time = DateTimeOffset.Now; UpdateInstaller.SaveResult(plan, result);
        }
        if (result.Status != "success") Environment.ExitCode = 1;
        return result;
    }

    private static async Task<bool> CheckHealthAsync(UpdatePlan plan, string session, CancellationToken ct)
    {
        var folder = Path.Combine(session, "health"); Directory.CreateDirectory(folder);
        var data = Path.Combine(folder, "data"); Directory.CreateDirectory(data);
        var settings = Path.Combine(plan.DataDirectory, "settings.json"); UpdatePackage.RejectLinks(settings);
        if (File.Exists(settings)) File.Copy(settings, Path.Combine(data, "settings.json"), false);
        var marker = Path.Combine(folder, "ready.json");
        var start = new ProcessStartInfo(Path.Combine(plan.TargetDirectory, "RikaTranslator.exe")) { UseShellExecute = false, WorkingDirectory = plan.TargetDirectory, CreateNoWindow = true };
        foreach (var value in new[] { "--server", "--data-dir", data, "--port", "0", "--update-health-file", marker, "--expected-version", plan.TargetVersion }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("无法执行新版启动检查。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
        }
        if (process.ExitCode != 0 || !File.Exists(marker)) return false;
        var ready = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(marker));
        return ready.TryGetProperty("version", out var version) && version.GetString() == plan.TargetVersion;
    }
}

internal sealed class UpdateWindow : Form
{
    private readonly Label _message = new() { Dock = DockStyle.Fill, Padding = new Padding(24), Text = "准备更新…", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Bottom, Height = 14, Style = ProgressBarStyle.Marquee };
    private bool _finished;
    public UpdateWindow(UpdatePlan plan, bool restore)
    {
        Text = "Rika Translator · 本地更新"; Size = new Size(540, 220); StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 10); Controls.Add(_message); Controls.Add(_bar);
        FormClosing += (_, e) => e.Cancel = !_finished;
        Shown += async (_, _) =>
        {
            try
            {
                var result = await Program.RunAsync(plan, restore, text => { if (!IsDisposed) BeginInvoke(() => _message.Text = text); });
                _finished = true; _bar.Visible = false; _message.Text = result.Message;
                if (result.Status == "success" && result.RestartedProcessId != null) Close();
                else MessageBox.Show(this, result.Message + "\n\n旧程序备份：" + result.BackupDirectory, "本地更新结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { _finished = true; _bar.Visible = false; _message.Text = "更新未完成：" + ex.Message; }
        };
    }
}
