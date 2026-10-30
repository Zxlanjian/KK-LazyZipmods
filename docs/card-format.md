# 恋活卡片文件格式实测（角色卡 / 服装卡 / Studio 场景卡）

> 全部结论来自本机真实文件的字节级实测（hexdump + 可运行的解析器），不是推测。
> 实测样本：`<KoikatsuDir>\UserData\` 下 **644 个 PNG**（chara\female 3、chara\male 2、studio\scene 40、coordinate 599）。
> 解析器实现：`src/Core_Sideloader/CardScan/`（net35 兼容，`csc.exe` v3.5.30729.9151 编译通过，0 warning 0 error）。
> 验证程序：`tools/cardscan/`。
>
> **Note on paths.** These notes were written against a specific local install, so absolute paths
> have been replaced with placeholders: `<KoikatsuDir>` is the game folder (the one containing
> `Koikatu_Data\`), `<repo>` is this repository, `<upstream>` is the BepisPlugins checkout this fork
> is based on, and `<work>` is any scratch build directory. File names, offsets, byte counts and IL
> are reproduced verbatim from the actual disassembly.

---

## 0. 一句话结论

**三种卡片都不是「PNG + 尾部追加块」结构。** ExtensibleSaveFormat（ExtSave）的数据被序列化成
MessagePack，**内嵌在卡片自己的 msgpack 数据块里**（键名 `ExtendedSaveData`），位置随卡片类型和
卡片内容浮动在文件任意处（实测从偏移 83090 到 40010757，甚至距文件尾 253 MB）。

因此本库**不做布局假设**，而是：

1. 校验 PNG 签名；
2. **全文件字节扫描**，同时找「卡类型标记」和「UAR 插件键的 `D9 <len> <key>` 编码」；
3. 命中 UAR 键后，从键后第一个字节开始用最小化 MessagePack 解析器解析值；
4. 形状校验（值必须是 `[int, map]` 且 map 里至少有一个已知 UAR 子键），否则丢弃该命中。

---

## 1. 三种卡片的整体布局（实测）

### 1.1 角色卡 `【KoiKatuChara】`

文件：`<KoikatsuDir>\UserData\chara\female\2026-09-24-13-23-45_00_阿格莱亚_Female.png`（47 633 377 字节）

```
0x00000000  PNG#1（1x1 占位缩略图；IHDR = 00 00 00 01 00 00 00 01）
0x00000041  PNG#1 的 IEND 数据结束
0x00000044  64                          'd'  卡头魔数
0x00000045  00 00 00 12                 int32 BE = 18
0x00000049  E3 80 90 "KoiKatuChara" E3 80 90   【KoiKatuChara】
0x0000005D  05                          版本长度 = 5
0x0000005E  "0.0.0"                     版本字符串
0x00000063  44 00 00 00                 之后是第二个 PNG（又一张 1x1 占位图）
0x000000A1  "IEND" ...                   PNG#2 的 IEND
0x000000A9  E5 00 00 00                 int32 LE = 229  ← lstInfo 块表长度
0x000000AD  81 A7 "lstInfo" 95 ...      msgpack: {lstInfo: [5 个 {name,version,pos,size}]}
0x00000192  47 D2 D6 02                 int32 LE = 47 632 967 ← 主数据块长度
0x00000196  00 00 00 00 FD 05 00 00     主数据块 payload 起点（= 406）
0x0000019E  DE 00 27 ...                msgpack map16(39) 主数据开始
...
0x0001447B  B0 "ExtendedSaveData" C0    ← ExtSave 的键（值是 nil 占位）
0x0001448C  DE 00 1E ...                msgpack map16(30) 插件字典
0x00014492  ...                         ← UAR 插件键文本起点（见 §3）
...
0x2D6D1F0   </characterInfo> ...        主数据块尾部变成 XML 文本（skinnedMesh 定义）
0x02D6D3DD  EOF（47633377）
```

**lstInfo 块表实测内容**（`[int32 LE size][msgpack payload]`，payload 是 `{lstInfo: [...]}`）：

| name | version | pos | size | 累计（pos + 上一个 size） |
| --- | --- | --- | --- | --- |
| `Custom` | `0.0.0` | `0` | `0x0E88` (3720) | 0 |
| `Coordinate` | `0.0.0` | `0x0E88` (3720) | `0x012EE8` (77544) | 3720 ✓ |
| `Parameter` | `0.0.5` | `0x013D70` (81264) | `0x023D` (573) | 81264 ✓ |
| `Status` | `0.0.0` | `0x013FAD` (81837) | `0x0346` (838) | 81837 ✓ |
| `KKEx` | `3` | `0x142F3` (82675) | `0x02D58F54` (47632980) | 82675 ✓ |

（msgpack 数组里的顺序是 `KKEx, Custom, Coordinate, Parameter, Status`，与 `pos` 顺序不同；
上表按 `pos` 排序，可以看出 4 个非 KKEx 块首尾相接，`KKEx` 正好排在它们之后。）

> **`pos` 是卡片内部「块流」里的顺序偏移，不是文件偏移。** 实测：
> 主数据块 payload 从文件偏移 **406** 开始，而 `KKEx` 的 `pos` = 82675；
> 406 + 82675 = 83081，落在 `"ExtendedSaveData"` 字符串**中间**（该串实际在 83068–83083），
> 而 UAR 键文本在 **83090**（差 415 字节）。
> 也就是说 **`pos` 既不是文件偏移，也不能用来精确定位 UAR 数据** —— 这是本库放弃
> 「按块表跳转」、改用全文件扫描的直接原因。

### 1.2 服装卡 `【KoiKatuClothes】`

文件：`<KoikatsuDir>\UserData\coordinate\1.png`（63 178 221 字节）

```
0x00000000  PNG（真图，252x352，IHDR 00 00 00 FC 00 00 01 60）
0x00025A79  pngEnd = 154041
0x00025A79  64 00 00 00 14                 'd' + int32 BE = 20
0x00025A82  E3 80 90 "KoiKatuClothes" E3 80 90   【KoiKatuClothes】
0x00025A96  05 "0.0.0"                       版本
0x00025A9C  00 E2 28 00 00 DF 12 00 00        块前缀
0x00025AA4  86 A7 "version" A5 "0.0.1" A5 "parts" 99 ...   msgpack 直接开始
0x000282CF                                     ← UAR 插件键
```

**与角色卡的差别（重要）**：
- PNG 是**真图**（不是 1x1 占位），卡数据在 PNG **之后**；
- **没有 `lstInfo` 块表**（全文件搜索 `lstInfo` 命中数 = 0），卡头之后直接是 msgpack 数据。

### 1.3 Studio 场景卡 `【KStudio】`

文件：`<KoikatsuDir>\UserData\studio\scene\2026_0614_1349_59_706.png`（125 016 936 字节）

```
0x00000000  PNG#1（真图 320x180；IHDR = 00 00 01 40 00 00 00 B4）
0x000195B3  pngEnd = 103859（IEND 的 CRC 结束处）
0x000195B3  07 "1.0.4.2" 01 00 ...          场景头（0x07 + 7 字符版本串 + 二进制，非长度字段）
0x000195F8  64 00 00 00 12                  'd' + int32 BE = 18  ← 场景里内嵌的角色卡头
0x000195FD  E3 80 90 "KoiKatuChara" E3 80 90 05 "0.0.0"
0x00019619  89 50 4E 47 0D 0A 1A 0A          又一张完整 PNG（240x320）
0x00034498  E5 00 00 00                      int32 LE = 229 ← lstInfo 块表长度（214168）
0x0003449C  81 A7 "lstInfo" 95 ...            5 个条目（payload 229 字节）
0x00034581  DF 84 6F 07                      int32 LE = 124 929 247 ← 主数据块长度（214401）
0x00034585  00 00 00 00 01 06 00 00          主数据块 payload 起点（= 214405）
0x0003458D  DE 00 27 ...                      msgpack map16(39) 主数据开始
...
0x000477C0  ...                               ← UAR 插件键 #1（内嵌角色的 info）
...
0x0772ECA3  00 00 00 00                      ← 场景 ExtSave 块头（124972195）
0x0772ECA7  0D                               '0x0D'
0x0772ECA8  E3 80 90 "KStudio" E3 80 90        【KStudio】（124972200）
0x0772ECB7  04                               '0x04'
0x0772ECB8  "KKEx"
0x0772ECBC  03                               版本 '3'
0x0772ECBD  00 00 00 A6 AE                  int32 BE = 42670
0x0772ECC1  DE 00 23 ...                     msgpack map16(35) 插件字典
0x0772ECC7  D9 2A "com.bepis..."               ← UAR 插件键 #2（124972231，场景 mapInfo/filterInfo）
```

**场景卡 lstInfo 块表实测内容**（`[int32 LE size][msgpack payload]`）：

| name | version | pos | size |
| --- | --- | --- | --- |
| `KKEx` | `3` | `0x013232` (78642) | `0x076E52AD` (124670637) |
| `Custom` | `0.0.0` | `0` | `0x0E82` (3714) |
| `Coordinate` | `0.0.0` | `0x0E82` (3714) | `0x011E27` (73255) |
| `Parameter` | `0.0.5` | `0x012CA9` (76969) | `0x0243` (579) |
| `Status` | `0.0.0` | `0x012EEC` (77548) | `0x0346DF` (214751) |

> 场景卡的 `pos` 同样是「块流顺序偏移」：`KKEx` 的 `pos` = 78642，但场景 ExtSave
> 实际在文件偏移 **124 972 195**。差了 124 MB —— **绝对不能用 `pos` 直接 seek 文件**。

**与角色卡的差别（重要）**：
- 场景卡**可以有多张 PNG**（缩略图 + 贴图），PNG#1 之后不是卡头而是 `0x07 + "1.0.4.2"` 场景头；
- `lstInfo` 块表存在，但**不在文件开头**（实测在 214164 附近）；
- **`KStudio` 标记在文件尾部**（实测 124972203，距文件尾 44633 字节），不是头部；
- 场景卡**同时包含内嵌角色数据**，所以文件里通常还有一份 `【KoiKatuChara】` 和它的 `info` UAR 键。

---

## 2. 为什么不能「按块表跳转」

实测统计（每个文件都记录了卡类型标记偏移与 UAR 命中偏移，此处只列汇总）：

| 目录 | 文件数 | 卡类型标记最大偏移 | UAR 命中最大首偏移 | UAR 命中距文件尾最大距离 |
| --- | --- | --- | --- | --- |
| `chara\female` | 3 | 148 826 | 346 028 | — |
| `studio\scene` | 40 | 1 733 681 | **40 010 757** | **253 405 314** |
| `coordinate` | 599 | 3 122 386 | 3 133 723 | 264 269 659 |

→ UAR 数据可以出现在文件**任意位置**（场景卡内嵌角色的 ExtSave 块甚至在前 1/10 处）。
「只读头部 + 尾部 N MB」的方案会漏数据，**必须全文件扫描**。

---

## 3. UAR 插件键的字节编码（实测 hexdump）

UniversalAutoResolver 的数据挂在 ExtSave 的插件字典里，键是**长度已知的 ASCII 字符串**，
msgpack 编码为 `D9 <len> <key bytes>`（`0xD9` = str8）：

- 新键 `com.bepis.sideloader.universalautoresolver` → `D9 2A` + 42 字节（**0x2A = 42**）
- 旧键 `EC.Core.Sideloader.UniversalAutoResolver` → `D9 24` + 36 字节（**0x24 = 36**）

### 3.1 角色卡实测（偏移 83090）

```
0001448A  74 61 C0 DE 00 1E D9 2A 63 6F 6D 2E 62 65 70 69  |ta.....*com.bepi|
0001449A  73 2E 73 69 64 65 6C 6F 61 64 65 72 2E 75 6E 69  |s.sideloader.uni|
000144AA  76 65 72 73 61 6C 61 75 74 6F 72 65 73 6F 6C 76  |versalautoresolv|
000144BA  65 72 92 00 81 A4 69 6E 66 6F DC 00 35 C4 7D 88  |er....info..5.}.|
000144CA  A5 4D 6F 64 49 44 B0 4D 6F 64 69 66 69 65 64 20  |.ModID.Modified |
000144DA  68 65 61 64 6D 6F 64 A4 53 6C 6F 74 01 A9 4C 6F  |headmod.Slot..Lo|
000144EA  63 61 6C 53 6C 6F 74 CE 05 F7 4B 74 A8 50 72 6F  |calSlot...Kt.Pro|
000144FA  70 65 72 74 79 B2 43 68 61 46 69 6C 65 46 61 63  |perty.ChaFileFac|
```

逐字节解读：

| 字节 | 含义 |
| --- | --- |
| `D9 2A` | str8，长度 42 |
| `"com.bepis.sideloader.universalautoresolver"` | 插件键 |
| `92` | fixarray(2) — UAR 的值是 `[版本号, data]` |
| `00` | int 0（数据版本） |
| `81` | fixmap(1) — PluginData.data |
| `A4 "info"` | fixstr(4) 键 `"info"` |
| `DC 00 35` | array16(53) — 53 条 ResolveInfo |
| `C4 7D` | bin8，长度 125 — 第 1 条 ResolveInfo 的序列化字节 |
| `88` | fixmap(8) — ResolveInfo 本体是 8 键 map |

### 3.2 服装卡实测（偏移 164559）

```
000282C7  00 21 83 C1 03 89 D9 2A 63 6F 6D 2E 62 65 70 69  |.!.....*com.bepi|
000282F7  65 72 92 00 81 A4 69 6E 66 6F 99 C4 A9 88 A5 4D  |er....info.....M|
00028307  6F 64 49 44 BE 5B E7 BA B8 E9 B8 A2 5D E6 B5 B7  |odID.[......]...|
00028317  E4 BC A6 2D E6 98 9F E5 A4 9C E5 8D 8E E5 B0 94  |...-............|
```

`92 00 81 A4 "info" 99 C4 A9 88 ...` → `99` = fixarray(**9**) 条 ResolveInfo，
`C4 A9` = bin8 长度 169。GUID 是 `[纸鸢]海伦-星夜华尔兹`（UTF-8 中文，hexdump 里能看到
`E7 BA B8 E9 B8 A2` = "纸鸢"、"E6 B5 B7 E4 BC A6" = "海伦"）。

> **中文/日文 GUID 是正常 UTF-8**，控制台显示乱码只是 GBK 终端渲染问题，库内部按 UTF-8 解码，数据正确。
> 实测有效样例：`[纸鸢]耀嘉音`、`[Hatoko]ひがしRobin用`、`[Hatoko]ひがし茜特拉莉手指脚趾`、`知更鸟`。

### 3.3 场景卡实测（偏移 124972231）

```
0772ECBF  AE 00 00 DE 00 23 D9 2A 63 6F 6D 2E 62 65 70 69  |.....#.*com.bepi|
0772ECEF  65 72 92 00 88 AB 6D 61 70 49 6E 66 6F 47 55 49  |er....mapInfoGUI|
0772ECFF  44 D9 20 63 6F 6D 2E 44 65 61 74 68 57 65 61 73  |D. com.DeathWeas|
0772ED0F  65 6C 2E 4D 61 70 2E 53 61 6B 75 72 61 27 73 52  |el.Map.Sakura'sR|
0772ED1F  6F 6F 6D AD 6D 61 70 49 6E 66 6F 41 75 74 68 6F  |oom.mapInfoAutho|
```

`92 00 88 ...` → `88` = fixmap(**8**)，8 个键全是**纯字符串**：

| 键 | 实测值 |
| --- | --- |
| `mapInfoGUID` | `com.DeathWeasel.Map.Sakura'sRoom` |
| `mapInfoAuthor` | `DeathWeasel` |
| `mapInfoWebsite` | `https://www.patreon.com/DeathWeasel` |
| `mapInfoName` | `Map - Sakura's Room` |
| `filterInfoGUID` | `com.DeathWeasel.NormalLUT` |
| `filterInfoAuthor` | `DeathWeasel` |
| `filterInfoWebsite` | `https://mega.nz/#F!...` |
| `filterInfoName` | `Normal LUT` |

