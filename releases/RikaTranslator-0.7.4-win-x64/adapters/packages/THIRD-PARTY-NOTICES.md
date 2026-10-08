# Bundled runtime resources

These files were downloaded from their official upstream projects on 2026-10-06. No protected DLLs from RenpyThief or source/UI code from XUnityToolkit-WebUI is redistributed.

| Resource | Version | License and source |
| --- | --- | --- |
| BepInEx Windows Mono x86/x64 | 5.4.23.5 | MIT, `BepInEx.LICENSE`; https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5 |
| BepInEx Unity IL2CPP x86/x64 | 6.0.0-be.788+5b766a3 | LGPL-2.1-only, `BepInEx6.LICENSE`; official build https://builds.bepis.io/projects/bepinex_be ; matching source https://github.com/BepInEx/BepInEx/tree/5b766a3 |
| XUnity.AutoTranslator BepInEx Mono | 5.6.2 | MIT, `XUnity.AutoTranslator.LICENSE`; https://github.com/bbepis/XUnity.AutoTranslator/releases/tag/v5.6.2 |
| XUnity.AutoTranslator BepInEx IL2CPP | 5.6.2 | MIT, `XUnity.AutoTranslator.LICENSE`; same official release, IL2CPP-specific asset |
| Il2CppInterop Runtime / Generator / Common / HarmonySupport | 1.5.3, dependency in official BepInEx 6 ZIP | LGPL-3.0, `Il2CppInterop.LICENSE` includes the GPL text; https://github.com/BepInEx/Il2CppInterop/tree/v1.5.3 |
| Unity Doorstop | 4.5.0 in official BepInEx 6 ZIP | LGPL-2.1, `UnityDoorstop.LICENSE`; https://github.com/NeighTools/UnityDoorstop/tree/v4.5.0 |
| Microsoft .NET CoreCLR | 6.0.7 embedded in official BepInEx 6 ZIP | MIT and dependency notices, `DotNet6.LICENSE`, `DotNet6.THIRD-PARTY-NOTICES`; https://github.com/dotnet/runtime/tree/v6.0.7 |
| HarmonyX / MonoMod / Mono.Cecil / Cpp2IL | Unmodified dependencies in official ZIPs | MIT notices in `HarmonyX.LICENSE`, `MonoMod.LICENSE`, `Mono.Cecil.LICENSE`, `Cpp2IL.LICENSE`; upstream versions/references in matching BepInEx source |
| NotoSansCJKsc-Regular.otf | Upstream noto-cjk main snapshot | SIL Open Font License 1.1, `NotoSansCJK.LICENSE`; https://github.com/notofonts/noto-cjk/tree/main/Sans/OTF/SimplifiedChinese |

The original ZIP packages are preserved unmodified. The installer verifies their SHA-256 before use; pinned values are in EngineService.cs. BepInEx's official package includes Harmony/HarmonyX, MonoMod and Mono.Cecil dependencies. XUnity's official package includes its own extension/resource libraries and ExIni. These remain the upstream projects' distributed binaries; they are not represented as original toolkit code. The local installer selects CustomTranslate exclusively and clears the fallback endpoint.

Corresponding source archives for bundled LGPL components are included in `sources/`: `BepInEx-5b766a3-source.zip` (SHA-256 `184B7486E7005B0528F9E71D7F31A3AF87130AC02063AD9B9A3FD2992DBE00E5`), `Il2CppInterop-1.5.3-source.zip` (`32C37DC7CBD94AEB97A26BDCF14C551118062633D2E2D5491D258570F8B5DD9A`), and `UnityDoorstop-4.5.0-source.zip` (`7F0C963104AA08BF5FEFEF8FF85E7FECD8306838F5AF3101487D9DB4E9188D63`). They preserve upstream source, licenses and build files. No third-party DLL is patched, obfuscated or statically linked into the toolkit; game adapters use separate, replaceable runtime libraries. The installer hash check selects the tested original archive; recipients may rebuild the toolkit and update its package hash when replacing a runtime with their modified compatible build.

The IL2CPP package's first run can fetch the Unity base-library archive configured by BepInEx for the detected Unity version. Generated interop assemblies, those downloaded base libraries, runtime logs and game save files are retained during uninstall. XUnity rewrites `AutoTranslatorConfig.ini` during startup; a changed runtime configuration is archived in the installation's `generated/` directory before the original INI is restored or a newly installed INI is removed.

Reinstall over retained generated data requires a matching toolkit `removed` manifest for the same game identity and absolute directory, with the native loader previously owned by this toolkit. It accepts only the known generated cache/config/log directories and safe data extensions. Existing core/plugin DLLs, native DLLs or scripts masquerading as generated data, unknown directories and links cause a conflict. Reuse of an existing IL2CPP 6 runtime also requires byte-for-byte matches for the tested BepInEx 788 Core/Unity assemblies and Il2CppInterop dependencies; matching `AssemblyVersion=6.0.0.0` alone does not establish compatibility with other bleeding-edge builds.

RPG Maker and Ren'Py adapters in the enclosing adapters directory are original MIT-licensed toolkit code. RPG Maker game runtimes and Ren'Py runtimes themselves are not bundled here.
