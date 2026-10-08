using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit;

public sealed partial class EngineService
{
    public static bool IsGalgameEngine(string engine) => engine is
        "KiriKiri" or "NScripter" or "SiglusEngine" or "BGI / Ethornell" or "YU-RIS" or
        "CatSystem2" or "Artemis" or "Malie" or "WillPlus" or "Lucifen" or "Silky's" or
        "CMVS" or "Majiro" or "Nitroplus" or "SoftPal" or "RealLive" or "AliceSoft System4" or "Eushully AGE" or "QLIE";

    // Inspect only known resource locations and small headers, never recursively read a library.
    private static bool DetectGalgame(DetectionResult result, string root, string? executable)
    {
        bool Has(string relative)
        {
            var path = root;
            foreach (var segment in relative.Split('/'))
            {
                path = Path.Combine(path, segment);
                try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            }
            return File.Exists(path);
        }
        FileVersionInfo? info = null;
        if (executable != null && Has(Path.GetRelativePath(root, executable).Replace('\\', '/')))
            try { info = FileVersionInfo.GetVersionInfo(executable); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        var description = string.Join(" ", info?.FileDescription, info?.ProductName, info?.InternalName, info?.OriginalFilename);
        bool Set(string engine, string evidence)
        {
            result.Engine = engine; result.Backend = "Native";
            result.DetectionNotes = evidence + "；取词和内嵌能力以实际 Hook 通道为准。";
            return true;
        }
        if (description.Contains("Ethornell", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("BURIKO General Interpreter", StringComparison.OrdinalIgnoreCase) ||
            Has("BGI.gdb") && (Has("BGI.edb") || Has("BGI.hvl")) || Has("BGI.exe") && Has("sysgrp.arc"))
        {
            Set("BGI / Ethornell", description.Contains("Ethornell", StringComparison.OrdinalIgnoreCase)
                ? "EXE 版本信息包含 Ethornell / BURIKO General Interpreter" : "发现 BGI 数据文件组合");
            result.EngineVersion = FindVersion(info?.FileVersion ?? "", @"(?i)Version\s*:\s*([\d.]+)");
            return true;
        }
        if (Has("RealLive.exe") && Has("Seen.txt") || Has("REALLIVEDATA/Start.ini") || description.Contains("RealLiveEngine", StringComparison.OrdinalIgnoreCase))
            return Set("RealLive", "发现 RealLive 程序与脚本，或 RealLive 专用启动信息");
        if (Has("Malie.ini") && (Has("Malie.exe") || Has("data.dat") || Has("data.dzi")))
            return Set("Malie", "发现 Malie.ini 与游戏程序 / 资源组合");
        if (Has("AliceStart.ini")) return Set("AliceSoft System4", "发现 AliceSoft System4 专用 AliceStart.ini");
        if (Has("AGERC.dll")) return Set("Eushully AGE", "发现 Eushully AGE 运行库 AGERC.dll");
        if (Has("dll/Pal.dll")) return Set("SoftPal", "发现 SoftPal 运行库 dll/Pal.dll");
        if (Has("data.arc") && Has("effect.arc") && Has("Script.arc") ||
            Has("bgm.AWF") && Has("effect.AWF") && Has("gcc.ARC") && Has("mes.ARC") && Has("sequence.ARC"))
            return Set("Silky's", "发现 Silky's 脚本、图像与效果资源组合");

        var files = GalgameResourceFiles(root).ToArray();
        if (Has("Rio.arc") && files.Any(p => Path.GetFileName(p).StartsWith("Chip", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(p).Equals(".arc", StringComparison.OrdinalIgnoreCase)) ||
            description.Contains("Will Plus/ETERNAL", StringComparison.OrdinalIgnoreCase) && Has("Data/Rio.arc"))
            return Set("WillPlus", "发现 WillPlus 的 Rio / Chip 资源组合或 ETERNAL 版本信息");
        foreach (var path in files)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".pack")
            {
                var tail = GalgameHeader(path, fromEnd: true);
                if (Regex.IsMatch(tail, @"\AFilePackVer\d+\.\d", RegexOptions.CultureInvariant))
                    return Set("QLIE", $"{Path.GetRelativePath(root, path)} 具有 QLIE FilePackVer 尾部标记");
                continue;
            }
            if (extension is not (".ypf" or ".int" or ".pfs" or ".lpk" or ".cpz" or ".arc" or ".npa")) continue;
            var header = GalgameHeader(path);
            var relative = Path.GetRelativePath(root, path);
            if (extension == ".ypf" && header.StartsWith("YPF\0", StringComparison.Ordinal)) return Set("YU-RIS", $"{relative} 具有 YPF 文件头");
            if (extension == ".int" && header.StartsWith("KIF\0", StringComparison.Ordinal)) return Set("CatSystem2", $"{relative} 具有 KIF 文件头");
            if (extension == ".pfs" && header.Length >= 3 && header.StartsWith("pf", StringComparison.Ordinal) && header[2] is '2' or '6' or '8') return Set("Artemis", $"{relative} 具有 Artemis PFS 文件头");
            if (extension == ".lpk" && header.StartsWith("LPK1", StringComparison.Ordinal)) return Set("Lucifen", $"{relative} 具有 Lucifen LPK1 文件头");
            if (extension == ".cpz" && header.Length >= 4 && header.StartsWith("CPZ", StringComparison.Ordinal) && header[3] is '5' or '6' or '7') return Set("CMVS", $"{relative} 具有 CMVS CPZ 文件头");
            if (extension == ".arc" && Regex.IsMatch(header, @"\AMajiroArcV[1-3]\.000\x00", RegexOptions.CultureInvariant)) return Set("Majiro", $"{relative} 具有 MajiroArcV 文件头");
            if (extension == ".npa" && header.StartsWith("NPA\x01", StringComparison.Ordinal)) return Set("Nitroplus", $"{relative} 具有 Nitroplus NPA 文件头");
        }
        if (Has("scenario/asbmacro.asb") && Has("scenario/main.iet")) return Set("Artemis", "发现 Artemis 的 ASB 脚本与 IET 索引组合");
        return false;
    }

    private static IEnumerable<string> GalgameResourceFiles(string root)
    {
        var total = 0;
        foreach (var relative in new[] { "", "pac", "data", "data/pack", "GameData", "Archive", "script" })
        {
            var directory = root;
            var safe = true;
            foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                directory = Path.Combine(directory, segment);
                try { if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { safe = false; break; } }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { safe = false; break; }
            }
            if (!safe) continue;
            // A missing/inaccessible location cannot hide later known locations.
            foreach (var file in EnumerateFiles(directory, "*").Take(100))
            {
                if (++total > 700) yield break;
                yield return file;
            }
        }
    }
    private static string GalgameHeader(string path, bool fromEnd = false)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fromEnd) { if (stream.Length <= 28) return ""; stream.Position = stream.Length - 28; }
            Span<byte> header = stackalloc byte[32];
            var count = stream.Read(header);
            return Encoding.ASCII.GetString(header[..count]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