**注意场景卡的 UAR 值里没有 `info`/`itemInfo` 这些数组键，只有 GUID 字符串。**
场景卡内嵌角色的 `info` 在另一个 UAR 键里（同一文件的偏移 292800）。

---

## 4. ResolveInfo 二进制布局

`ResolveInfo.Serialize()` = `MessagePackSerializer.Serialize(this)`，产出的 byte[] 被包进
`bin8`（`C4 <len>`）或 `bin16`（`C5 <len:2>`）里，再放进 `array16`。**每个 bin 内容是一个
字符串键的 msgpack map**，实测 8 键：

角色卡 / 服装卡（`ResolveInfo`，`Core.UAR.ResolveInfo.cs`）：

| 键 | 类型 | 实测样例 |
| --- | --- | --- |
| `ModID` | string | `Modified headmod`（**这就是 zipmod GUID**） |
| `Slot` | int | `01` → 1 |
| `LocalSlot` | int | `CE 05 F7 4B 74` → 100 092 788 |
| `Property` | string | `ChaFileFace.headId` |
| `CategoryNo` | int（枚举） | `64` → 100 |
| `Author` | string | `i` |
| `Website` | string | `""` |
| `Name` | string | `Modified headmod` |

Studio 场景卡（`StudioResolveInfo`，`Core.UAR.Studio.StudioResolveInfo.cs`）11 键：
`ModID`(string)、`Slot`(int)、`LocalSlot`(int)、`SceneDicKey`(int)、`SceneObjectOrder`(int)、
`ResolveItem`(bool)、`Group`(int)、`Category`(int)、`Author`(string)、`Website`(string)、`Name`(string)。

