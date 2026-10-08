# 译游 TMP 中文字体（GameTranslateToolkitCJK）

本文件记录字体来源、资产修改和验证边界，随 `GameTranslateToolkitCJK-Unity2022.bundle` 分发。

| 项目 | 记录 |
| --- | --- |
| 修改后资产大小 | 56,029,276 字节（约 53.43 MiB），LZ4 压缩 |
| 修改后资产 SHA-256 | `f1af95422720a8cd8f41c49553e0204e8c19b4e92d2b63c471beb4df591b829b` |
| 原始 TMP 资产 | 作者 Moe release 的 `2022.zip` → `yozai 2022` |
| 原始资产大小 / SHA-256 | 38,861,711 字节 / `92b11d9154b959fe4fbcdb46e0d13676322c6f9e859131f30937ded16db4e142` |
| 下载归档大小 / SHA-256 | 118,142,094 字节 / `0f85774c4c27eaad1ead9d72993c8564d103468b679bd4513fb2117c17fed6b4` |
| Unity 构建版本 | `2022.1.0a15`，与 UnityFS 头部及作者发布说明一致 |
| TMP 资产序列化版本 | `1.1.0`，与具体游戏 TMP 版本可能不同 |
| 原字体版本 | Yozai Regular `0.868`，2025-09-12，LXGW / Y.OzVox |
| 内嵌原始 TTF 大小 / SHA-256 | 15,605,374 字节 / `25071998a0fe6a72f54c235e714986d259633e5ff670b1a2b5e264387f3316ac` |

## 来源与许可

- [TMP 资产下载](https://github.com/sorrowmoil/sorrowmoil-MoeFont-for-XUnity.AutoTranslator/releases/download/Moe/2022.zip)；[作者发布说明](https://github.com/sorrowmoil/sorrowmoil-MoeFont-for-XUnity.AutoTranslator/releases/tag/Moe)。
- [原字体官方 v0.868 发布](https://github.com/lxgw/yozai-font/releases/tag/v0.868)；[原始 TTF 下载](https://github.com/lxgw/yozai-font/releases/download/v0.868/Yozai-Regular.ttf)。
- [原字体完整 OFL](https://github.com/lxgw/yozai-font/blob/90a91ca019faad7b7dae885a4f70a44359021333/OFL.txt)允许按条件随软件捆绑、嵌入与再分发。每份随包副本附完整版权与 OFL；字体及其衍生不得单独销售或改用其他许可。
- [TMP 构建作者许可](https://github.com/sorrowmoil/sorrowmoil-MoeFont-for-XUnity.AutoTranslator/blob/35921b06f86588920856a707d969bf00e1089bce/LICENSE.txt)明确允许下载、使用、修改、重新打包、个人项目及 Unity 汉化用途，要求仍遵守原字体作者的限制。
- 原字体文档将“悠哉 / Yozai”列为保留名称，OFL 文件另保留上游 Y.Oz / YOz 名称。本次修改后的 TMP 资产以 **GameTranslateToolkitCJK** 为主名称；Yozai 仅作来源归属说明，不作为衍生字体品牌。

完整原字体许可证与构建作者许可原文均保存于 `Yozai.LICENSE`。该文件名用于标明原字体归属，安装到游戏时应一并保留许可证副本。

## 2026-10-06 修改记录

使用 UnityPy 1.25.4 修改以下资产名称并重新打包为 LZ4：

- TMP `m_Name`：`GameTranslateToolkitCJK-Regular SDF`。
- TMP `m_FaceInfo.m_FamilyName`：`GameTranslateToolkitCJK`。
- Atlas、Material、Unity Font wrapper 名称及 AssetBundle/container 标识改为对应的新名称。

未编辑原始内嵌 TTF、字形表、字符表、图集像素、字体许可元数据及框架类型。重新载入保存后的资产确认主名称与 FamilyName 已更新；从资产读出的 TTF 与原作者 v0.868 官方 TTF **逐字节一致**，上表 SHA-256 也一致。原始 TTF 保留其原始名称和版权，其软件许可继续为 OFL 1.1。

## 适用范围与验证边界

该包用于 Windows Unity 2022 TextMeshPro 试验适配，经 XUnity 的 `FallbackFontTextMeshPro` 路线加载。它不是安装到 Windows 的桌面字体，不包含厂商 Arial 或 Arial Unicode 字体。

Unity 构建版本、TMP 结构、字体覆盖、图集大小及游戏自定义排版会影响兼容性。资产读取和哈希核验不证明任意 Unity 2022 游戏均能显示中文；其他年份或更旧 TMP 应另选相应资产并在游戏副本中验证。首次加载会解压较大的图集并增加内存占用。

开源字体和资产构建者未对本工具作背书。运行时显示结论应在测试记录中按具体游戏单独说明。
