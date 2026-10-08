# 第三方组件

界面布局参考本地 XUnity Toolkit，游戏适配组织方式参考 RenpyThief。新工具的前后端与 Ren’Py、RPG Maker 插件独立编写，没有使用原软件受保护的专用 Hook DLL。

- Microsoft .NET 10.0.9：桌面与本地 HTTP 服务，来自微软 SDK 发布的运行时文件；许可及第三方声明保留在 `licenses`。[项目及许可](https://github.com/dotnet/runtime)。
- Microsoft.Web.WebView2 1.0.4129.50：原生窗口中的网页容器，通过 NuGet 获取，许可和声明保留在 `licenses/WebView2`。[官方文档](https://learn.microsoft.com/microsoft-edge/webview2/)。
- BepInEx 5.4.23.5：Unity Mono 模组加载框架，来自官方发布包，保留随包许可证。[官方仓库](https://github.com/BepInEx/BepInEx)。
- BepInEx 6.0.0-be.788（提交 5b766a3）：Unity IL2CPP 加载器，使用未修改的官方 x86/x64 发布包；此版本为 LGPL-2.1，许可证和对应源码 ZIP 保存在 `adapters/packages`。Il2CppInterop、UnityDoorstop 的许可证、对应源码和 CoreCLR 运行时声明一并保留，具体来源见该目录的 `THIRD-PARTY-NOTICES.md`。[官方安装文档](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)。
- XUnity.AutoTranslator 5.6.2：Unity 文本取词和显示组件，来自官方发布包，保留 MIT 许可。[官方仓库](https://github.com/bbepis/XUnity.AutoTranslator)。
- AssetsTools.NET 3.0.0.0：只读检查 UnityFS / TMP 字体资源，MIT 许可证保留于 `licenses/AssetsTools.NET.LICENSE`。程序集使用公开的 AssetsTools.NET 组件，SHA-256：`60AA43D8FC284C1599B478E8E197E915979FE5B31DF7F3F3DD446E78D2435959`；字体解析逻辑独立编写，解析在受限时长和内存的独立进程中运行。[官方仓库及许可](https://github.com/nesrak1/AssetsTools.NET)。
- UE4SS v3.0.1-1160-g3f4037d2：Unreal 5.7 的固定版本运行时组件，来自 [UE4SS 官方实验构建](https://github.com/UE4SS-RE/RE-UE4SS/releases/tag/experimental-latest)。完整包 SHA-256：`8318394877278A6342F2BBFF3778F076DA042D2FFF91C414F66B227388488E48`。安装仅提取原版代理 DLL、UE4SS.dll 与 MIT 许可证，不启用随包控制台或其他模组；翻译 Lua 插件独立编写。许可位于 `licenses/UE4SS.LICENSE`，安装时保留 `ue4ss/LICENSE`。中文回退使用游戏已有字体，便携包不包含或分发游戏字体资产。[Lua API](https://docs.ue4ss.com/lua-api.html)。
- LunaTranslator / LunaHook **v12.0.1**（发布标签提交 `4cab4c3`）：LunaHost64.dll 和 LunaSubprocess32/64.exe 保留[官方发布](https://github.com/HIllya51/LunaTranslator/releases/tag/v12.0.1)原文件；0.7.4 的 LunaHook32/64.dll 从同一标签源码完整重编译，加入本项目 `hijackfuns.cc` 字体基准与缓存修复，标记为 `12.0.1-rika-fontfix1`。不打包上游 GUI 或 Python 环境。原生文件大小和 SHA-256 位于 `adapters/galgame/native-manifest.json`，连接前校验。原发布 ZIP SHA-256：`3555842E3E2D522FA330B723A4BE5ECE1E835ECD3659759BEDC2AFF178BD21EF`。完整[对应标签源码](https://github.com/HIllya51/LunaTranslator/tree/v12.0.1)包 `LunaTranslator-v12.0.1-source.zip`、随发布提供的全部 31 个许可声明，以及 `RikaLunaHook-fontfix` 内的修改源码、统一差异、构建脚本、MinHook v1.3.4 源码、构建和测试清单位于 `licenses/galgame`；源码包 SHA-256：`72DCE61593FBCD00233CAAEE329E68DFE36D226120F7FAF207C0F6DA93B3E024`。修复 DLL 使用 MSVC 19.44、Windows SDK 10.0.26100.0、静态 CRT 构建。保留上游 GPL v3 与原第三方声明。
- RikaHookWorker 0.7.4：本项目独立编写的 **GPL-3.0-or-later** 程序，源码 `tools/HookWorker`；与主程序通过 JSON 行协议通信，在单独进程中加载 LunaHost 的 C ABI。上游 API Key 不传入此程序。完整源码、构建说明及 GPL 全文随程序放在 `licenses/galgame/RikaHookWorker-source/`（不压缩构建）或同名 `.zip`；图标是可选项，使用 .NET 10 SDK 可独立构建。
- Noto CJK 字体：来自官方 notofonts/noto-cjk 仓库，保留 SIL Open Font License。[官方仓库](https://github.com/notofonts/noto-cjk)。
- Unity 2022 TMP 中文后备字体：基于 sorrowmoil 的公开 TMP 字体资产和未修改的 Yozai 0.868 原字体；将衍生 TMP、材质和图集的名称改为 GameTranslateToolkitCJK。保留字体 SIL Open Font License、原作者声明和资产构建者的再打包许可，来源、修改内容与哈希见 `adapters/packages/Yozai-NOTICE.md`。[字体作者](https://github.com/lxgw/yozai-font)，[TMP 资产构建者](https://github.com/sorrowmoil/sorrowmoil-MoeFont-for-XUnity.AutoTranslator)。

组件来源、校验值和具体运行时适配说明保存在 `adapters` 内。使用这些组件不代表所有引擎版本和具体游戏均已通过测试。
