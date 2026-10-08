# Galgame Hook 组件

原生组件来自 LunaTranslator v12.0.1 官方 x64 发布，保持原文件；来源、包校验值、全部许可声明及对应完整源码见程序旁的 THIRD-PARTY.md 与 licenses/galgame。

- native：LunaHost64.dll、LunaHook32.dll、LunaHook64.dll、LunaSubprocess32.exe、LunaSubprocess64.exe。
- native-manifest.json：五个原生文件的名称、大小、SHA-256；连接前由主程序校验。
- RikaHookWorker.exe：独立 GPL-3.0-or-later 进程，加载原生 Host，与主程序交换 JSON 行。托管程序集及共享 .NET 依赖位于 ../../runtime。

组件在游戏运行时注入，停止连接后游戏继续运行，重启游戏清除注入状态。具体游戏需要选择正确的对白通道；内嵌能力以通道标记和游戏画面为准。主程序仅把待处理文本和显示设置交给此进程，不把上游 API Key 写入组件或游戏。

ABI 按 v12.0.1 标签源码实现；升级上游组件前须重新核对 ThreadParam、HostSettings 和回调签名，并在 32/64 位合成进程及游戏副本上回归，不直接替换 DLL。
