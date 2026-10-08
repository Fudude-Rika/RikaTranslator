namespace GameTranslateToolkit;

public sealed class TmpFontReport
{
    public string UnityVersion { get; set; } = "";
    public List<TmpFontInfo> Fonts { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string Scope { get; set; } = "文件静态检查；画面、材质和实际动态补字仍需在游戏中验证。";
}
public sealed class TmpFontInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public int CharacterCount { get; set; }
    public string MissingSample { get; set; } = "";
    public bool? Dynamic { get; set; }
    public bool? AtlasReadable { get; set; }
    public bool SourceFontEmbedded { get; set; }
    public int AtlasCount { get; set; }
    public bool DynamicConditionsMet => Dynamic == true && AtlasReadable == true && SourceFontEmbedded;
}
public sealed class UnityMigrationPreview
{
    public bool Available { get; set; }
    public bool ConfigFound { get; set; }
    public string Loader { get; set; } = "未发现";
    public string Translator { get; set; } = "未发现";
    public string TargetLanguage { get; set; } = "";
    public string CacheDirectory { get; set; } = "";
    public int CacheFileCount { get; set; }
    public bool CacheMatchesLanguage { get; set; }
    public Dictionary<string, string> FontValues { get; set; } = [];
    public List<TmpFontReport> TmpFonts { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public List<string> Conflicts { get; set; } = [];
}
