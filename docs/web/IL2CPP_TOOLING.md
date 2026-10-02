# IL2CPP 逆向工具链现状（Nordicandia Android）

日期：2026-10-02。目标：建立"指令 → 字符串/方法"的通用解析能力，以打通客户端逻辑（制作校验、NPC 等）。

## 现有可用资产
- `tmp/apk-libil2cpp.so`（96,065,080 B，来自 `dist/android-*` 各 APK，大小一致）。
- `tmp/global-metadata.dat`（24,246,868 B，从 `dist/android-arm64-online-patched/com.IterativeStudios.Nordicandia.apk`
  的 `assets/bin/Data/Managed/Metadata/` 提取）。**magic `0xFAB11BAF`，version `39`**。
- `tmp/android-metadata.json`（138 MB，`addressMap`）——已解析出 170,518 个方法、36,771 条字符串字面量、
  类型/字段/方法地址等。**该文件由外部/自定义工具生成，仓库内无对应脚本**。

## 已知的字符串字面量引用机制（探查结果）
- `addressMap.stringLiterals[i].virtualAddress` 是 `.so` 中的槽地址（如 `0x58A0A68`，连续的 +8）。
- 槽内容是 IL2CPP metadata-usage 令牌：低 4 字节 `0xA0003AFF`（kind=5=StringLiteral），如
  `CraftingReason_*` 共 55 条集中在 `0x58A0A00` 起。
- **但代码不直接引用这些槽地址**：全 `.so` 对页 `0x58A0000` 的 `adrp` 只有 2 处（Firebase/Unity Entities，无关）；
  令牌值也只出现在数据段（`0x56740B0`），代码段无立即数/地址引用。
- 结论：该 release 构建通过运行时 metadata-usage 间接解析（codegen module + `g_MetadataUsages`），
  逐指令映射需要完整解析器。

## 工具尝试
| 工具 | 结果 |
|---|---|
| Il2CppDumper v6.7.46（官方 net7 版） | ❌ `Metadata file supplied is not a supported version[39]`（源码仅支持 16–31） |
| Il2CppDumper（源码 + 把版本上限改到 39，net8 构建） | ⚠️ 越过版本检查，但读取 v39 头部即失败：`fieldDefaultValues` 重复键——**v39 头部布局与 v31 不同**（原始 (offset,size) 序列对不上 v31 字段顺序） |
| Cpp2IL 2022.0.7 | ❌ 官方 OSX 资产实为 Linux x86-64 ELF，无法在 arm64 macOS 运行；2022 版也可能不支持 v39 |

## 下一步（把 A 做完的具体路径）
1. **确定 v39 `Il2CppGlobalMetadataHeader` 布局**：对照一个已知能解析 v39 的工具（如更新的 Cpp2IL /
   Il2CppInspector 分支），或从 `global-metadata.dat` 的 (offset,size) 序列 + 已知 section 内容反推字段顺序。
2. 把该布局补进 Il2CppDumper（或自写解析器），解析出 v39 的 `stringLiteral`/`metadataUsage` 表。
3. 之后即可把每条指令引用的令牌映射回字符串，完成"条件 → `CraftingReason_*`"。

在上述完成前，制作系统的**规则集合与公式已还原**（见 `CRAFTING_EXTRACTED.md`），但**逐条件映射尚未完成**。