> 两个类的 `ModID` setter 都是 `value?.Trim()`，所以解析后要 `Trim()`。
> 旧卡（BepInEx 4 时代）的 `info` 值可能是 `object[]` 而不是 `List<byte[]>`，
> 库对两种都做了兼容（见 `CardScanner.ReadResolveInfoArray`）。

---

## 5. UAR 子键实测统计（`--keys`，644 个文件）

| 卡类型 | 文件数 | `info` | `itemInfo` | `patternInfo` | `mapInfoGUID` | `filterInfoGUID` | `rampInfoGUID` |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 角色卡 | 3 | 3 | 0 | 0 | 0 | 0 | 0 |
| 场景卡 | 40 | 36 | 19 | **0** | 4 | 20 | 8 |
| 服装卡 | 599 | 467 | 0 | 0 | 0 | 0 | 0 |

- 角色卡 / 服装卡**只有 `info`**；
- 场景卡同时有 `info`（内嵌角色）+ `itemInfo`（道具/配件）+ `mapInfoGUID`（地图）+ `filterInfoGUID`（滤镜）+ `rampInfoGUID`（shader ramp）；
- **`patternInfo` 在 40 张实测场景卡里一次都没出现** —— 源码支持（`Core.UAR.Studio.Hooks.cs:267`），实机数据未使用。库仍然解析它，以防未来出现。

