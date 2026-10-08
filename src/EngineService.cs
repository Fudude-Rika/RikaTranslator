using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit;

/// <summary>Bounded, read-only engine detection and manifest-based reversible installers.</summary>
public sealed partial class EngineService
{
    private readonly string _installRoot;
    private readonly LogService _logs;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string RpgPlugin = "GameTranslateToolkit";
    private const string RenpyScript = "zz_game_translate_toolkit.rpy";
    private const string FontFile = "NotoSansCJKsc-Regular.otf";
    private const string Unity2022Font = "GameTranslateToolkitCJK-Unity2022.bundle";
    private const string Il2CppBuild = "6.0.0-be.788+5b766a3";
    private const string UnrealPackage = "UE4SS_v3.0.1-1160-g3f4037d2.zip";
    private static readonly Dictionary<string, string> PackageHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BepInEx_win_x64_5.4.23.5.zip"] = "82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4",
        ["BepInEx_win_x86_5.4.23.5.zip"] = "37651C79E40D6F909572A4F461AC25350BB3EF8FE7FBD29F1AA8791A33B84C82",
        ["XUnity.AutoTranslator-BepInEx-5.6.2.zip"] = "836A4066B9369B0D23DC1B0EF6336AD7FE7AE16880931259EC4D157AFF7A81C0",
        ["BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip"] = "F4CC496BD098A0DF4164B81E3737297707F13A47C2478DBA2F60EEFAB784817A",
        ["BepInEx-Unity.IL2CPP-win-x86-6.0.0-be.788+5b766a3.zip"] = "D5954A5993EC39CD1133603D85BFF93875D30B6411B712CC13DCF03C8E08A4D3",
        ["XUnity.AutoTranslator-BepInEx-IL2CPP-5.6.2.zip"] = "639392D3EE3C7542CCA98C6C04B384DEEE4961FE965F3D80A4D9C8413884A0F3",
        [FontFile] = "2C76254F6FC379FDDFCE0A7E84FB5385BB135D3E399294F6EEB6680D0365B74B",
        [Unity2022Font] = "F1AF95422720A8CD8F41C49553E0204E8C19B4E92D2B63C471BEB4DF591B829B",
        [UnrealPackage] = "8318394877278A6342F2BBFF3778F076DA042D2FFF91C414F66B227388488E48"
    };

    public EngineService(string dataDir, LogService logs)
    {
        _installRoot = Path.GetFullPath(Path.Combine(dataDir, "installations"));
        System.IO.Directory.CreateDirectory(_installRoot);
        _logs = logs;
        _fonts = new FontAssets(dataDir);
    }

    public DetectionResult Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请选择游戏 EXE 或游戏目录。");
        path = Path.GetFullPath(path.Trim().Trim('"'));
        if (!File.Exists(path) && !System.IO.Directory.Exists(path)) throw new FileNotFoundException("游戏路径不存在。", path);
        if (File.Exists(path) && !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择游戏 EXE，或包含游戏启动程序的目录。");
        var inputDir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        RejectLinks(inputDir, inputDir);
        var executable = File.Exists(path) ? path : FindExecutable(inputDir);
        var result = new DetectionResult
        {
            Name = Path.GetFileNameWithoutExtension(executable ?? Path.GetFileName(inputDir)),
            ExecutablePath = executable ?? "", Directory = inputDir,
            Architecture = executable == null ? "unknown" : Architecture(executable)
        };
        var roots = CandidateRoots(inputDir).ToList();
        foreach (var root in roots)
        {
            var rpg = RpgRoot(root);
            if (rpg != null)
            {
                var mz = File.Exists(Path.Combine(rpg, "js", "rmmz_core.js"));
                result.Directory = root; result.Engine = mz ? "RPG Maker MZ" : "RPG Maker MV";
                result.Backend = "JavaScript";
                result.EngineVersion = FindVersion(ReadBounded(Path.Combine(rpg, "js", mz ? "rmmz_core.js" : "rpg_core.js")), @"RPGMAKER_VERSION\s*=\s*['"" ]+([\d.]+)");
                result.DetectionNotes = $"发现 {Path.GetRelativePath(root, rpg)}/js/{(mz ? "rmmz_core.js" : "rpg_core.js")} 和插件列表。";
                result.Capability = "experimental-embedded";
                result.Warnings.Add("适配普通对话、选择项与标准文本窗口；图片文字、加密/打包的脚本、完全改写文本窗口的插件需单独验证。");
                break;
            }
            if (System.IO.Directory.Exists(Path.Combine(root, "renpy")) && System.IO.Directory.Exists(Path.Combine(root, "game")))
            {
                result.Directory = root; result.Engine = "Ren'Py"; result.Backend = "Python";
                var renpyVersion = ReadBounded(Path.Combine(root, "renpy", "__init__.py"));
                result.EngineVersion = FindVersion(renpyVersion, @"version(?:_only)?\s*=\s*['"" ]+([\d.]+)");
                if (result.EngineVersion.Length == 0)
                {
                    var tuple = Regex.Match(renpyVersion, @"version_tuple\s*=\s*(?:VersionTuple)?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
                    if (tuple.Success) result.EngineVersion = string.Join(".", tuple.Groups.Cast<Group>().Skip(1).Select(g => g.Value));
                }
                result.Capability = "experimental-embedded";
                result.DetectionNotes = "发现 renpy 与 game 目录；通过 Ren'Py 官方文本替换钩子适配。";
                result.Warnings.Add("面向 Ren'Py 7/8 Windows 发行版；关闭脚本加载、特殊打包或改写文字渲染的游戏仍需验证。");
                break;
            }
            var unityData = UnityData(root, executable);
            if (unityData != null)
            {
                result.Directory = root; result.Engine = "Unity";
                var il2cpp = File.Exists(Path.Combine(root, "GameAssembly.dll")) || File.Exists(Path.Combine(unityData, "il2cpp_data", "Metadata", "global-metadata.dat"));
                result.Backend = il2cpp ? "IL2CPP" : System.IO.Directory.Exists(Path.Combine(unityData, "Managed")) ? "Mono" : "unknown";
                result.EngineVersion = FindVersion(ReadBounded(Path.Combine(unityData, "globalgamemanagers"), 2 * 1024 * 1024), @"((?:20\d{2}|[3456])\.\d+\.\d+[abfp]\d+)");
                if (result.EngineVersion.Length == 0)
                {
                    foreach (var binary in new[] { Path.Combine(root, "UnityPlayer.dll"), executable })
                    {
                        if (binary == null || !File.Exists(binary)) continue;
                        try { result.EngineVersion = FindVersion(FileVersionInfo.GetVersionInfo(binary).ProductVersion ?? "", @"((?:20\d{2}|[3456])\.\d+\.\d+[abfp]\d+)"); } catch { }
                        if (result.EngineVersion.Length > 0) break;
                    }
                }
                result.DetectionNotes = $"发现 {Path.GetFileName(unityData)}，运行后端为 {result.Backend}。";
                result.Capability = result.Backend is "Mono" or "IL2CPP" ? "experimental-embedded" : "detection-only";
                result.Warnings.Add(result.Backend == "Mono"
                    ? "使用官方 BepInEx 5 与 XUnity.AutoTranslator；UGUI、NGUI、TextMeshPro 等实际取词仍取决于游戏实现。TextMeshPro 中文字体资源可能需要另行适配。"
                    : result.Backend == "IL2CPP" ? "使用官方 BepInEx 6 IL2CPP 与 XUnity.AutoTranslator IL2CPP；首启需要生成 interop，可能从官方站点下载 Unity 基础库。安装成功不等于已取得文字，需检查运行日志；TextMeshPro 字体资源需按游戏验证。" : "未知 Unity 后端，当前仅识别，不能选择注入组件。");
                if (System.IO.Directory.Exists(Path.Combine(root, "BepInEx"))) result.Warnings.Add("发现已有 BepInEx，安装前会检查版本及文件冲突，并保留现有组件。");
                break;
            }
            if (IsUnreal(root))
            {
                result.Directory = root; result.Engine = "Unreal Engine"; result.Backend = "Native";
                var shipping = UnrealLayout.ShippingExecutable(root, executable ?? "");
                if (shipping != null) { result.EngineVersion = UnrealLayout.EngineVersion(shipping); result.Architecture = Architecture(shipping); }
                result.Capability = shipping != null && result.Architecture == "x64" && UnrealLayout.SupportedVersion(result.EngineVersion) ? "experimental-embedded" : "detection-only";
                result.DetectionNotes = shipping == null ? "发现 Unreal 打包特征，但未找到唯一的 Win64 Shipping 游戏程序。" : $"已定位 {Path.GetRelativePath(root, shipping)}；{(result.EngineVersion.Length > 0 ? "二进制版本标记为 UE " + result.EngineVersion : "未找到可靠版本标记")}。";
                result.Warnings.Add(result.Capability == "experimental-embedded" ? "使用固定版本 UE4SS 在运行时读取 UMG TextBlock / RichTextBlock，异步回填译文。当前路线面向 UE 5.7 Win64；已验证的游戏与显示范围见测试记录。图片文字、自定义 Slate 绘制和未进入画面的控件不覆盖。" : "本版 Unreal 运行时路线仅开放已验证的 UE 5.7 Win64；未知版本、其他版本及特殊打包仍需单独适配。");
                result.Warnings.Add("中文回填时尝试使用游戏自带的 Roboto 及引擎回退字体，保留字号与描边；会改变字形。若游戏未包含回退字库、使用特殊字体或文本绑定，仍需按游戏验证。已有 UE4SS 或代理 DLL 时会停止安装并保留现有组件。");
                break;
            }
        }
        if (result.Engine == "Unknown") DetectOther(result, inputDir, executable);
        if (IsGalgameEngine(result.Engine) && result.Architecture is "x86" or "x64")
        {
            result.Capability = "experimental-hook";
            result.Warnings.Add("提供 Galgame Hook 取词与翻译浮窗；连接后请选择实际对话通道，内嵌仅对组件标为可回填的通道开放，具体游戏画面仍需验证。");
        }
        if (string.IsNullOrEmpty(result.ExecutablePath)) result.Warnings.Add("未找到唯一可用的游戏 EXE，请直接选择启动程序后再安装。");
        if (result.Engine == "Unknown")
        {
            result.DetectionNotes = "未找到本版能够验证的引擎特征。";
            result.Warnings.Add("可在游戏详情尝试 Galgame Hook：仅支持 Windows x86/x64，能否取词和回填需要实际验证。");
        }
        return result;
    }

    public InstallPlan Plan(GameRecord game)
    {
        var plan = new InstallPlan { GameId = game.Id };
        if (game.Engine == "Unity") plan.Migration = MigrationPreview(game);
        try
        {
            if (ActiveManifest(game) is { } manifest)
            {
                plan.Adapter = manifest.Adapter; plan.Description = "已安装。翻译设置或译文修改后，请重启游戏刷新进程内缓存。Unity 还需处理 XUnity 自身缓存；更改目标语言需卸载后重新安装。修改适配器文件前请先卸载。";
                plan.Warnings.Add("卸载会核验组件文件哈希，并恢复本工具备份的原始文件。Unity 运行时更新的 AutoTranslatorConfig.ini 会先完整归档；其他文件发生外部修改时会停止并保留文件。");
                plan.Files = manifest.Files.Select(f => new PlannedFile { Path = f.RelativePath, Action = f.Existed ? "已安装；卸载时恢复备份" : "已安装；卸载时移除" }).ToList();
                return plan;
            }
            var files = Payload(game, 17865, new string('x', 64));
            plan.Adapter = AdapterName(game); plan.Supported = true;
            plan.Description = "关闭游戏后安装，先备份再写入；游戏显示原文，译文异步回填。游戏目录仅保存本地桥接令牌，不保存 API Key。";
            plan.Warnings.AddRange(game.Warnings);
            foreach (var file in files)
            {
                var target = SafeTarget(game.Directory, file.RelativePath);
                plan.Files.Add(new PlannedFile { Path = file.RelativePath, Action = File.Exists(target) ? "备份并修改" : "新增" });
            }
            if (game.Engine == "Ren'Py") plan.Warnings.Add("Ren'Py 会生成同名 .rpyc 编译缓存；卸载时先归档该缓存再移除。工具不会触碰游戏存档。");
            plan.Warnings.Add("请先关闭游戏。已有未知注入器、同名适配器或冲突 DLL 时不会覆盖。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or BadImageFormatException)
        {
            plan.Supported = false; plan.Adapter = AdapterName(game); plan.Description = ex.Message;
        }
        return plan;
    }

    public string AdapterStatus(GameRecord game)
    {
        try
        {
            var manifest = ActiveManifest(game);
            return manifest == null ? "not-installed" : manifest.State == "installed" ? "installed" : "conflict";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _logs.Write("warning", "installation", "安装清单校验失败，请保留游戏条目并查看备份。", game.Id, details: ex.Message);
            return "conflict";
        }
    }

    public async Task<GameRecord> InstallAsync(GameRecord game, int port, string bridgeToken, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(Path.GetFullPath(game.Directory), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (port is < 1024 or > 65535 || !Regex.IsMatch(bridgeToken, "^[A-Za-z0-9_-]{32,128}$")) throw new InvalidOperationException("本地桥接端口或令牌无效。");
            if (ActiveManifest(game) != null) throw new InvalidOperationException("这个游戏已有安装清单，请先卸载已有适配器。");
            EnsureStopped(game.Directory);
            var files = Payload(game, port, bridgeToken);
            var directory = ManifestDirectory(game.Id);
            System.IO.Directory.CreateDirectory(directory);
            RejectLinks(_installRoot, directory);
            var session = Guid.NewGuid().ToString("N");
            var backupDir = Path.Combine(directory, "backups", session);
            System.IO.Directory.CreateDirectory(backupDir);
            RejectLinks(directory, backupDir);
            var manifest = new InstallationManifest { GameId = game.Id, Root = Path.GetFullPath(game.Directory), Adapter = AdapterName(game), State = "installing", CreatedAt = DateTimeOffset.UtcNow };
            _logs.RegisterSecret(bridgeToken);
            _logs.Write("info", "installation", "正在备份并安装适配器。", game.Id, details: new { manifest.Adapter, FileCount = files.Count });
            try
            {
                // Back up every original before touching any game file. The complete
                // manifest is durable first, so recovery also works after a crash.
                for (var i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = files[i]; var target = SafeTarget(manifest.Root, file.RelativePath);
                    var record = new InstalledFile { RelativePath = file.RelativePath, InstalledHash = Hash(file.Bytes), Existed = File.Exists(target) };
                    if (record.Existed)
                    {
                        var original = ReadFileBounded(target, 64 * 1024 * 1024);
                        record.OriginalHash = Hash(original);
                        record.BackupFile = $"backups/{session}/{i:D4}.bak";
                        File.WriteAllBytes(SafeTarget(directory, record.BackupFile), original);
                    }
                    manifest.Files.Add(record);
                }
                SaveManifest(directory, manifest);
                for (var i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var record = manifest.Files[i]; var target = SafeTarget(manifest.Root, record.RelativePath);
                    if (File.Exists(target) != record.Existed || (record.Existed && FileHash(target) != record.OriginalHash))
                        throw new InvalidOperationException($"安装期间文件被其他程序修改，已停止：{record.RelativePath}");
                    CreateParents(manifest, Path.GetDirectoryName(target)!);
                    SaveManifest(directory, manifest);
                    AtomicWrite(target, files[i].Bytes);
                    record.Applied = true;
                    SaveManifest(directory, manifest);
                }
                manifest.State = "installed"; SaveManifest(directory, manifest);
                game.AdapterStatus = "installed"; game.Capability = "experimental-embedded"; game.BridgeToken = bridgeToken;
                _logs.Write("info", "installation", "适配器安装完成；启动工具并保持运行，再启动游戏。", game.Id, details: new { manifest.Adapter, FileCount = files.Count });
                return game;
            }
            catch (Exception ex)
            {
                var conflicts = Rollback(directory, manifest);
                manifest.State = conflicts.Count == 0 ? "rolled-back" : "conflict";
                manifest.Conflicts = conflicts; SaveManifest(directory, manifest);
                _logs.Write("error", "installation", "安装未完成，已执行备份恢复。", game.Id, details: new { Reason = ex.Message, Conflicts = conflicts });
                if (conflicts.Count != 0) throw new InvalidOperationException("安装失败，部分文件在安装期间被外部修改，已保留并记录恢复冲突：" + string.Join("；", conflicts), ex);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<GameRecord> UninstallAsync(GameRecord game, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(Path.GetFullPath(game.Directory), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            EnsureStopped(game.Directory);
            var directory = ManifestDirectory(game.Id);
            var manifest = ActiveManifest(game);
            if (manifest == null) throw new InvalidOperationException("没有可用的安装清单，无法安全判断哪些文件应恢复或移除。");
            if (!Path.GetFullPath(manifest.Root).Equals(Path.GetFullPath(game.Directory), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("游戏路径与安装清单不一致；请恢复原路径后再卸载。");
            var conflicts = new List<string>();
            var mutableConfigs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                var target = SafeTarget(manifest.Root, file.RelativePath);
                if (File.Exists(target))
                {
                    var hash = FileHash(target);
                    if (!KnownInstalledHash(file, hash) && (!file.Existed || hash != file.OriginalHash))
                    {
                        // XUnity adds defaults and saves its INI during every
                        // startup. Preserve that file before restoration rather
                        // than treating normal engine behaviour as DLL tampering.
                        if (IsUnityRuntimeConfig(manifest, file)) mutableConfigs[file.RelativePath] = ReadFileBounded(target, 1024 * 1024);
                        else conflicts.Add(file.RelativePath);
                    }
                }
                if (file.Existed)
                {
                    var backup = SafeTarget(directory, file.BackupFile ?? throw new InvalidOperationException("安装清单缺少备份路径。"));
                    if (!File.Exists(backup) || FileHash(backup) != file.OriginalHash) conflicts.Add(file.RelativePath + "（原始备份损坏或缺失）");
                }
            }
            if (conflicts.Count > 0)
            {
                manifest.State = "conflict"; manifest.Conflicts = conflicts; SaveManifest(directory, manifest);
                _logs.Write("warning", "installation", "卸载停止：适配器文件在安装后发生修改，已保留所有当前文件和原始备份。", game.Id, details: conflicts);
                throw new InvalidOperationException("以下文件已被外部修改，未执行卸载，请先保存修改并恢复为安装版本后重试：" + string.Join("；", conflicts));
            }
            foreach (var (relative, bytes) in mutableConfigs)
            {
                var archive = $"generated/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}-AutoTranslatorConfig.ini";
                var archivePath = SafeTarget(directory, archive);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
                AtomicWrite(archivePath, bytes);
                if (FileHash(SafeTarget(manifest.Root, relative)) != Hash(bytes)) throw new InvalidOperationException("Unity 配置在归档时被其他程序修改，已停止卸载。");
                manifest.GeneratedArchives.Add(archive); SaveManifest(directory, manifest);
            }
            manifest.State = "removing"; SaveManifest(directory, manifest);
            if (manifest.Adapter == "ue4ss-runtime") ArchiveUnrealGenerated(directory, manifest, game);
            // A Ren'Py compiler cache is generated later by the engine. Preserve
            // its exact bytes before removal, including any later manual changes.
            if (manifest.Adapter == "renpy-text-filter")
            {
                var compiled = SafeTarget(manifest.Root, "game/zz_game_translate_toolkit.rpyc");
                if (File.Exists(compiled))
                {
                    var content = ReadFileBounded(compiled, 64 * 1024 * 1024);
                    var archive = $"generated/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.rpyc";
                    var target = SafeTarget(directory, archive);
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, content);
                    if (FileHash(compiled) != Hash(content)) throw new InvalidOperationException("编译缓存在恢复时被其他程序修改，已停止卸载。");
                    File.Delete(compiled); manifest.GeneratedArchives.Add(archive); SaveManifest(directory, manifest);
                }
            }
            for (var i = manifest.Files.Count - 1; i >= 0; i--)
            {
                ct.ThrowIfCancellationRequested();
                var file = manifest.Files[i]; var target = SafeTarget(manifest.Root, file.RelativePath);
                if (File.Exists(target))
                {
                    var current = FileHash(target);
                    if (!KnownInstalledHash(file, current) && (!file.Existed || current != file.OriginalHash) &&
                        (!mutableConfigs.TryGetValue(file.RelativePath, out var archived) || current != Hash(archived))) throw new InvalidOperationException($"恢复期间文件被外部修改，已停止：{file.RelativePath}");
                }
                if (file.Existed) AtomicWrite(target, ReadFileBounded(SafeTarget(directory, file.BackupFile!), 64 * 1024 * 1024));
                else if (File.Exists(target)) File.Delete(target);
                file.Applied = false; SaveManifest(directory, manifest);
            }
            RemoveEmptyParents(manifest);
            manifest.State = "removed"; manifest.Conflicts.Clear(); SaveManifest(directory, manifest);
            game.AdapterStatus = "not-installed"; game.BridgeToken = "";
            _logs.Write("info", "installation", "适配器已卸载，原始文件已恢复；备份清单继续保留。游戏存档和运行产生的翻译缓存未删除。", game.Id);
            return game;
        }
        finally { gate.Release(); }
    }

    private static bool IsUnityRuntimeConfig(InstallationManifest manifest, InstalledFile file) =>
        manifest.Adapter is "xunity-mono" or "xunity-il2cpp" &&
        file.RelativePath.Replace('\\', '/').Equals("BepInEx/config/AutoTranslatorConfig.ini", StringComparison.OrdinalIgnoreCase);

    private List<PayloadFile> Payload(GameRecord game, int port, string token)
    {
        ValidateId(game.Id);
        FontAssets.Validate(game.Engine, game.Font);
        var root = Path.GetFullPath(game.Directory);
        if (!System.IO.Directory.Exists(root)) throw new DirectoryNotFoundException("游戏目录不存在。");
        RejectLinks(root, root);
        if (string.IsNullOrEmpty(game.ExecutablePath) || !File.Exists(game.ExecutablePath)) throw new InvalidOperationException("请选择存在的游戏 EXE 后再安装。");
        if (!IsInside(root, game.ExecutablePath)) throw new InvalidOperationException("游戏 EXE 不在识别出的游戏目录中。");
        var current = Inspect(game.ExecutablePath);
        if (current.Engine != game.Engine || current.Backend != game.Backend || !current.Directory.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目录的引擎特征已改变，请重新识别游戏后再安装。");
        var bridge = $"http://127.0.0.1:{port}/bridge/translate";
        var result = new List<PayloadFile>();
        void Add(string relative, byte[] bytes, bool mayReplace = false)
        {
            var target = SafeTarget(root, relative);
            if (File.Exists(target) && !mayReplace)
            {
                if (game.Engine == "Unity" && FileHash(target) == Hash(bytes)) return; // shared, identical OSS file: never claim ownership
                throw new InvalidOperationException($"已有同名文件，未覆盖：{relative}。请先处理现有汉化或注入组件。");
            }
            if (System.IO.Directory.Exists(target)) throw new InvalidOperationException($"目标文件位置已有同名目录：{relative}");
            result.Add(new PayloadFile(relative, bytes));
        }
        if (game.Engine is "RPG Maker MV" or "RPG Maker MZ")
        {
            var rpg = RpgRoot(root) ?? throw new InvalidOperationException("未找到可读写的 RPG Maker 插件目录。");
            var prefix = Path.GetRelativePath(root, rpg); if (prefix == ".") prefix = ""; else prefix += "/";
            Add(prefix + "js/plugins/" + RpgPlugin + ".js", RpgFontScript(game));
            if (game.Font.Mode == "custom") Add(prefix + "fonts/" + CustomFontName(game), _fonts.Read(game.Font.FileId!));
            Add(prefix + "fonts/GameTranslateToolkitCJK.otf", Package(FontFile));
            Add(prefix + "fonts/GameTranslateToolkitCJK.LICENSE", Asset("packages/NotoSansCJK.LICENSE"));
            var listPath = Path.Combine(rpg, "js", "plugins.js");
            Add(prefix + "js/plugins.js", ModifyPlugins(ReadFileBounded(listPath, 8 * 1024 * 1024), bridge, game.Id, token, game.SourceLanguage ?? "auto", game.TargetLanguage ?? "zh-CN"), true);
        }
        else if (game.Engine == "Ren'Py")
        {
            if (!string.IsNullOrEmpty(game.EngineVersion) && game.EngineVersion[0] is not ('7' or '8')) throw new InvalidOperationException("本版适配 Ren'Py 7/8，当前版本需要另行验证。");
            if (File.Exists(SafeTarget(root, "game/zz_game_translate_toolkit.rpyc"))) throw new InvalidOperationException("发现同名 Ren'Py 编译缓存，请先备份并处理旧适配器，避免覆盖。");
            Add("game/" + RenpyScript, Asset(RenpyScript));
            Add("game/GameTranslateToolkit/bridge.json", RenpyBridge(game, bridge, token));
            if (game.Font.Mode == "custom") Add("game/GameTranslateToolkit/" + CustomFontName(game), _fonts.Read(game.Font.FileId!));
            Add("game/GameTranslateToolkit/" + FontFile, Package(FontFile));
            Add("game/GameTranslateToolkit/FONT-LICENSE.txt", Asset("packages/NotoSansCJK.LICENSE"));
        }
        else if (game.Engine == "Unity" && game.Backend is "Mono" or "IL2CPP")
        {
            if (game.Architecture is not ("x64" or "x86")) throw new InvalidOperationException("无法确定游戏 EXE 的 x86/x64 架构，不能安全选择 Unity 注入组件。");
            var il2cpp = game.Backend == "IL2CPP";
            var bepinex = Path.Combine(root, "BepInEx", "core", "BepInEx.dll");
            if (il2cpp)
            {
                var native = Path.Combine(root, "GameAssembly.dll");
                if (!File.Exists(native) || Architecture(native) != game.Architecture) throw new InvalidOperationException("IL2CPP GameAssembly.dll 缺失或其架构与游戏 EXE 不一致，未安装。");
                var metadata = Path.Combine(UnityData(root, game.ExecutablePath)!, "il2cpp_data", "Metadata", "global-metadata.dat");
                if (!File.Exists(metadata)) throw new InvalidOperationException("未找到 IL2CPP global-metadata.dat；特殊/加密打包需单独适配。");
                using (var stream = File.OpenRead(metadata)) using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 8 || reader.ReadUInt32() != 0xfab11baf) throw new InvalidOperationException("IL2CPP 元数据头无法识别，可能采用加密或自定义格式，未安装。");
                    var metadataVersion = reader.ReadInt32();
                    if (metadataVersion is < 16 or > 31) throw new InvalidOperationException($"IL2CPP 元数据版本 {metadataVersion} 尚未验证，未安装。当前适配链面向元数据 16–31。请保留检测日志供后续适配。");
                }
                if (File.Exists(bepinex)) throw new InvalidOperationException("IL2CPP 游戏目录发现 BepInEx 5/Mono 组件，请先处理现有注入器冲突。");
                bepinex = Path.Combine(root, "BepInEx", "core", "BepInEx.Core.dll");
            }
            var hasLoader = File.Exists(bepinex);
            if (hasLoader)
            {
                var assembly = AssemblyName.GetAssemblyName(bepinex);
                if (assembly.Name != (il2cpp ? "BepInEx.Core" : "BepInEx") || assembly.Version?.Major != (il2cpp ? 6 : 5)) throw new InvalidOperationException("已有注入器版本或后端不匹配，未修改现有组件。");
                if (il2cpp) ValidateExistingIl2Cpp(root, game.Architecture);
            }
            else
            {
                if ((System.IO.Directory.Exists(Path.Combine(root, "BepInEx")) || System.IO.Directory.Exists(Path.Combine(root, "dotnet"))) && !CanReuseUnityRuntimeResidue(game))
                    throw new InvalidOperationException("发现已有或不完整的 Unity 注入组件；没有匹配的已卸载清单，或目录中包含未知组件，请先处理冲突后再安装。");
                if (new[] { "winhttp.dll", "doorstop_config.ini", "version.dll", "dinput8.dll", "dxgi.dll", "winmm.dll", "dsound.dll" }.Any(name => File.Exists(Path.Combine(root, name))) || System.IO.Directory.Exists(Path.Combine(root, "MelonLoader")))
                    throw new InvalidOperationException("发现已有或不完整的 Unity 注入组件；请先处理冲突后再安装。");
                foreach (var file in ZipFiles(il2cpp ? $"BepInEx-Unity.IL2CPP-win-{game.Architecture}-{Il2CppBuild}.zip" : $"BepInEx_win_{game.Architecture}_5.4.23.5.zip")) Add(file.RelativePath, file.Bytes);
            }
            var pluginCore = Path.Combine(root, "BepInEx", "plugins", "XUnity.AutoTranslator", "XUnity.AutoTranslator.Plugin.Core.dll");
            var pluginEntry = Path.Combine(Path.GetDirectoryName(pluginCore)!, il2cpp ? "XUnity.AutoTranslator.Plugin.BepInEx-IL2CPP.dll" : "XUnity.AutoTranslator.Plugin.BepInEx.dll");
            var oppositeEntry = Path.Combine(Path.GetDirectoryName(pluginCore)!, il2cpp ? "XUnity.AutoTranslator.Plugin.BepInEx.dll" : "XUnity.AutoTranslator.Plugin.BepInEx-IL2CPP.dll");
            if (File.Exists(oppositeEntry)) throw new InvalidOperationException("已有 XUnity 插件对应另一种 Unity 后端，请先处理混装组件。");
            if (!File.Exists(pluginCore)) foreach (var file in ZipFiles(il2cpp ? "XUnity.AutoTranslator-BepInEx-IL2CPP-5.6.2.zip" : "XUnity.AutoTranslator-BepInEx-5.6.2.zip")) Add(file.RelativePath, file.Bytes);
            else if (!File.Exists(pluginEntry)) throw new InvalidOperationException("已有 XUnity 插件缺少对应后端的加载入口，未修改。");
            else if (!File.Exists(Path.Combine(Path.GetDirectoryName(pluginCore)!, "Translators", "CustomTranslate.dll"))) throw new InvalidOperationException("已有 XUnity.AutoTranslator 缺少 CustomTranslate 端点，未改写其组件。");
            if (il2cpp)
            {
                byte[] Resolve(string relative)
                {
                    var payload = result.FirstOrDefault(p => p.RelativePath.Replace('\\', '/').Equals(relative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                    if (payload != null) return payload.Bytes;
                    var path = SafeTarget(root, relative);
                    if (!File.Exists(path)) throw new InvalidOperationException("IL2CPP 加载链缺少文件：" + relative);
                    return ReadFileBounded(path, 64 * 1024 * 1024);
                }
                foreach (var relative in new[] { "BepInEx/plugins/XUnity.AutoTranslator/XUnity.AutoTranslator.Plugin.BepInEx-IL2CPP.dll", "BepInEx/plugins/XUnity.AutoTranslator/XUnity.AutoTranslator.Plugin.Core.dll", "BepInEx/core/XUnity.Common.dll", "BepInEx/core/BepInEx.Unity.IL2CPP.dll" })
                    ValidateManagedDependencies(Resolve, Resolve(relative));
            }
            var configRelative = "BepInEx/config/AutoTranslatorConfig.ini";
            var configPath = SafeTarget(root, configRelative);
            var originalConfig = File.Exists(configPath) ? ReadFileBounded(configPath, 1024 * 1024) : [];
            var configText = Encoding.UTF8.GetString(originalConfig);
            if (configText.Contains("; Game Translate Toolkit", StringComparison.Ordinal)) throw new InvalidOperationException("发现其他安装清单所属的 Toolkit 配置，请先卸载那个游戏条目的适配器。");
            var ini = originalConfig.Length == 0 ? "" : configText.TrimStart('\ufeff');
            ini = SetIni(ini, "Service", "Endpoint", "CustomTranslate");
            ini = SetIni(ini, "Service", "FallbackEndpoint", "");
            ini = SetIni(ini, "General", "Language", IniValue(game.TargetLanguage ?? "zh-CN"));
            ini = SetIni(ini, "General", "FromLanguage", IniValue(game.SourceLanguage ?? "auto"));
            foreach (var framework in new[] { "EnableUGUI", "EnableNGUI", "EnableTextMeshPro", "EnableTextMesh" }) ini = SetIni(ini, "TextFrameworks", framework, "True");
            ini = SetIni(ini, "TextFrameworks", "EnableIMGUI", "False");
            if (game.Font.Mode == "auto" && HasBundledTmp(game))
            {
                Add("BepInEx/fonts/GameTranslateToolkitCJK-Unity2022.bundle", Package(Unity2022Font));
                Add("BepInEx/fonts/GameTranslateToolkitCJK-LICENSE.txt", Asset("packages/Yozai.LICENSE"));
            }
            if (game.Font.Mode == "custom" && game.Font.FileId != null) Add("BepInEx/fonts/" + CustomFontName(game), _fonts.Read(game.Font.FileId));
            ini = UnityFontIni(game, ini);
            AddLegacyCache(game, result, ref ini);
            ini = SetIni(ini, "Behaviour", "MaxCharactersPerTranslation", "2500");
            ini = SetIni(ini, "Custom", "Url", $"{bridge}/{Uri.EscapeDataString(game.Id)}/{token}");
            Add(configRelative, Encoding.UTF8.GetBytes("; Game Translate Toolkit — localhost bridge; no upstream API Key\r\n" + ini), true);
            var licenseText = $"BepInEx {(il2cpp ? Il2CppBuild + " Unity.IL2CPP" : "5.4.23.5 Mono")} / XUnity.AutoTranslator 5.6.2 — official release packages\r\n\r\n" + Encoding.UTF8.GetString(Asset("packages/THIRD-PARTY-NOTICES.md")) + "\r\n" + Encoding.UTF8.GetString(Asset(il2cpp ? "packages/BepInEx6.LICENSE" : "packages/BepInEx.LICENSE")) + "\r\n" + Encoding.UTF8.GetString(Asset("packages/XUnity.AutoTranslator.LICENSE"));
            foreach (var license in new[] { "HarmonyX.LICENSE", "MonoMod.LICENSE", "Mono.Cecil.LICENSE" }.Concat(il2cpp ? new[] { "Il2CppInterop.LICENSE", "UnityDoorstop.LICENSE", "DotNet6.LICENSE", "DotNet6.THIRD-PARTY-NOTICES", "Cpp2IL.LICENSE" } : []))
                licenseText += "\r\n\r\n===== " + license + " =====\r\n" + Encoding.UTF8.GetString(Asset("packages/" + license));
            Add("BepInEx/plugins/GameTranslateToolkit-LICENSES.txt", Encoding.UTF8.GetBytes(licenseText));
        }
        else if (game.Engine == "Unreal Engine")
        {
            if (current.Architecture != "x64" || !UnrealLayout.SupportedVersion(current.EngineVersion)) throw new InvalidOperationException("当前 Unreal 运行时适配仅开放 UE 5.7 Win64，其他版本需单独验证。");
            var shipping = UnrealLayout.ShippingExecutable(root, game.ExecutablePath) ?? throw new InvalidOperationException("无法定位唯一的 Unreal Shipping 游戏程序。");
            var bin = Path.GetDirectoryName(shipping)!;
            if (System.IO.Directory.Exists(Path.Combine(bin, "ue4ss")) || System.IO.Directory.Exists(Path.Combine(bin, "Mods")) ||
                new[] { "dwmapi.dll", "dinput8.dll", "dxgi.dll", "version.dll", "winmm.dll", "dsound.dll", "UE4SS.dll" }.Any(n => File.Exists(Path.Combine(bin, n))))
                throw new InvalidOperationException("发现已有 Unreal 注入器、UE4SS 目录或代理 DLL；请先处理组件冲突，工具不会覆盖。");
            var prefix = Path.GetRelativePath(root, bin).Replace('\\', '/') + "/";
            if (prefix == "./") prefix = "";
            var wanted = new HashSet<string>(["dwmapi.dll", "ue4ss/UE4SS.dll", "ue4ss/LICENSE"], StringComparer.OrdinalIgnoreCase);
            foreach (var file in ZipFiles(UnrealPackage).Where(f => wanted.Contains(f.RelativePath.Replace('\\', '/'))))
            { Add(prefix + file.RelativePath, file.Bytes); wanted.Remove(file.RelativePath.Replace('\\', '/')); }
            if (wanted.Count > 0) throw new InvalidOperationException("固定版本 UE4SS 包缺少必要文件，未安装。");
            Add(prefix + "ue4ss/UE4SS-settings.ini", Asset("unreal/UE4SS-settings.ini"));
            Add(prefix + "ue4ss/Mods/mods.txt", Encoding.ASCII.GetBytes("GameTranslateToolkit : 1\r\n"));
            Add(prefix + "ue4ss/Mods/mods.json", Encoding.ASCII.GetBytes("[]\r\n"));
            var mod = prefix + UnrealLayout.ModRelative + "/";
            Add(mod + "Scripts/main.lua", Asset("unreal/main.lua"));
            Add(mod + "Scripts/config.lua", UnrealConfig(game, token));
            Add(mod + "ipc/requests/.keep", []); Add(mod + "ipc/responses/.keep", []);
        }
        else throw new InvalidOperationException($"{game.Engine} {game.Backend} 当前仅提供检测，尚无可验证运行的适配器。此版本不使用 OCR。");
        return result;
    }

    private static byte[] ModifyPlugins(byte[] bytes, string endpoint, string id, string token, string from, string to)
    {
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\ufeff');
        var match = Regex.Match(text, @"(?:var|let|const)\s+\$plugins\s*=\s*\[");
        if (!match.Success) throw new InvalidOperationException("plugins.js 不是标准插件列表，未尝试修改。");
        var start = text.IndexOf('[', match.Index); var end = BalancedArrayEnd(text, start);
        var list = JsonNode.Parse(text[start..(end + 1)], documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonArray ?? throw new InvalidOperationException("插件列表格式无法识别。");
        if (list.Any(n => n?["name"]?.ToString().Equals(RpgPlugin, StringComparison.OrdinalIgnoreCase) == true)) throw new InvalidOperationException("插件列表已有 Toolkit 适配器，请先通过原安装清单卸载。");
        list.Add(new JsonObject { ["name"] = RpgPlugin, ["status"] = true, ["description"] = "Game Translate Toolkit: local OpenAI-compatible translation bridge", ["parameters"] = new JsonObject { ["Endpoint"] = endpoint, ["Token"] = token, ["GameId"] = id, ["SourceLanguage"] = from, ["TargetLanguage"] = to } });
        var output = Encoding.UTF8.GetBytes(text[..start] + list.ToJsonString(Json) + text[(end + 1)..]);
        return bom ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(output).ToArray() : output;
    }

    private static int BalancedArrayEnd(string text, int start)
    {
        var depth = 0; var inString = false; var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') inString = false; continue; }
            if (c == '"') inString = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return i;
        }
        throw new InvalidOperationException("插件列表数组未闭合，未修改该文件。");
    }

    private static string SetIni(string text, string section, string key, string value)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var header = new Regex(@"^\s*\[([^\]]+)\]\s*(?:[;#].*)?$");
        var sections = lines.Select((line, index) => (Match: header.Match(line), Index: index)).Where(p => p.Match.Success && p.Match.Groups[1].Value.Equals(section, StringComparison.OrdinalIgnoreCase)).Select(p => p.Index).ToList();
        if (sections.Count == 0) { lines.Add("[" + section + "]"); lines.Add(key + "=" + value); return string.Join("\r\n", lines) + "\r\n"; }
        // Duplicate sections are permitted by several INI implementations but
        // differ in first/last precedence. Configure every matching section so
        // no previously selected provider can remain active by ambiguity.
        foreach (var index in sections.AsEnumerable().Reverse())
        {
            var end = index + 1; while (end < lines.Count && !header.IsMatch(lines[end])) end++;
            var existing = -1;
            for (var i = index + 1; i < end; i++) if (Regex.IsMatch(lines[i], @"^\s*" + Regex.Escape(key) + @"\s*=", RegexOptions.IgnoreCase)) { if (existing < 0) { lines[i] = key + "=" + value; existing = i; } else { lines.RemoveAt(i--); end--; } }
            if (existing < 0) lines.Insert(end, key + "=" + value);
        }
        return string.Join("\r\n", lines).TrimEnd() + "\r\n";
    }
    private static string IniValue(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,32}$") ? value : throw new InvalidOperationException("语言代码只能包含字母、数字、下划线和短横线。");
    private static string AdapterName(GameRecord game) => game.Engine switch { "Ren'Py" => "renpy-text-filter", "RPG Maker MV" or "RPG Maker MZ" => "rpg-maker-runtime", "Unity" when game.Backend == "Mono" => "xunity-mono", "Unity" when game.Backend == "IL2CPP" => "xunity-il2cpp", "Unreal Engine" => "ue4ss-runtime", _ => "unavailable" };

    private static void ArchiveUnrealGenerated(string directory, InstallationManifest manifest, GameRecord game)
    {
        var mod = UnrealLayout.ModDirectory(game) ?? throw new InvalidOperationException("Unreal 游戏程序已改变，请恢复目录后卸载。");
        var loader = Path.GetFullPath(Path.Combine(mod, "../.."));
        var generated = new List<string>();
        foreach (var relative in new[] { "UE4SS.log", "UE4SS.log.1", "UE4SS.log.2" })
            if (File.Exists(Path.Combine(loader, relative))) generated.Add(Path.Combine(loader, relative));
        foreach (var relative in new[] { "status.json", "status.json.tmp" })
            if (File.Exists(Path.Combine(mod, relative))) generated.Add(Path.Combine(mod, relative));
        foreach (var sub in new[] { "requests", "responses" })
        {
            var ipc = Path.Combine(mod, "ipc", sub);
            if (!System.IO.Directory.Exists(ipc)) continue;
            foreach (var path in System.IO.Directory.EnumerateFiles(ipc).Take(512))
                if (Regex.IsMatch(Path.GetFileName(path), @"^\d{8,12}-\d{1,10}-\d{1,10}\.(?:(?:req|res)(?:\.tmp)?|tmp)$")) generated.Add(path);
        }
        // Archive only known generated files. Unknown mods or user files remain untouched.
        foreach (var path in generated)
        {
            var relative = Path.GetRelativePath(manifest.Root, path);
            var safe = SafeTarget(manifest.Root, relative);
            var content = ReadFileBounded(safe, 64 * 1024 * 1024);
            var archive = $"generated/unreal-{Guid.NewGuid():N}/{Path.GetFileName(path)}";
            var target = SafeTarget(directory, archive);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!); AtomicWrite(target, content);
            if (FileHash(safe) != Hash(content)) throw new InvalidOperationException("Unreal 运行文件在归档时被其他程序修改，已停止卸载。");
            manifest.GeneratedArchives.Add(archive); SaveManifest(directory, manifest); File.Delete(safe);
        }
    }

    private bool CanReuseUnityRuntimeResidue(GameRecord game)
    {
        var manifestPath = SafeTarget(ManifestDirectory(game.Id), "manifest.json");
        if (!File.Exists(manifestPath)) return false;
        var previous = JsonSerializer.Deserialize<InstallationManifest>(ReadFileBounded(manifestPath, 4 * 1024 * 1024), Json);
        if (previous == null || previous.State != "removed" || previous.GameId != game.Id || previous.Adapter != AdapterName(game) ||
            !Path.GetFullPath(previous.Root).Equals(Path.GetFullPath(game.Directory), StringComparison.OrdinalIgnoreCase) ||
            !previous.Files.Any(f => f.RelativePath.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase) && !f.Existed && !f.Applied)) return false;
        var root = Path.GetFullPath(game.Directory);
        var bepinexRoot = SafeTarget(root, "BepInEx");
        var entries = new Queue<string>(); if (System.IO.Directory.Exists(bepinexRoot)) entries.Enqueue(bepinexRoot);
        var count = 0;
        while (entries.Count > 0)
        {
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(entries.Dequeue()))
            {
                if (++count > 5000) return false;
                RejectLinks(root, entry);
                var relative = Path.GetRelativePath(bepinexRoot, entry).Replace('\\', '/');
                var parts = relative.Split('/'); var area = parts[0].ToLowerInvariant();
                if (System.IO.Directory.Exists(entry))
                {
                    if (area is not ("interop" or "unity-libs" or "cache" or "translation" or "config" or "core" or "plugins" or "patchers")) return false;
                    entries.Enqueue(entry); continue;
                }
                if (parts.Length == 1)
                {
                    if (relative is not ("LogOutput.log" or "ErrorLog.log")) return false;
                    continue;
                }
                var extension = Path.GetExtension(entry).ToLowerInvariant();
                var fileName = Path.GetFileName(entry);
                var allowed = area switch
                {
                    "interop" => extension == ".dll" ? IsGeneratedManagedAssembly(entry) : extension == ".pdb" || fileName is "assembly-hash.txt" or "MethodAddressToToken.db" or "MethodXrefScanCache.db",
                    "unity-libs" => extension == ".dll" ? fileName.StartsWith("UnityEngine", StringComparison.Ordinal) && IsGeneratedManagedAssembly(entry) : Regex.IsMatch(fileName, @"^\d+\.\d+\.\d+(?:[abfp]\d+)?\.zip$"),
                    "cache" => parts.Length == 2 && fileName == "chainloader_typeloader.dat",
                    "config" => parts.Length == 2 && fileName is "BepInEx.cfg" or "gravydevsupreme.xunity.resourceredirector.cfg",
                    "translation" => extension is ".txt" or ".json" or ".png" or ".jpg" or ".dds" or ".bundle" or ".assetbundle",
                    _ => false // core/plugins/patchers must contain no files
                };
                if (!allowed) return false;
            }
        }
        var dotnet = SafeTarget(root, "dotnet");
        if (System.IO.Directory.Exists(dotnet))
        {
            var directories = new Queue<string>(); directories.Enqueue(dotnet);
            while (directories.Count > 0)
                foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directories.Dequeue()))
                {
                    if (++count > 5000) return false;
                    RejectLinks(root, entry);
                    if (!System.IO.Directory.Exists(entry)) return false;
                    directories.Enqueue(entry);
                }
        }
        return true;
    }

    private static bool IsGeneratedManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return false;
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (name.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) || name.StartsWith("XUnity", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException) { return false; }
    }

    private static void ValidateExistingIl2Cpp(string root, string architecture)
    {
        var coreNames = new[] { "BepInEx.Core", "BepInEx.Unity.IL2CPP", "BepInEx.Unity.Common", "BepInEx.Preloader.Core", "Il2CppInterop.Runtime", "Il2CppInterop.Generator", "Il2CppInterop.Common", "Il2CppInterop.HarmonySupport" };
        foreach (var name in coreNames)
        {
            var file = SafeTarget(root, "BepInEx/core/" + name + ".dll");
            if (!File.Exists(file) || AssemblyName.GetAssemblyName(file).Name != name) throw new InvalidOperationException("已有 BepInEx 6 缺少有效 IL2CPP 组件：" + name);
        }
        // Bleeding-edge releases commonly retain AssemblyVersion=6.0.0.0
        // despite incompatible runtime APIs. Reuse only the complete build
        // whose actual binaries are included and exercised by this toolkit.
        using (var packageStream = new MemoryStream(Package($"BepInEx-Unity.IL2CPP-win-{architecture}-{Il2CppBuild}.zip")))
        using (var package = new ZipArchive(packageStream))
            foreach (var name in coreNames)
            {
                var relative = "BepInEx/core/" + name + ".dll";
                var entry = package.GetEntry(relative) ?? throw new InvalidOperationException("官方 IL2CPP 组件包缺少依赖：" + name);
                using var expected = entry.Open();
                if (FileHash(SafeTarget(root, relative)) != Convert.ToHexString(SHA256.HashData(expected)))
                    throw new InvalidOperationException($"已有 BepInEx 6 / Interop 属于另一 build 或修改版本（{name}）；本版仅验证官方 {Il2CppBuild}，未覆盖现有组件。");
            }
        foreach (var relative in new[] { "winhttp.dll", "dotnet/coreclr.dll" })
            if (!File.Exists(Path.Combine(root, relative)) || Architecture(Path.Combine(root, relative)) != architecture) throw new InvalidOperationException("已有 IL2CPP 注入器架构或运行库不匹配：" + relative);
        foreach (var relative in new[] { "dotnet/System.Private.CoreLib.dll", "dotnet/System.Runtime.dll" })
            if (!File.Exists(Path.Combine(root, relative))) throw new InvalidOperationException("已有 IL2CPP CoreCLR 运行库不完整：" + relative);
        var doorstop = ReadBounded(Path.Combine(root, "doorstop_config.ini"));
        if (!Regex.IsMatch(doorstop, @"(?im)^\s*target_assembly\s*=\s*BepInEx[\\/]core[\\/]BepInEx\.Unity\.IL2CPP\.dll\s*$") || !Regex.IsMatch(doorstop, @"(?im)^\s*coreclr_path\s*=\s*dotnet[\\/]coreclr\.dll\s*$") || !Regex.IsMatch(doorstop, @"(?im)^\s*enabled\s*=\s*true\s*$") || !Regex.IsMatch(doorstop, @"(?im)^\s*corlib_dir\s*=\s*dotnet\s*$")) throw new InvalidOperationException("已有 Doorstop 不是启用状态的标准 IL2CPP 启动配置，未修改。");
    }

    private static void ValidateManagedDependencies(Func<string, byte[]> resolver, byte[] plugin)
    {
        using var stream = new MemoryStream(plugin); using var pe = new PEReader(stream); var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle); var name = metadata.GetString(reference.Name);
            if (!name.StartsWith("BepInEx.", StringComparison.Ordinal) && !name.StartsWith("Il2CppInterop.", StringComparison.Ordinal)) continue;
            var bytes = resolver("BepInEx/core/" + name + ".dll");
            using var dependencyStream = new MemoryStream(bytes); using var dependencyPe = new PEReader(dependencyStream); var dependencyMetadata = dependencyPe.GetMetadataReader();
            var definition = dependencyMetadata.GetAssemblyDefinition();
            if (dependencyMetadata.GetString(definition.Name) != name || definition.Version.Major != reference.Version.Major || definition.Version < reference.Version) throw new InvalidOperationException("XUnity IL2CPP 与加载器依赖版本不兼容：" + name);
        }
    }

    private InstallationManifest? ActiveManifest(GameRecord game)
    {
        var dir = ManifestDirectory(game.Id); var file = Path.Combine(dir, "manifest.json");
        if (!File.Exists(file)) return null;
        RejectLinks(_installRoot, file);
        var manifest = JsonSerializer.Deserialize<InstallationManifest>(ReadFileBounded(file, 4 * 1024 * 1024), Json) ?? throw new InvalidOperationException("安装清单无法读取。");
        if (manifest.GameId != game.Id) throw new InvalidOperationException("安装清单的游戏标识不匹配。");
        return manifest.State is "removed" or "rolled-back" ? null : manifest;
    }
    private string ManifestDirectory(string id) { ValidateId(id); return SafeTarget(_installRoot, id); }
    private static void ValidateId(string id) { if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,80}$")) throw new InvalidOperationException("游戏标识无效。"); }
    private static void SaveManifest(string directory, InstallationManifest manifest) => AtomicWrite(Path.Combine(directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));

    private static List<string> Rollback(string directory, InstallationManifest manifest)
    {
        var conflicts = new List<string>();
        foreach (var file in manifest.Files.AsEnumerable().Reverse())
        {
            try
            {
                var target = SafeTarget(manifest.Root, file.RelativePath);
                if (!File.Exists(target)) continue;
                var current = FileHash(target);
                if (file.Existed && current == file.OriginalHash) continue;
                if (current != file.InstalledHash) { if (file.Applied) conflicts.Add(file.RelativePath); continue; }
                if (file.Existed)
                {
                    var bytes = ReadFileBounded(SafeTarget(directory, file.BackupFile!), 64 * 1024 * 1024);
                    if (Hash(bytes) != file.OriginalHash) { conflicts.Add(file.RelativePath + "（备份校验失败）"); continue; }
                    AtomicWrite(target, bytes);
                }
                else File.Delete(target);
            }
            catch (Exception ex) { conflicts.Add(file.RelativePath + "：" + ex.Message); }
        }
        RemoveEmptyParents(manifest);
        return conflicts;
    }
    private static void CreateParents(InstallationManifest manifest, string parent)
    {
        var missing = new Stack<string>(); var current = parent;
        while (!System.IO.Directory.Exists(current) && IsInside(manifest.Root, current)) { missing.Push(current); current = Path.GetDirectoryName(current)!; }
        foreach (var dir in missing)
        {
            RejectLinks(manifest.Root, dir); System.IO.Directory.CreateDirectory(dir);
            manifest.CreatedDirectories.Add(Path.GetRelativePath(manifest.Root, dir));
        }
    }
    private static void RemoveEmptyParents(InstallationManifest manifest)
    {
        foreach (var relative in manifest.CreatedDirectories.OrderByDescending(p => p.Length))
        {
            try { var dir = SafeTarget(manifest.Root, relative); if (System.IO.Directory.Exists(dir) && !System.IO.Directory.EnumerateFileSystemEntries(dir).Any()) System.IO.Directory.Delete(dir, false); }
            catch { /* Keep directories containing runtime caches or external files. */ }
        }
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".gttwrite-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string SafeTarget(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.Split('/', '\\').Any(p => p is ".." or ".")) throw new InvalidOperationException("文件路径越界，已停止操作。");
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsInside(root, result)) throw new InvalidOperationException("文件路径不在允许的目录内。");
        RejectLinks(root, result); return result;
    }
    private static bool IsInside(string root, string path) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void RejectLinks(string root, string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("路径包含符号链接或目录联接，无法安全安装或恢复。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current) ?? "";
        }
    }
    private static void EnsureStopped(string root)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    var executable = process.MainModule?.FileName;
                    if (executable != null && IsInside(root, executable)) throw new GameRunningException("游戏或同目录组件正在运行，请关闭后再安装或卸载。");
                }
            }
            catch (GameRunningException) { throw; }
            catch { /* Windows may deny querying unrelated system processes. */ }
        }
    }
    private sealed class GameRunningException(string message) : InvalidOperationException(message);
    private static byte[] ReadFileBounded(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new InvalidOperationException($"文件超出本版安全读取上限：{Path.GetFileName(path)}");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private static string ReadBounded(string path, int limit = 262144)
    {
        if (!File.Exists(path)) return "";
        try { using var stream = File.OpenRead(path); var bytes = new byte[(int)Math.Min(stream.Length, limit)]; stream.ReadExactly(bytes); return Encoding.UTF8.GetString(bytes); } catch { return ""; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string FileHash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static byte[] Asset(string relative)
    {
        var root = Path.Combine(GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory, "adapters");
        var target = SafeTarget(root, relative); if (!File.Exists(target)) throw new InvalidOperationException("适配器资源缺失，请使用完整发布目录：" + relative);
        return ReadFileBounded(target, 64 * 1024 * 1024);
    }
    private static byte[] Package(string name)
    {
        var bytes = Asset("packages/" + name);
        if (!PackageHashes.TryGetValue(name, out var hash) || Hash(bytes) != hash) throw new InvalidOperationException("第三方组件校验失败，未安装：" + name);
        return bytes;
    }
    private static IEnumerable<PayloadFile> ZipFiles(string package)
    {
        using var stream = new MemoryStream(Package(package)); using var zip = new ZipArchive(stream);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.Length == 0) continue;
            if (entry.Length > 64 * 1024 * 1024) throw new InvalidOperationException("组件包包含异常大文件，未安装。");
            // Validate names independently before touching a target directory.
            if (Path.IsPathRooted(entry.FullName) || entry.FullName.Contains(':') || entry.FullName.Split('/', '\\').Any(p => p is ".." or ".")) throw new InvalidOperationException("组件包包含越界路径，未安装。");
            using var source = entry.Open(); using var content = new MemoryStream(); source.CopyTo(content);
            yield return new PayloadFile(entry.FullName, content.ToArray());
        }
    }

    private static IEnumerable<string> CandidateRoots(string directory)
    {
        var current = directory;
        for (var i = 0; i < 5 && !string.IsNullOrEmpty(current); i++)
        {
            if (Path.GetPathRoot(current)?.Equals(Path.TrimEndingDirectorySeparator(current) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true) yield break;
            yield return current; current = Path.GetDirectoryName(current);
        }
    }
    private static string? RpgRoot(string root)
    {
        foreach (var candidate in new[] { root, Path.Combine(root, "www") })
            if (File.Exists(Path.Combine(candidate, "js", "plugins.js")) && (File.Exists(Path.Combine(candidate, "js", "rpg_core.js")) || File.Exists(Path.Combine(candidate, "js", "rmmz_core.js")))) return candidate;
        return null;
    }
    private static string? UnityData(string root, string? exe)
    {
        var exact = exe == null ? "" : Path.Combine(root, Path.GetFileNameWithoutExtension(exe) + "_Data");
        if (System.IO.Directory.Exists(exact)) return exact;
        try { return System.IO.Directory.EnumerateDirectories(root, "*_Data", SearchOption.TopDirectoryOnly).Take(50).FirstOrDefault(p => File.Exists(Path.Combine(p, "globalgamemanagers")) || System.IO.Directory.Exists(Path.Combine(p, "Managed")) || System.IO.Directory.Exists(Path.Combine(p, "il2cpp_data"))); } catch { return null; }
    }
    private static bool IsUnreal(string root)
    {
        if (System.IO.Directory.Exists(Path.Combine(root, "Engine")) && (System.IO.Directory.Exists(Path.Combine(root, "Engine", "Binaries")) || System.IO.Directory.Exists(Path.Combine(root, "Engine", "Content")))) return true;
        if (System.IO.Directory.Exists(Path.Combine(root, "Content", "Paks")) && System.IO.Directory.Exists(Path.Combine(root, "Binaries"))) return true;
        try { return System.IO.Directory.EnumerateDirectories(root).Take(60).Any(child => System.IO.Directory.Exists(Path.Combine(child, "Content", "Paks")) && System.IO.Directory.Exists(Path.Combine(child, "Binaries"))); } catch { return false; }
    }
    private static void DetectOther(DetectionResult result, string root, string? exe)
    {
        var gameIni = ReadBounded(Path.Combine(root, "Game.ini"));
        if (DetectGalgame(result, root, exe)) return;
        if (EnumerateFiles(root, "*.xp3").Any()) { result.Engine = "KiriKiri"; result.Backend = "Native"; result.DetectionNotes = "发现 XP3 资源；KiriKiri 具体版本和取词能力以运行时 Hook 为准。"; }
        else if (File.Exists(Path.Combine(root, "nscript.dat")) || File.Exists(Path.Combine(root, "0.txt")) && (File.Exists(Path.Combine(root, "arc.nsa")) || File.Exists(Path.Combine(root, "nscr.exe")))) { result.Engine = "NScripter"; result.Backend = "Native"; result.DetectionNotes = "发现 NScripter 脚本或资源特征。"; }
        else if (File.Exists(Path.Combine(root, "Scene.pck")) || Path.GetFileName(exe)?.Contains("Siglus", StringComparison.OrdinalIgnoreCase) == true) { result.Engine = "SiglusEngine"; result.Backend = "Native"; result.DetectionNotes = "发现 SiglusEngine 程序或 Scene.pck 特征。"; }
        else if (gameIni.Contains("RGSS", StringComparison.OrdinalIgnoreCase)) { result.Engine = "RPG Maker XP/VX/VX Ace"; result.Backend = "RGSS"; result.DetectionNotes = "Game.ini 包含 RGSS 运行库特征。"; }
        else if (File.Exists(Path.Combine(root, "data.win"))) { result.Engine = "GameMaker"; result.Backend = "Native"; result.DetectionNotes = "发现 GameMaker data.win。"; }
        else if (File.Exists(Path.Combine(root, "project.godot")) || EnumerateFiles(root, "*.pck").Any(IsGodotPackage)) { result.Engine = "Godot"; result.Backend = "Native"; result.DetectionNotes = "发现 project.godot 或具有 GDPC 文件头的资源包。"; }
        else if (System.IO.Directory.Exists(Path.Combine(root, "tyrano")) || System.IO.Directory.Exists(Path.Combine(root, "www", "tyrano"))) { result.Engine = "TyranoScript"; result.Backend = "JavaScript"; result.DetectionNotes = "发现 tyrano 目录。"; }
        else if (File.Exists(Path.Combine(root, "Data.wolf")) || System.IO.Directory.Exists(Path.Combine(root, "Data", "BasicData"))) { result.Engine = "Wolf RPG Editor"; result.Backend = "Native"; result.DetectionNotes = "发现 Data.wolf 或 Data/BasicData。"; }
        else if (EnumerateFiles(root, "*.qsp").Any()) { result.Engine = "QSP"; result.Backend = "Native"; result.DetectionNotes = "发现 .qsp 游戏文件。"; }
        else if (File.Exists(Path.Combine(root, "resources", "app.asar"))) { result.Engine = "Electron"; result.Backend = "JavaScript"; result.DetectionNotes = "发现 resources/app.asar；无法仅凭 Electron 包确定具体游戏框架。"; }
        if (result.Engine != "Unknown" && !IsGalgameEngine(result.Engine)) result.Warnings.Add("此引擎仅提供目录特征检测；可另外尝试 Galgame Hook，效果待具体游戏验证。");
    }
    private static bool IsGodotPackage(string path) { try { using var stream = File.OpenRead(path); Span<byte> magic = stackalloc byte[4]; return stream.Read(magic) == 4 && magic.SequenceEqual("GDPC"u8); } catch { return false; } }
    public static string ExecutableArchitecture(string exe) => Architecture(exe);
    private static IEnumerable<string> EnumerateFiles(string dir, string pattern) { try { return System.IO.Directory.EnumerateFiles(dir, pattern).Take(100).ToArray(); } catch { return []; } }
    private static string? FindExecutable(string root)
    {
        var files = EnumerateFiles(root, "*.exe").Where(p => !Regex.IsMatch(Path.GetFileName(p), "^(?:unins|uninstall|crash|unitycrash|notification|config|setup|updater|patch|BHVC\\.exe$)", RegexOptions.IgnoreCase)).ToList();
        if (files.Count == 1) return files[0];
        return files.FirstOrDefault(p => Path.GetFileName(p).Equals("Game.exe", StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase));
    }
    private static string Architecture(string exe)
    {
        try
        {
            using var stream = File.OpenRead(exe); using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) return "unknown";
            stream.Position = 0x3c; var offset = reader.ReadInt32();
            if (offset < 64 || offset > stream.Length - 6 || offset > 4 * 1024 * 1024) return "unknown";
            stream.Position = offset; if (reader.ReadUInt32() != 0x00004550) return "unknown";
            return reader.ReadUInt16() switch { 0x14c => "x86", 0x8664 => "x64", 0xaa64 => "arm64", _ => "unknown" };
        }
        catch { return "unknown"; }
    }
    private static string FindVersion(string text, string pattern) { var match = Regex.Match(text, pattern); return match.Success ? match.Groups[1].Value : ""; }
    private sealed record PayloadFile(string RelativePath, byte[] Bytes);

    private sealed class InstallationManifest
    {
        public string GameId { get; set; } = "";
        public string Root { get; set; } = "";
        public string Adapter { get; set; } = "";
        public string State { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public List<InstalledFile> Files { get; set; } = [];
        public List<string> CreatedDirectories { get; set; } = [];
        public List<string> GeneratedArchives { get; set; } = [];
        public List<string> Conflicts { get; set; } = [];
    }
    private sealed class InstalledFile
    {
        public string RelativePath { get; set; } = "";
        public string? BackupFile { get; set; }
        public bool Existed { get; set; }
        public string? OriginalHash { get; set; }
        public string InstalledHash { get; set; } = "";
        public bool Applied { get; set; }
        public List<string> SupersededHashes { get; set; } = [];
    }
}
