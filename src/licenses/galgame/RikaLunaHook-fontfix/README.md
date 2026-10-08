# LunaHook 字体修复：12.0.1-rika-fontfix1

本目录提供基于 LunaTranslator v12.0.1 的 LunaHook 32/64 位 DLL 的修改源码和构建材料。
上游许可证为 GPL-3.0，全文见 `COPYING`；原第三方版权与许可声明继续保留。

## 修改内容

`DCFontSwitcher` 原先从当前字体重新计算比例，测量与绘字路径重复使用已替换字体时可能累计缩放。
修复保存原始 `LOGFONTW`，创建、测量与绘制共享基准记录，并按基准和目标设置缓存字体。
同时处理失效句柄、UTF-16 字体名和并发，保留上游持续选择替换字体的兼容行为。

唯一修改文件为 `src/NativeImpl/LunaHook/LunaHook/hijackfuns.cc`。
完整替换源码为 `hijackfuns.cc`，差异为 `font-resize.patch`，校验信息为 `patch-manifest.json`。
完整原始源码包位于上一级目录的 `LunaTranslator-v12.0.1-source.zip`。

## 完整 DLL 构建

准备对应架构的 MSVC 开发命令环境、Windows SDK、ATL、CMake 和 Ninja。
当前分发 DLL 使用 MSVC 19.44.35229、SDK 10.0.26100.0、Release 和静态 CRT。
MinHook v1.3.4 完整源码保存在 `minhook-source`，不需要使用个人编译工具目录。

在本目录运行以下命令，输出目录必须是新的空目录：

```powershell
.\build-native.ps1 -Architecture x86 -BuildRoot '.\build-x86'
# 在 x64 的 MSVC 开发环境中运行：
.\build-native.ps1 -Architecture x64 -BuildRoot '.\build-x64'
```

也可以使用 `-PrepareOnly` 只准备对应源码。
脚本检查原始源码包和替换文件 SHA-256；需要手动部署构建结果并更新 `native-manifest.json`。
DLL 大小、架构、依赖、编译设置和 SHA-256 见 `native-build.json`。

## 运行限制

升级后已经加载旧 DLL 的游戏应完全退出后重新连接。
BGI 等游戏会缓存已经绘制的字形，实时修改比例可能混用新旧大小。
修改字体或字号后，保存进度并完全重启游戏。

本目录保留构建所需源码和辅助测试源码，不包含测试截图、游戏验证记录或测试结果报告。