---

## 6. 形状校验：为什么必须做

扫描器只做「字节匹配」，命中之后必须验证**值是不是合法的 UAR 结构**。校验规则（已实现）：

- 值是 `array`（`fixarray`/`array16`/`array32`）且至少含一个 `map`，**或**
- 值是 `map` 且至少含一个已知 UAR 子键（`info`/`itemInfo`/`patternInfo`/`mapInfoGUID`/`filterInfoGUID`/`rampInfoGUID`）

否则丢弃该命中。

### 6.1 开发期真实踩到的坑（值得记下）

调试期间 `--raw` 曾显示 `2026_0702_1744_21_318.png` 的 11 个命中里有 7 个"值"是 `int 99`、
1 个是 `array(7)[int 44, "LocalSlot", ...]`，看起来像误报。**实际上那是扫描器的 1 字节偏移 bug**：
分块读时命中偏移写成 `position + p` 而正确值是 `position - carried + p`，导致读到的"值"从
键名中间开始。修掉偏移 bug 后同一文件的 11 个命中**全部是真 UAR 条目**：

```
@   307282  value: 92 00 81 A4 "info" 90        ← {"info": []} 空列表
@   577629  value: 92 00 81 A4 "info" 90        ← 同上
...  (共 10 个空 info)
@  2464948  value: 92 00 85 A8 "itemInfo" DC 00 1E C4 C4 ...  ← {"itemInfo": array16(30)}
```

