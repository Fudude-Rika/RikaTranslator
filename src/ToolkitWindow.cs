using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GameTranslateToolkit;

internal sealed class ToolkitWindow : Form
{
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };
    private readonly string _url;
    private readonly string _dataDirectory;
    private readonly LogService _logs;
    public static ToolkitWindow? Current { get; private set; }
    public ToolkitWindow(string url, string dataDirectory, LogService logs)
    {
        Current = this; _url = url; _dataDirectory = dataDirectory; _logs = logs;
        Text = $"{Program.DisplayName} {Program.Version}";
        using (var iconStream = typeof(ToolkitWindow).Assembly.GetManifestResourceStream("GameTranslateToolkit.RikaIcon"))
        {
            if (iconStream != null)
            {
                using var embeddedIcon = new Icon(iconStream);
                Icon = (Icon)embeddedIcon.Clone();
            }
        }
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 860); MinimumSize = new Size(820, 600);
        Controls.Add(_browser);
        Shown += Initialize;
        FormClosed += (_, _) => Current = null;
    }
    private async void Initialize(object? sender, EventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dataDirectory, "webview"));
            await _browser.EnsureCoreWebView2Async(environment);
            _browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith(_url + "/", StringComparison.Ordinal)) args.Cancel = true;
            };
            _browser.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            try
            {
                // Only clear downloaded UI resources, preserving settings and browser storage.
                await _browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            }
            catch (Exception ex)
            {
                _logs.Write("warning", "app", "清理界面缓存失败，已使用当前版本的界面资源地址。", details: ex.Message);
            }
            _browser.CoreWebView2.Navigate(_url + "/?v=" + Uri.EscapeDataString(Program.Version));
        }
        catch
        {
            _browser.Visible = false;
            var label = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "未找到可用的 Microsoft Edge WebView2 Runtime。\n请安装微软官方 WebView2 Runtime 后重新打开。\n也可以在浏览器访问：" + _url, Font = new Font("Microsoft YaHei UI", 12) };
            Controls.Add(label);
        }
    }
    public static Task<string?> PickExecutableAsync()
        => PickFileAsync("选择游戏启动程序", "游戏启动程序 (*.exe)|*.exe");

    public static Task<string?> PickFontAsync(bool unity)
        => PickFileAsync(unity ? "选择与游戏匹配的 TMP 字体包" : "选择字体文件", unity ? "Unity TMP 字体包|*.bundle;*.assetbundle|所有文件|*.*" : "字体文件 (*.ttf;*.otf)|*.ttf;*.otf");

    public static Task<string?> PickUpdateAsync()
        => PickFileAsync("选择 Rika Translator 本地更新包", "ZIP 更新包 (*.zip)|*.zip");

    public static Task<string?> PickPlayerLogAsync()
        => PickFileAsync("选择此游戏的 Player.log", "游戏日志 (*.log;*.txt)|*.log;*.txt");

    public static Task<string?> PickCacheDirectoryAsync()
    {
        var window = Current ?? throw new InvalidOperationException("当前为服务模式，请手动填写目录完整路径。");
        var completion = new TaskCompletionSource<string?>();
        window.BeginInvoke(() =>
        {
            try { using var dialog = new FolderBrowserDialog { Description = "选择旧 XUnity 译文缓存目录", UseDescriptionForTitle = true }; completion.SetResult(dialog.ShowDialog(window) == DialogResult.OK ? dialog.SelectedPath : null); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }

    public static void CloseForUpdate()
    {
        var window = Current;
        if (window != null && !window.IsDisposed) window.BeginInvoke(() => window.Close());
    }

    internal static Task OnUiAsync(Action action)
    {
        var window = Current ?? throw new InvalidOperationException("服务模式没有桌面浮窗，请使用桌面版启动程序。");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.BeginInvoke(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        return completion.Task;
    }

    private static Task<string?> PickFileAsync(string title, string filter)
    {
        var window = Current;
        if (window == null) throw new InvalidOperationException("当前为服务模式，请手动填写文件的完整路径。");
        var completion = new TaskCompletionSource<string?>();
        window.BeginInvoke(() =>
        {
            try
            {
                using var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
                completion.SetResult(dialog.ShowDialog(window) == DialogResult.OK ? dialog.FileName : null);
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }
}
