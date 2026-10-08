using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace GameTranslateToolkit;

/// <summary>Read-only UnityFS/TMP checks using embedded type trees; never resolves external files.</summary>
public static class TmpFontInspector
{
    private const long MaxExpanded = 128L * 1024 * 1024;
    private static readonly Dictionary<string, TmpFontReport> Cache = new();
    private static readonly object Gate = new();
    public static TmpFontReport InspectSafe(byte[] bytes, string gameUnityVersion = "")
    {
        var executable = Path.Combine(GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory, "RikaTranslator.exe");
        if (!File.Exists(executable)) return Inspect(bytes, gameUnityVersion); // Linked-source test harnesses use trusted fixtures.
        var cacheKey = Convert.ToHexString(SHA256.HashData(bytes)) + gameUnityVersion;
        lock (Gate) if (Cache.TryGetValue(cacheKey, out var known)) return JsonSerializer.Deserialize<TmpFontReport>(JsonSerializer.Serialize(known))!;
        var folder = Path.Combine(Path.GetTempPath(), "RikaFontChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "font.bundle"); var result = Path.Combine(folder, "report.json");
        try
        {
            File.WriteAllBytes(input, bytes);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = GameTranslateToolkit.Updates.UpdatePackage.ApplicationDirectory };
            foreach (var value in new[] { "--server", "--data-dir", Path.Combine(folder, "data"), "--inspect-tmp-input", input, "--inspect-tmp-result", result, "--unity-version", gameUnityVersion }) start.ArgumentList.Add(value);
            using var worker = Process.Start(start) ?? throw new InvalidDataException("无法启动独立字体检查。");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!worker.WaitForExit(100))
            {
                worker.Refresh();
                if (DateTime.UtcNow > deadline || worker.WorkingSet64 > 768L * 1024 * 1024)
                { worker.Kill(true); worker.WaitForExit(); throw new InvalidDataException("字体检查超过时间或内存上限，请使用较小、结构完整的 TMP 字体包。"); }
            }
            if (worker.ExitCode != 0 || !File.Exists(result))
            {
                var error = Path.Combine(folder, "data", "startup-error.txt");
                throw new InvalidDataException(File.Exists(error) ? File.ReadAllText(error).Split('\n', 2).Last().Trim() : "字体检查进程未能完成，请重新选择字体包。");
            }
            if (new FileInfo(result).Length > 128 * 1024) throw new InvalidDataException("字体检查结果过大。");
            var report = JsonSerializer.Deserialize<TmpFontReport>(File.ReadAllText(result), Updates.UpdatePackage.Json) ?? throw new InvalidDataException("字体检查没有结果。");
            lock (Gate) { if (Cache.Count >= 24) Cache.Clear(); Cache[cacheKey] = report; }
            return report;
        }
        finally { if (Updates.UpdatePackage.Within(Path.Combine(Path.GetTempPath(), "RikaFontChecks"), folder)) try { Directory.Delete(folder, true); } catch { } }
    }
    public static TmpFontReport Inspect(byte[] bytes, string gameUnityVersion = "")
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        TmpFontReport report;
        lock (Gate)
        {
            if (!Cache.TryGetValue(hash, out report!))
            {
                report = Read(bytes);
                if (Cache.Count >= 24) Cache.Clear();
                Cache[hash] = report;
            }
        }
        var copy = System.Text.Json.JsonSerializer.Deserialize<TmpFontReport>(System.Text.Json.JsonSerializer.Serialize(report))!;
        if (!string.IsNullOrEmpty(gameUnityVersion) && !string.IsNullOrEmpty(copy.UnityVersion) && gameUnityVersion != copy.UnityVersion)
            copy.Warnings.Add($"字体包由 Unity {copy.UnityVersion} 生成，游戏为 {gameUnityVersion}。跨版本加载及 TMP 序列化兼容性尚未验证。");
        return copy;
    }

    private static TmpFontReport Read(byte[] bytes)
    {
        CheckHeader(bytes);
        var manager = new AssetsManager();
        try
        {
            var bundle = manager.LoadBundleFile(new MemoryStream(bytes, false), "font.bundle", false);
            if (bundle.file.BlockAndDirInfo.BlockInfos.Sum(b => (long)b.DecompressedSize) > MaxExpanded || bundle.file.BlockAndDirInfo.DirectoryInfos.Count > 64)
                throw new InvalidDataException("字体包展开后过大或包含过多资源文件。");
            if (bundle.file.DataIsCompressed)
            {
                var unpacked = new MemoryStream();
                bundle.file.Unpack(new AssetsFileWriter(unpacked));
                if (unpacked.Length > MaxExpanded + 8 * 1024 * 1024) throw new InvalidDataException("字体包展开后过大。");
                manager.UnloadAll(); unpacked.Position = 0;
                bundle = manager.LoadBundleFile(unpacked, "unpacked.bundle", false);
            }
            var report = new TmpFontReport { UnityVersion = bundle.file.Header.EngineVersion };
            var uninspectable = false;
            for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                if (!bundle.file.IsAssetsFile(i)) continue;
                var instance = manager.LoadAssetsFileFromBundle(bundle, i, false);
                if (instance.file.AssetInfos.Count > 10000) throw new InvalidDataException("字体包资源数量过多。");
                foreach (var asset in instance.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
                {
                    AssetTypeValueField field;
                    try { field = BaseField(manager, instance, asset.PathId); }
                    catch { uninspectable = true; continue; }
                    var characters = field["m_CharacterTable.Array"];
                    var legacy = field["m_glyphInfoList.Array"];
                    if (characters.IsDummy && legacy.IsDummy) continue;
                    if (field["m_FaceInfo"].IsDummy && field["m_fontInfo"].IsDummy) continue;
                    var info = new TmpFontInfo { Name = Text(field["m_Name"]), Version = Text(field["m_Version"]) };
                    var codes = new HashSet<uint>();
                    foreach (var character in (characters.IsDummy ? legacy : characters).Children)
                    {
                        var unicode = character["m_Unicode"]; if (unicode.IsDummy) unicode = character["unicode"]; if (unicode.IsDummy) unicode = character["id"];
                        if (!unicode.IsDummy) codes.Add(unicode.AsUInt);
                    }
                    info.CharacterCount = codes.Count;
                    info.MissingSample = string.Concat("中文字体测试游戏翻译あいうえおアイウエオABC012".EnumerateRunes().Where(r => !codes.Contains((uint)r.Value)).Select(r => r.ToString()));
                    var mode = field["m_AtlasPopulationMode"]; info.Dynamic = mode.IsDummy ? null : mode.AsInt != 0;
                    var pointers = field["m_AtlasTextures.Array"];
                    var atlases = pointers.IsDummy ? new[] { field["atlas"] }.Where(p => !p.IsDummy) : pointers.Children;
                    var readable = new List<bool?>();
                    foreach (var pointer in atlases)
                    {
                        info.AtlasCount++;
                        var texture = Internal(manager, instance, pointer);
                        readable.Add(texture == null || texture["m_IsReadable"].IsDummy ? null : texture["m_IsReadable"].AsBool);
                    }
                    info.AtlasReadable = readable.Count == 0 || readable.Any(b => b == null) ? null : readable.All(b => b == true);
                    var source = Internal(manager, instance, field["m_SourceFontFile"]);
                    info.SourceFontEmbedded = source != null && !source["m_FontData.Array"].IsDummy && source["m_FontData.Array"].AsByteArray.Length > 0;
                    report.Fonts.Add(info);
                    if (info.Dynamic == true && info.AtlasReadable == false) report.Warnings.Add($"{info.Name} 的动态图集不可读，动态补字可能失败。应使用匹配版本、图集可读的字体包。");
                    if (info.Dynamic == true && !info.SourceFontEmbedded) report.Warnings.Add($"{info.Name} 未确认内嵌源字体；动态补字条件不足或引用了外部资源。");
                    if (info.MissingSample.Length > 0) report.Warnings.Add($"{info.Name} 的静态字符表缺少样例「{info.MissingSample}」；动态补字和后备字体效果须实测。");
                }
            }
            if (report.Fonts.Count == 0) throw new InvalidDataException(uninspectable ? "字体包缺少可读取的字体类型树，无法确认 TMP 字体资源；请使用可检查的匹配字体包。" : "UnityFS 包中没有可识别的 TMP 字体资源。");
            return report;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { throw new InvalidDataException("无法读取 TMP 字体包。文件可能损坏、缺少类型树或使用了不支持的格式。", ex); }
        finally { manager.UnloadAll(); }
    }
    private static string Text(AssetTypeValueField field) => field.IsDummy ? "" : field.AsString;
    private static AssetTypeValueField? Internal(AssetsManager manager, AssetsFileInstance instance, AssetTypeValueField pointer)
    {
        if (pointer.IsDummy || pointer["m_FileID"].IsDummy || pointer["m_FileID"].AsInt != 0 || pointer["m_PathID"].IsDummy || pointer["m_PathID"].AsLong == 0) return null;
        try { return BaseField(manager, instance, pointer["m_PathID"].AsLong); } catch { return null; }
    }
    private static AssetTypeValueField BaseField(AssetsManager manager, AssetsFileInstance instance, long id)
    {
        var info = instance.file.GetAssetInfo(id);
        var template = manager.GetTemplateBaseField(instance, info).Clone();
        CompactBytes(template, 0);
        return template.MakeValue(instance.file.Reader, info.GetAbsoluteByteOffset(instance.file), manager.GetRefTypeManager(instance));
    }
    private static void CompactBytes(AssetTypeTemplateField field, int depth)
    {
        if (depth > 64) throw new InvalidDataException("字体类型树过深。");
        if (field.IsArray && field.Children.Count == 2 && field.Children[1].ValueType is AssetValueType.UInt8 or AssetValueType.Int8) field.ValueType = AssetValueType.ByteArray;
        foreach (var child in field.Children) CompactBytes(child, depth + 1);
    }
    private static void CheckHeader(byte[] bytes)
    {
        if (bytes.Length is < 40 or > 64 * 1024 * 1024 || !bytes.AsSpan().StartsWith("UnityFS\0"u8)) throw new InvalidDataException("这不是有效的 UnityFS 字体包。");
        int offset = 12;
        for (var i = 0; i < 2; i++)
        {
            var end = Array.IndexOf(bytes, (byte)0, offset, Math.Min(128, bytes.Length - offset));
            if (end < 0) throw new InvalidDataException("UnityFS 版本信息无效。"); offset = end + 1;
        }
        if (offset + 20 > bytes.Length) throw new InvalidDataException("字体包头损坏。");
        var size = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset));
        var packedInfo = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8));
        var unpackedInfo = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 12));
        if (size != bytes.Length || packedInfo > 8 * 1024 * 1024 || unpackedInfo > 8 * 1024 * 1024) throw new InvalidDataException("字体包长度或资源信息大小无效。");
    }
}