→ 该卡真实结果 **13 个 GUID**（全部来自最后那个 `itemInfo`），前 10 个 `info` 是场景里
10 个内嵌角色、都没用角色 mod。

**教训**：解析器输出异常时，先怀疑自己的偏移算术，再怀疑数据。

### 6.2 形状校验仍然必要

它防的是**截断 / 损坏 / 伪造**文件。实测：造一个「PNG 签名 + UAR 键 + 7 字节垃圾」的文件，
不加校验会抛异常或读出垃圾，加了校验返回 `ok=True, guids=0`（1 ms）。
对 644 个真实卡片，修掉偏移 bug 后**未观察到任何误报命中**。


---

## 7. 性能与扫描策略

### 7.1 实测耗时（warm cache，重复扫描取稳定值）

| 文件 | 大小 | 耗时 | GUID 数 |
| --- | --- | --- | --- |
| `chara\female\2026-09-24-..._阿格莱亚_Female.png` | 47.6 MB | **47.2 ms** | 18 |
| `studio\scene\2026_0614_1349_59_706.png` | 125.0 MB | **127.7 ms** | 20 |
| `studio\scene\2026_0705_2153_24_005.png` | 1.48 MB | **1.6 ms** | 6 |

（注：同一台机器上重复跑会有 ±10% 波动，以上为 3 次取中位数。）

→ 耗时**线性于文件大小**，有效吞吐约 **1 GB/s**。瓶颈是**全文件字节扫描**，不是 msgpack 解析
（msgpack 跳过是纯 seek，47 MB 的插件 blob 几乎零成本）。

### 7.2 全量实测

- 45 个文件（female 3 + male 2 + scene 40）：**1004 guid references，300 distinct，3679 ms，平均 81.8 ms，最慢 455 ms**。
- 644 个文件（上述 + coordinate 599）：**3054 guid references，782 distinct，22 366 ms**，其中 **502** 个文件引用了 mod。
- 599 张服装卡单独跑：**461 张有 mod，15 108 ms，平均 25.2 ms**。

### 7.3 优化点（已实现）

- 1 MiB 分块读 + 64 字节 carry（跨块匹配）；
- 热循环只判 `buffer[p-2] == 0xD9` 且 `buffer[p-1] ∈ {0x2A, 0x24}`，再做逐字节比较；
- 卡类型标记复用同一个 `'K'`(0x4B) 首字节过滤，一趟扫描同时收 UAR 偏移和卡类型；
- `BufferedFileSource` 用 `FileShare.ReadWrite`，游戏正在写卡时也能读。

---

## 8. 交叉验证

### 8.1 与已安装 zipmod manifest 对照

用 PowerShell 打开 `<KoikatsuDir>\mods` 下所有 `*.zipmod`（本质是 zip），读内部
`manifest.xml` 的 `<guid>`，得到 **14 979 个 zipmod 文件 / 13 421 个 distinct GUID / 12 980 个 distinct name**。

对 644 张卡解析出的 GUID 做匹配：

| 指标 | 数值 |
| --- | --- |
| 卡片 GUID 引用总数 | 3 054 |
| 去重 GUID | 782 |
| **在已安装 manifest 中找到** | **3 038（99.48%）** |
| 未安装 | 16 次引用 / 13 个 distinct |

未安装的 13 个（核对过 manifest，确认是真缺 mod，不是解析错误）：
`Baizhu`、`Phantom.GKIMValentine2025`、`Phantom.GKMwork`、`Phantom.humessr2`、
`Phantom.lilijaSSR4`、`Phantom.liliyaChris`、`Phantom.liliyaSSR1`、`Phantom.temariC1`、
`Phantom.temariSSR2`、`RikkiBalboa.TentacleStuff`、`[B1li]Elsa`、`[Liangpi]YuTian_Body_7`、`sa1.0`。

> **坑**：`manifest.xml` 是 XML，GUID 里的 `&` 被转义成 `&amp;`。提取脚本必须先做
> XML 实体解码（`&amp;` `&lt;` `&gt;` `&quot;` `&apos;`），否则 `n3x7xp.bottom&pantiesg01`
> 会被误判成「未安装」。

### 8.2 已知真值

- 阿格莱亚角色卡 → **18 个 GUID**（全部 1x 引用）：
  `BMANN.Bandages`、`Modified headmod`、`[Makise]Aglaea`、`[纸鸢]耀嘉音`、`cbr95810_ph`、
  `com.DeathWeasel.CenteredBodyPaintLayout`、`com.DeathWeasel.Panties`、`com.gf.clothes`、
  `com.nep.ryounastuff`、`kks.backport.clothes`、`moderchan.CharamakerSupportMOD`、
  `n3x7xp.brag02`、`xne_brzlswim`、`xne_egyptianset`、`yamadamod.nohairB`、`yanmod.charamake`、
  `yanmod.nipples`、`ztokki.UnderwearPasties`
- 场景卡 `2026_0614_1349_59_706` → **20 个**：18 个来自内嵌角色（`info`）+ 地图
  `com.DeathWeasel.Map.Sakura'sRoom` + 滤镜 `com.DeathWeasel.NormalLUT`；内嵌角色 GUID 含
  日文/中文：`[Hatoko]ひがしRobin用`、`知更鸟`、`[Makise]Robin`
- 场景卡 `2026_0706_0033_13_789` → **61 个**（最多）
- 服装卡 `coordinate\1.png` → **1 个**：`[纸鸢]海伦-星夜华尔兹`

> 上面是 UTF-8 正确显示。用 GBK 控制台直接看会变成 `[绾搁涪]鑰€鍢夐煶` 这类乱码 ——
> **只是终端渲染问题**，库内部按 UTF-8 解码，数据正确。查看时先
> `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`。

---

## 9. 健壮性实测（12 类坏输入，全部不抛异常、不挂起）

| 输入 | 结果 |
| --- | --- |
| 文件不存在 | `ok=False` `File not found: ...`（0 ms） |
| 空文件 | `ok=False` `File is too small to be a PNG card (0 bytes).` |
| 2 字节文件 | 同上 |
| 只有 PNG 签名 | `ok=True`，0 GUID（1 ms） |
| 文本文件改名 .png | `ok=False` `File does not start with the PNG signature.` |
| PNG 签名 + 1 MiB 随机字节 | `ok=True`，0 GUID（3 ms） |
| PNG 签名 + UAR 键 + 7 字节垃圾 | `ok=True`，0 GUID（1 ms，形状校验生效） |
| 47 MB 真卡截断到 1% / 10% / 50% / 99% | 全部 `ok=True`，18 GUID（2 / 5 / 24 / 45 ms） |
| 目录路径 | `ok=False` `File not found: ...` |

`TryScan` 内部所有解析都有上限保护（数组元素 ≤ 65536、map 条目 ≤ 256、递归深度 ≤ 64），
`catch` 兜底返回 `false` + 异常类型和消息，`finally` 释放文件句柄。

---

## 10. 库 API

```csharp
namespace KKLazyZipmods.CardScan
{
    public static class CardScanner
    {
        public const string UarPluginKey       = "com.bepis.sideloader.universalautoresolver";
        public const string UarPluginKeyLegacy = "EC.Core.Sideloader.UniversalAutoResolver";

        // 主 API：只拿 GUID 列表（去重 + Ordinal 排序）
        public static bool TryGetRequiredGuids(string filePath, out List<string> guids, out string error);

        // 扩展 API：还给出卡类型、插件键、每条 GUID 的引用次数、耗时
        public static bool TryScan(string filePath, out CardScanResult result, out string error);
    }

    public enum CardKind { Unknown = 0, Character = 1, StudioScene = 2, Coordinate = 3 }

    public sealed class ModReference   { string Guid; string PluginKey; int ResolveInfoCount; }
    public sealed class CardScanResult { CardKind Kind; long FileLength; List<string> PluginKeys;
                                         List<ModReference> References; List<string> Guids;
                                         long ElapsedMilliseconds; }
}
```

**语义**：

- 文件不是 PNG / 读不了 → `false` + `error`；
- 是合法卡但没引用任何 mod → **`true` + 空列表**（正常情况，不是错误）；
- 永远不抛异常。

---

## 11. 复现方式

```powershell
# 1) net35 真实编译验证（csc 3.5）
powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\cardscan\_net35.ps1

# 2) net8 验证程序
powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\cardscan\_build.ps1
<repo>\tools\cardscan\CardScanProbe\bin\Release\net8.0\CardScanProbe.exe
```

验证程序子命令：`--debug <file>`（布局 + 命中偏移 + hexdump）、`--raw <file>`（原始命中 + 值前 24 字节）、
`--details <file>`（完整 GUID 列表）、`--diag <dir>`、`--keys <dir>`、`--robust <tmpdir>`、
`--markers <dir>`、`--offsets <dir>`、`--depth <dir>`、`--window <dir> <headMiB> <tailMiB>`、
`--crosscheck <modGuidsFile> <roots...>`。

---

## 12. 待确认 / 未覆盖

- `patternInfo` 实测 0 次（源码有，实机未见）。
- 旧版 UAR 键 `EC.Core.Sideloader.UniversalAutoResolver` 在 644 张实测卡里**一次都没出现**
  （全部是新键），代码里保留兼容分支但**未经真实旧卡验证**。
- 未测试非 PNG 格式的卡（恋活也有 `.jpg` 卡？未在 `<KoikatsuDir>` 中发现）。
- 卡内 KKEx 版本 `3` 的完整语义未解析（本库不需要，只取 UAR）。
