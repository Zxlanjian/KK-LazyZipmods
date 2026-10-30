# Studio 物品列表运行时注入 — 精确配方（Q1–Q8）

> 全部结论来自 Cecil 反编译 `<KoikatsuDir>\Koikatu_Data\Managed\Assembly-CSharp.dll`（types=1924）与只读复核 `<repo>\src\`。
> 工具：`<repo>\tools\ilscan\bin\Release\net8.0\ilscan.exe`（命令 `types/methods/body/callers/callees/fields/fieldrefs/strlit/patch`）。
> 本文**未修改** `source\` 下任何文件。
>
> **Note on paths.** These notes were written against a specific local install, so absolute paths
> have been replaced with placeholders: `<KoikatsuDir>` is the game folder (the one containing
> `Koikatu_Data\`), `<repo>` is this repository, `<upstream>` is the BepisPlugins checkout this fork
> is based on, and `<work>` is any scratch build directory. File names, offsets, byte counts and IL
> are reproduced verbatim from the actual disassembly.

---

## Q1. `Studio.Info` 实例字段全清单

`Studio.Info : Singleton<Studio.Info>`。以下为**全部**实例字段（`ilscan ac fields "^Studio\.Info::"`，已剔除 `<>f__am$cache0..10` / `<>f__mg$cache0` 等 static lambda 缓存）：

| 字段名 | 类型 |
|---|---|
| `dicBoneInfo` | `Dictionary<Int32, Studio.Info/BoneInfo>` |
| `dicItemGroupCategory` | `Dictionary<Int32, Studio.Info/GroupInfo>` |
| `dicItemLoadInfo` | `Dictionary<Int32, Dictionary<Int32, Dictionary<Int32, Studio.Info/ItemLoadInfo>>>` |
| `dicAccessoryGroup` | `Dictionary<Int32, String>` |
| `dicAccessoryPointInfo` | `Dictionary<Int32, Studio.Info/AccessoryPointInfo>` |
| `m_AccessoryPoint` | `ExcelData` |
| `_accessoryPointNum` | `Int32` |
| `m_AccessoryPointGroup` | `ExcelData` |
| `dicLightLoadInfo` | `Dictionary<Int32, Studio.Info/LightLoadInfo>` |
| `dicAGroupCategory` | `Dictionary<Int32, Studio.Info/GroupInfo>` |
| `dicAnimeLoadInfo` | `Dictionary<Int32, Dictionary<Int32, Dictionary<Int32, Studio.Info/AnimeLoadInfo>>>` |
| `dicHandAnime` | `Dictionary<Int32, Studio.Info/HandAnimeInfo>[]`（长度 2，`.ctor` @IL_0060 `ldc.i4.2` + `newarr`） |
| `dicVoiceGroupCategory` | `Dictionary<Int32, Studio.Info/GroupInfo>` |
| `dicVoiceLoadInfo` | `Dictionary<Int32, Dictionary<Int32, Dictionary<Int32, Studio.Info/LoadCommonInfo>>>` |
| `dicBGMLoadInfo` | `Dictionary<Int32, Studio.Info/LoadCommonInfo>` |
| `dicENVLoadInfo` | `Dictionary<Int32, Studio.Info/LoadCommonInfo>` |
| `dicMapLoadInfo` | `Dictionary<Int32, Studio.Info/MapLoadInfo>` |
| `dicFilterLoadInfo` | `Dictionary<Int32, Studio.Info/LoadCommonInfo>` |
| `fileCheck` | `Studio.Info/FileCheck` |
| `waitTime` | `Studio.Info/WaitTime` |
| `<isLoadList>k__BackingField` | `Boolean`（属性 `isLoadList`，getter/setter 各 1 个，**无其它写入点**：`ilscan ac fieldrefs isLoadList` → refs=2，只在 `get_isLoadList`/`set_isLoadList` 里） |

### 你点名的 6 个

- **`isLoadList`** → 自动属性，backing field `System.Boolean <isLoadList>k__BackingField`。
- **`dicItemLoadInfo`** → `Dictionary<Int32, Dictionary<Int32, Dictionary<Int32, Studio.Info/ItemLoadInfo>>>`。三层 = `[group][category][no]`。
- **`dicItemGroupCategory`** → `Dictionary<Int32, Studio.Info/GroupInfo>`，key = **group no**。
- **`fileListInfo` 字段不存在**。`FileListInfo` 是 `<LoadExcelDataCoroutine>c__Iterator0` 的**局部变量** `<fli>__0`（IL_01F5 `newobj` → `stfld <fli>__0`），不是 `Studio.Info` 的成员。`FileListInfo` 只有 1 个字段：`Dictionary<String, String[]> dicFile`。
- **`fileCheck`** → `Studio.Info/FileCheck`（实例字段，`<LoadExcelDataCoroutine>` @IL_0084 赋值，@IL_0AC9 置 null）。`FileCheck` 只有 1 个字段：`Dictionary<String, Boolean> dicConfirmed`。
- **`waitTime`** → `Studio.Info/WaitTime`（实例字段，@IL_0094 赋值，@IL_0AD6 置 null）。`WaitTime` 字段：`static Single intervalTime`、`Single nextFrameTime`；方法 `get_isOver()`、`Next()`。

### 嵌套类型字段（写代码要按这个顺序摆列）

```
Studio.Info/FileInfo        { String manifest; String bundlePath; String fileName; }
Studio.Info/LoadCommonInfo  : FileInfo { String name; }
Studio.Info/ItemLoadInfo    : LoadCommonInfo {
    String childRoot; Boolean isAnime; Boolean[] color; Boolean[] pattren;
    Boolean isScale; Boolean isEmission; Boolean isGlass; List<String> bones; }
Studio.Info/GroupInfo       { String name; Dictionary<Int32,String> dicCategory; }
Studio.Info/FileListInfo    { Dictionary<String,String[]> dicFile; }
Studio.Info/FileCheck       { Dictionary<String,Boolean> dicConfirmed; }
Studio.Info/WaitTime        { static Single intervalTime; Single nextFrameTime; }
```

`ItemLoadInfo::.ctor(List<String> _lst)` 的列索引（`ilscan ac body`）：`[3]`→`name`、`[4]`→`manifest`、`[5]`→`bundlePath`、`[6]`→`fileName`、`[7]`→`childRoot`、`[8]`→`isAnime`、`[9]/[11]/[13]`→`color[0..2]`（后续还有 pattren/isScale/isEmission/isGlass/bones）。

---

## Q2. `<LoadExcelDataCoroutine>c__Iterator0::MoveNext` 完整序列

### 阶段 A：守卫 + 重置（IL_0069–IL_014D）

```
IL_0069  $this
IL_006F  call Studio.Info::get_isLoadList()
IL_0074  brfalse IL_007E      // isLoadList == false 才继续
IL_0079  br IL_0AEE           // 已加载过 → 直接结束
IL_0084  newobj Studio.Info/FileCheck  → stfld Studio.Info::fileCheck
IL_0094  newobj Studio.Info/WaitTime   → stfld Studio.Info::waitTime
```

然后**按序 Clear 11 个字典**（逐条 IL 已核）：

| # | IL | 字段 |
|---|---|---|
| 1 | IL_00A4 | `dicBoneInfo` |
| 2 | IL_00B4 | `dicItemGroupCategory` |
| 3 | IL_00C4 | `dicItemLoadInfo` |
| 4 | IL_00D4 | `dicLightLoadInfo` |
| 5 | IL_00E4 | `dicAGroupCategory` |
| 6 | IL_00F4 | `dicAnimeLoadInfo` |
| 7 | IL_0104 + `ldc.i4.0 ldelem` | `dicHandAnime[0]` |
| 8 | IL_0116 + `ldc.i4.1 ldelem` | `dicHandAnime[1]` |
| 9 | IL_0128 | `dicVoiceGroupCategory` |
| 10 | IL_0138 | `dicVoiceLoadInfo` |
| 11 | IL_0148 | `dicFilterLoadInfo` |

> 注意：`dicMapLoadInfo`、`dicBGMLoadInfo`、`dicENVLoadInfo`、`dicAccessoryGroup`、`dicAccessoryPointInfo` **不在 Clear 列表里**（它们在 `.ctor` 里 `new`，本协程不重置）。

### 阶段 B：读路径列表 + 建 FileListInfo（IL_0192–IL_01FA）

```
IL_0193  ldstr "studio/info/"
IL_0198  ldc.i4.1                                            // subdirCheck = true
IL_0199  call CommonLib::GetAssetBundleNameListFromPath(String path, Boolean subdirCheck)
IL_019E  stfld <pathList>__0
IL_01A9  List<String>::Sort()
IL_01F5  newobj Studio.Info/FileListInfo::.ctor(List<String> _list)
IL_01FA  stfld <fli>__0        // 局部变量，不是字段
```

`CommonLib::GetAssetBundleNameListFromPath` body：`AssetBundleCheck::get_IsSimulation()` 为 true 则返回空 List；否则 `AssetBundleManager::get_BaseDownloadingURL() + path` → `Directory.Exists` 否则返回空 → `Directory.GetFiles(dir, "*.unity3d", SearchOption.AllDirectories)`（`subdirCheck=true` 时）→ `Select(Path::GetFileNameWithoutExtension)` → `ToList()`。
**Sideloader 的 Postfix（`Core_Sideloader_Studio/Core.Sideloader.Studio.Hooks.cs:138`）在 `path == "studio/info/"` 时把所有 `Lists.ExternalStudioDataList.Keys` 追加进 `__result`** —— 这就是懒加载能从这里「加塞」的唯一入口。

`FileListInfo::.ctor(List<String> _list)` body：
```
IL_0007  newobj Dictionary<String,String[]>() → stfld dicFile
loop:  dicFile.Add(bundlePath, Studio.AssetBundleCheck::GetAllFileName(bundlePath, false))
```
即 **每个 bundle 调一次 `GetAllFileName` 并把结果快照进 `dicFile`**。Sideloader 的 Prefix（`Studio.Hooks.cs:110`）在命中 `ExternalStudioDataList` 时直接返回 `FileNameWithoutExtension.ToLower()` 数组并 `return false`。

### 阶段 C：逐 bundle 循环（IL_0206 起，`<i>__1` 遍历 `<pathList>__0`）

每个 bundle：
```
IL_0229  Path::GetFileNameWithoutExtension(bundlePath) → <fileName>__2
```

然后**按固定顺序**做 19 组加载。`✅` = 有 `FileListInfo::Check(bundlePath, 前缀+fileName)` 前置守卫；`⛔` = **没有**守卫（无条件调用）。

| 顺序 | IL | 前缀 / 正则字面量 | 调用 | 守卫 | 写入目标 |
|---|---|---|---|---|---|
| 1 | IL_023F / 026B | `"AccessoryPointGroup_"` | `LoadExcelData` → `LoadAccessoryGroupInfo(ExcelData)` | ✅ IL_024F | `dicAccessoryGroup` |
| 2 | IL_0291 / 02BD | `"AccessoryPoint_"` | `LoadExcelData` → `LoadAccessoryPointInfo(ExcelData)` | ✅ IL_02A1 | `dicAccessoryPointInfo` / `m_AccessoryPoint` |
| 3 | IL_02E3 / 030F | `"Bone_"` | `LoadExcelData` → `LoadBoneInfo(ExcelData, dicBoneInfo)` | ✅ IL_02F3 | `dicBoneInfo` |
| 4 | IL_0380 | `"HandAnime_(\d*)_(\d*)"` | `LoadHandAnimeInfo(String, String, Dictionary<int,HandAnimeInfo>[])` | ⛔ | `dicHandAnime[0..1]` |
| 5 | IL_03E1 / 040D | `"ItemGroup_"` | `LoadExcelData` → **`LoadAnimeGroupInfo(ExcelData, dicItemGroupCategory)`** | ✅ IL_03F1 | **`dicItemGroupCategory`** |
| 6 | IL_047E | `"ItemCategory_(\d*)_(\d*)"` | **`LoadAnimeCategoryInfo(String, String, dicItemGroupCategory)`** | ⛔ | **`dicItemGroupCategory[*].dicCategory`** |
| 7 | IL_04E0 | `"ItemList_(\d*)_(\d*)_(\d*)"` | **`LoadItemLoadInfoCoroutine(String, String)`**（yield 子协程） | ⛔ | **`dicItemLoadInfo`** |
| 8 | IL_050F | `"ItemBoneList_(\d*)_(\d*)"` | `LoadItemBoneInfo(String, String)` | ⛔ | `dicItemLoadInfo[*][*][*].bones` |
| 9 | IL_0565 / 0591 | `"Light_"` | `LoadExcelData` → `LoadLightLoadInfo(ExcelData)` | ✅ IL_0575 | `dicLightLoadInfo` |
| 10 | IL_05F8 / 0624 | `"AnimeGroup_"` | `LoadExcelData` → `LoadAnimeGroupInfo(ExcelData, dicAGroupCategory)` | ✅ IL_0608 | `dicAGroupCategory` |
| 11 | IL_0696 | `"AnimeCategory_(\d*)_(\d*)"` | `LoadAnimeCategoryInfo(String, String, dicAGroupCategory)` | ⛔ | `dicAGroupCategory` |
| 12 | IL_06F9 | `"Anime_(\d*)_(\d*)_(\d*)"` | `LoadAnimeLoadInfoCoroutine(..., dicAnimeLoadInfo, LoadAnimeLoadInfo)` | ⛔ | `dicAnimeLoadInfo` |
| 13 | IL_0746 | `"HAnime_(\d*)_(\d*)_(\d*)"` | `LoadAnimeLoadInfoCoroutine(..., dicAnimeLoadInfo, LoadHAnimeLoadInfo)` | ⛔ | `dicAnimeLoadInfo` |
| 14 | IL_0792 / 07BE | `"VoiceGroup_"` | `LoadExcelData` → `LoadAnimeGroupInfo(ExcelData, dicVoiceGroupCategory)` | ✅ IL_07A2 | `dicVoiceGroupCategory` |
| 15 | IL_0830 | `"VoiceCategory_(\d*)_(\d*)"` | `LoadAnimeCategoryInfo(String, String, dicVoiceGroupCategory)` | ⛔ | `dicVoiceGroupCategory` |
| 16 | IL_0893 | `"Voice_(\d*)_(\d*)_(\d*)"` | `LoadVoiceLoadInfoCoroutine(String, String)` | ⛔ | `dicVoiceLoadInfo` |
| 17 | IL_08C3 / 08EF | `"BGM_"` | `LoadExcelData` → `LoadSoundLoadInfo(ExcelData, dicBGMLoadInfo)` | ✅ IL_08D3 | `dicBGMLoadInfo` |
| 18 | IL_0961 / 098D | `"Map_"` | `LoadExcelData` → `LoadMapLoadInfo(ExcelData, dicMapLoadInfo)` | ✅ IL_0971 | `dicMapLoadInfo` |
| 19 | IL_09FF / 0A2B | `"Filter_"` | `LoadExcelData` → `LoadSoundLoadInfo(ExcelData, dicFilterLoadInfo)` | ✅ IL_0A0F | `dicFilterLoadInfo` |

每组之间都有 `waitTime.get_isOver()` / `waitTime.Next()` 让帧点（`brfalse` 跳过 yield）。

bundle 末尾：
```
IL_0A9A  AssetBundleManager::UnloadAssetBundle(bundlePath, true, null, false)
```
全部 bundle 结束后：
```
IL_0ACA  stfld Studio.Info::fileCheck = null
IL_0AD6  stfld Studio.Info::waitTime  = null
IL_0AE2  call Studio.Info::set_isLoadList(true)
```

### 哪些写 `dicItemLoadInfo` / `dicItemGroupCategory`（直接回答）

- **写 `dicItemLoadInfo`**：只有两处 —— 第 7 步 `LoadItemLoadInfoCoroutine` → `LoadItemLoadInfo`，和第 8 步 `LoadItemBoneInfo`（只补 `bones` 字段）。
- **写 `dicItemGroupCategory`**：只有两处 —— 第 5 步 `LoadAnimeGroupInfo`（建 group），第 6 步 `LoadAnimeCategoryInfo`（填 `GroupInfo.dicCategory`）。**顺序不可颠倒**（见 Q5）。

---

## Q3. `<LoadItemLoadInfoCoroutine>c__Iterator1::MoveNext` 完整分析

### 是否引用 `FileListInfo`？

**完全不引用。** `ilscan ac body` 全量符号统计，该 `MoveNext` 里出现的 `Studio.Info` 成员只有 4 个：

```
1 × Studio.Info::FindAllAssetName
1 × Studio.Info::LoadExcelData
1 × Studio.Info::LoadItemLoadInfo
2 × Studio.Info::waitTime
```

无 `FileListInfo`、无 `dicFile`、无 `fileCheck`、无 `isLoadList`。

### 两个参数各干什么

| 参数 | 用在哪 | 用途 |
|---|---|---|
| `String _bundlePath` | IL_002B（`FindAllAssetName`）、IL_025A（`LoadExcelData`） | 指定要扫哪个 asset bundle（即 `studio/info/` 下的某个 `*.unity3d` 名） |
| `String _regex` | IL_0031（`FindAllAssetName` 过滤）、IL_0057（`_regex.Split('_')[0].ToLower()` 得 `checkKey`）、IL_00C2（`Regex.Match(fileName, _regex, IgnoreCase)` 提 3 个捕获组） | 既做文件名过滤，又做字段解析模板 |

### 完整流程

```
IL_0036  FindAllAssetName(_bundlePath, _regex) → <files>__0
IL_0046  NullCheck::IsNullOrEmpty(<files>__0) → 空则 return
IL_0057  <checkKey>__0 = _regex.Split('_')[0].ToLower()      // 例如 "itemlist"
IL_0079  new SortedDictionary<int, SortedDictionary<int, SortedDictionary<int,string>>>() → <sortDic>__0
loop over <files>__0 (IL_0085..IL_019C):
    IL_009D  files[i].Split('_')[0].ToLower()
    IL_00AF  .CompareTo(<checkKey>__0) → 不等则跳过该文件
    IL_00C2  Regex::Match(files[i], _regex, IgnoreCase)
    IL_00DF  V_4 = int.Parse(Groups[1].Value)
    IL_00F7  V_5 = int.Parse(Groups[2].Value)
    IL_010F  V_6 = int.Parse(Groups[3].Value)
    IL_011C..  sortDic[V_5][V_6][V_4] = files[i]            // [group][category][no] = 文件名
三层 foreach（IL_01C6 / IL_0201 / IL_023C）:
    IL_0265  LoadExcelData(_bundlePath, item3.Value)
    IL_026F  LoadItemLoadInfo(ExcelData)
    IL_027A  waitTime.get_isOver()  → true 则 yield（PC=1）
    IL_02B1  waitTime.Next()
```

### 是否碰 `isLoadList` / `WaitTime`

- **`isLoadList`：完全不碰。**
- **`waitTime`：会碰，而且这是唯一的运行时依赖 —— 见下方「坑」。**

```
IL_0274  ldarg.0 / ldfld $this / ldfld Studio.Info::waitTime
IL_027F  callvirt Studio.Info/WaitTime::get_isOver()
IL_02AC  ldfld Studio.Info::waitTime / callvirt Studio.Info/WaitTime::Next()
```

⚠️ **`waitTime` 是 `null` 时这里直接 NRE。** 主协程在 IL_0094 才 `new`，在 IL_0AD6 又置 `null`。所以**单独调用 `LoadItemLoadInfoCoroutine` 之前必须确保 `Studio.Info.waitTime != null`**，否则第一轮迭代就抛 `NullReferenceException`。

`WaitTime` 语义（已反编译）：`get_isOver() => Time.realtimeSinceStartup >= nextFrameTime`；`Next() => nextFrameTime = Time.realtimeSinceStartup + 0.03f`。初始 `nextFrameTime == 0f` → 首次判定恒为 `true` → **第一个文件后必然 yield 一帧**，之后每 30 ms 让一帧。

---

## Q4. `Studio.Info::LoadItemLoadInfo(ExcelData _ed)`

签名：`private System.Void Studio.Info::LoadItemLoadInfo(ExcelData _ed)`

- **只写 `dicItemLoadInfo`。**
- **不读 `dicItemGroupCategory`**，也不读 `dicItemLoadInfo` 之外任何字典（`fieldrefs dicItemGroupCategory` 的 6 个引用点里没有它）。

body：

```
IL_0002  Object::op_Equality(_ed, null) → true 则 return
IL_0013  _ed.list.Skip(1)                    // 跳过表头行
每行:
  IL_0050  NullCheck::SafeGet(row, 0) → int.TryParse → V_2(no)；失败则 continue
  IL_0068  int.Parse(row[1]) → V_3 (group)
  IL_0075  int.Parse(row[2]) → V_4 (category)
  IL_0082  dicItemLoadInfo.ContainsKey(group)? 否 → IL_009E  dicItemLoadInfo[group] = new Dictionary<int, Dictionary<int, ItemLoadInfo>>()
  IL_00AF  dicItemLoadInfo[group].ContainsKey(category)? 否 → IL_00CE dicItemLoadInfo[group][category] = new Dictionary<int, ItemLoadInfo>()
  IL_00E6  dicItemLoadInfo[group][category][no] = new Studio.Info/ItemLoadInfo(row)   // 覆盖式 set_Item
```

**结论：新 group / 新 category 无需预注册，`LoadItemLoadInfo` 自己会建。** 列布局：`row[0]=no, row[1]=group, row[2]=category, row[3..]=ItemLoadInfo 各字段`。

⚠️ `row[1]`/`row[2]` 用 `int.Parse`（**不是 TryParse**），遇到非数字行会抛 `FormatException`。

---

## Q5. `LoadAnimeGroupInfo` / `LoadAnimeCategoryInfo`

### 签名（`ilscan ac methods "^Studio\.Info$"`）

```
private System.Void Studio.Info::LoadAnimeGroupInfo(ExcelData _ed, Dictionary<Int32, Studio.Info/GroupInfo> _dic)
private System.Void Studio.Info::LoadAnimeCategoryInfo(System.String _bundlePath, System.String _regex, Dictionary<Int32, Studio.Info/GroupInfo> _dic)
private System.Void Studio.Info::LoadAnimeCategoryInfo(ExcelData _ed, Studio.Info/GroupInfo _info)
```

`LoadAnimeGroupInfo` 名不副实 —— 它被 **ItemGroup_ / AnimeGroup_ / VoiceGroup_ 三处共用**（callers @IL_042D、@IL_0644、@IL_07DE）。

### `LoadAnimeGroupInfo(ExcelData, dic)` body

```
IL_0002  _ed == null → return
IL_0013  _ed.list.Skip(1)
每行:
  IL_0050  SafeGet(row,0) → int.TryParse → V_2(groupNo)；失败则 continue
  IL_0068  V_3 = row[1]  (组名)
  IL_006E  _dic.TryGetValue(groupNo, out V_4)
          命中 → IL_007D  V_4.name = V_3            // 只更新名字
          未命中 → IL_0087 new GroupInfo(); V_4.name = V_3; IL_009A _dic.Add(groupNo, V_4)
```

→ **只写 `_dic[groupNo].name`，不碰 `dicCategory`。**

### `LoadAnimeCategoryInfo(String, String, dic)` body

```
IL_0003  FindAllAssetName(_bundlePath, _regex) → V_0；空则 return
IL_0021  V_1 = _regex.Split('_')[0].ToLower()
loop over V_0:  // IL_0036..IL_00CE
    IL_0049  files[i].Split('_')[0].ToLower() CompareTo V_1 → 不等跳过
    IL_0064  Regex::Match(files[i], _regex, IgnoreCase)
    IL_0083  V_5 = int.Parse(Groups[1])
    IL_009C  V_6 = int.Parse(Groups[2])
    IL_009E.. sortDic[V_6][V_5] = files[i]              // [group][category] = 文件名
foreach sortDic (IL_00E4):
    IL_00F5  if (!_dic.ContainsKey(kv.Key)) → IL_015B 跳过整个 group      // ★ 关键守卫
    foreach 内层:
        IL_0125  LoadExcelData(_bundlePath, item.Value)
        IL_0137  LoadAnimeCategoryInfo(ExcelData, _dic[kv.Key])
```

### `LoadAnimeCategoryInfo(ExcelData, GroupInfo)` body

```
IL_0002  _ed == null → return
IL_0013  _ed.list.Skip(1)
每行:
  IL_0050  SafeGet(row,0) → int.TryParse → V_2(categoryNo)；失败跳过
  IL_0069  _info.dicCategory[categoryNo] = row[1]      // 覆盖式 set_Item
```

### 对新 item category 是否必需？—— 分两件事

| 目的 | 是否需要 | 原因 |
|---|---|---|
| **场景卡加载物件**（`AddObjectItem.Load`） | **不需要** | 它只读 `dicItemLoadInfo`（`ilscan ac fieldrefs dicItemLoadInfo` → `Studio.AddObjectItem::GetLoadInfo` @IL_0005）。 |
| **物品调色板 UI 能选到** | **需要** | `Studio.ItemGroupList::InitList()` @IL_005F 遍历 `dicItemGroupCategory`；`Studio.ItemCategoryList::InitList(group)` @IL_005F 直接 `dicItemGroupCategory[_group]`（`get_Item`，group 缺失 → **`KeyNotFoundException`**）。 |

**顺序硬约束**：`LoadAnimeCategoryInfo` 有 `if (!_dic.ContainsKey(group)) continue;` 守卫（IL_00F5），所以**必须先 `LoadAnimeGroupInfo` 建好 group，再 `LoadAnimeCategoryInfo` 填 category**，否则整个 group 被静默跳过。

> 这不是「只对动画需要」——`ItemGroup_` / `ItemCategory_` 走的是同一对方法。

---

## Q6. `FileListInfo::Check(path, file)` 的失败路径

### 用法

```
FileListInfo::.ctor(List<String> _list)
  → dicFile[bundlePath] = Studio.AssetBundleCheck::GetAllFileName(bundlePath, false)   // 快照

FileListInfo::Check(String _path, String _file)
  IL_0000  if (!AssetBundleCheck::get_IsSimulation()) _file = _file.ToLower()
  IL_001B  if (!dicFile.TryGetValue(_path, out V_0)) return false       // ← 新 bundle 不在快照 → false
  IL_0027  return Enumerable::Contains(V_0, _file)
```

`Check` 的**全部 10 个调用点**都在 `<LoadExcelDataCoroutine>c__Iterator0::MoveNext`（`ilscan ac callers` refs=10），全部形如：

```
IL_0233..IL_024F  FileListInfo::Check(<bundlePath>__2, "ItemGroup_" + <fileName>__2)
IL_0254           brfalse IL_0432          // false → 跳过 LoadExcelData
IL_041D           LoadExcelData(bundlePath, "ItemGroup_" + fileName)
IL_042D           LoadAnimeGroupInfo(...)
```

### 新 bundle 不在 `dicFile` 会怎样？

**该 bundle 的这 10 类目全部被跳过，但不抛异常、不报错、完全静默。**

被 `Check` 门控的 10 个前缀：`AccessoryPointGroup_`、`AccessoryPoint_`、`Bone_`、`ItemGroup_`、`Light_`、`AnimeGroup_`、`VoiceGroup_`、`BGM_`、`Map_`、`Filter_`。

**不被门控（无条件执行）的 9 个**：`HandAnime_`、`ItemCategory_`、`ItemList_`、`ItemBoneList_`、`AnimeCategory_`、`Anime_`、`HAnime_`、`VoiceCategory_`、`Voice_`。

→ **对你的懒加载：`ItemList_`（物品本体）和 `ItemCategory_` 恰好都不受 `FileListInfo` 影响。** 但 `ItemGroup_`（建 group）受门控 —— 所以如果你走「重跑整个 `LoadExcelDataCoroutine`」的路线，新 bundle 会因为不在 `dicFile` 快照里而丢掉 `ItemGroup_`，进而 `ItemCategory_` 因 group 缺失被 IL_00F5 跳过。

**规避办法（三选一）**：
1. **不要重跑整条 `LoadExcelDataCoroutine`**，只调 `LoadAnimeGroupInfo` + `LoadAnimeCategoryInfo` + `LoadItemLoadInfoCoroutine`（都不查 `FileListInfo`）。
2. 若必须重跑整条协程，把 `isLoadList` 置回 `false` 并保证 `CommonLib::GetAssetBundleNameListFromPath("studio/info/")` 返回的列表包含新 bundle（Sideloader Postfix 已经做了这件事，只要 `ExternalStudioDataList` 里有该 bundle），这样 `FileListInfo::.ctor` 会重新快照、包含新 bundle。
3. 反射往 `FileListInfo.dicFile` 里补条目 —— 但它是局部变量，外部拿不到，**不可行**。

### `Studio.Info/FileCheck::Check` 的失败路径（另一层缓存）

```
FileCheck::Check(String _path)
  IL_0001  NullCheck::IsNullOrEmpty(_path) → 空则 return
  IL_0016  dicConfirmed.TryGetValue(_path, out V) → 命中即返回缓存值（false 也会被缓存）
  IL_0022  if (!AssetBundleCheck::get_IsSimulation()) ...
  IL_0037  File::Exists(path) → result
  IL_0048  dicConfirmed.Add(_path, result)
```

`FileCheck::Check` 只有 **2 个调用者**：`Studio.Info::LoadAnimeLoadInfo` @IL_0059、`Studio.Info::LoadHAnimeLoadInfo` @IL_0059。→ 只影响动画类，**不影响物品**。Sideloader 的 Postfix（`Studio.Hooks.cs:154`）会在 `__result == false` 时用 `BundleManager.Bundles.TryGetValue` 兜底并写回 `dicConfirmed[_path] = true`。

---

## Q7. `Studio.Info::Awake` / 启动顺序 / `isLoadList` 翻转

### `Studio.Info` **没有 `Start()`**（`ilscan ac methods "^Studio\.Info$"` 只有一个生命周期方法 `protected virtual System.Void Studio.Info::Awake()`）。

### `Studio.Info::Awake` 全文（5 条 IL）

```
IL_0001  Singleton<Studio.Info>::CheckInstance() → false 则跳到 ret
IL_000C  Component::get_gameObject()
IL_0011  Object::DontDestroyOnLoad(...)
IL_0017  ldc.i4.0
IL_0018  call Studio.Info::set_isLoadList(false)
IL_001D  ret
```

**`isLoadList` 全程只有 3 个写入点**（`fieldrefs isLoadList` refs=2 仅指 backing field 的直接读写；语义层面是）：

| 位置 | 值 |
|---|---|
| `Studio.Info::Awake` @IL_0018 | `false` |
| `<LoadExcelDataCoroutine>c__Iterator0::MoveNext` @IL_0AE2 | `true`（全部 bundle 跑完） |

### 谁启动协程（`LoadExcelDataCoroutine` 只有 2 个调用者）

- `Studio.StartScene/<LoadCoroutine>c__Iterator0::MoveNext` @IL_0027
- `StudioScene/<Start>c__Iterator0::MoveNext` @IL_0094

`StudioScene/<Start>c__Iterator0::MoveNext` 关键顺序：

```
IL_005B  Studio.Info::get_isLoadList()
IL_0060  brtrue IL_00F9                    // 已加载过 → 跳过整段
IL_0076  ldstr "StudioWait" → Manager.Scene::LoadReserve(...)
IL_008F  Studio.Info::get_Instance()
IL_0094  Studio.Info::LoadExcelDataCoroutine()
IL_00D0  new WaitWhile(Func<bool>) → yield 直到协程结束
IL_00EE  Manager.Scene::UnLoad()
IL_00F9  cameraInfo.Init() ... InputInfo::Init()
IL_02CF  Studio.Studio::get_Instance().Init()
```

### 把 `isLoadList` 置回 `false` 再重跑，安全吗？

**技术上可行，但代价高、且有副作用。** 逐条评估：

| 项 | 影响 |
|---|---|
| Clear 11 个字典（见 Q2 阶段 A 表） | 已有数据全丢，包括**当前场景正在用的** `dicItemLoadInfo`。重跑期间若有 `AddObjectItem.Load` 发生，会命中 `(0,0,0)` 回退或直接 `DeleteIndex`。 |
| `dicMapLoadInfo` / `dicBGMLoadInfo` / `dicENVLoadInfo` **不清** | 这几个会累积/重复写入，但因为 `LoadMapLoadInfo` 等是覆盖式 `set_Item`，重复执行等价。 |
| `fileCheck` / `waitTime` 重新 `new` | 无副作用。 |
| `dicHandAnime[0]/[1]` Clear | 无副作用。 |
| 耗时 | 重跑整条协程 = 重新扫全部 `studio/info/` bundle + 全部 19 类目 + 每类目 30 ms 让帧。**这正是你想省掉的 25 s。** |
| `AssetBundleManager::UnloadAssetBundle(bundlePath, true, null, false)` @IL_0A9A | 每个 bundle 处理完就卸载。若当前场景正在用这些 bundle 里的资源，可能触发重载。 |
| 并发 | `Studio.Info` 是 `DontDestroyOnLoad` 单例；重入由 IL_006F 守卫挡住，但 `isLoadList=false` 期间**任何再次调用都会重入**（第二次调用会在 IL_006F 通过，然后再次 Clear）。 |

**结论：不要重跑整条协程。** 只做「单 bundle 增量」：`LoadAnimeGroupInfo` → `LoadAnimeCategoryInfo` → `LoadItemLoadInfoCoroutine`（这三者都不查 `isLoadList`、不 Clear、不卸载 bundle）。

---

## Q8. fork 里有没有现成的 `dicItemLoadInfo` 重建手段？

**没有。**

`grep -r "dicItemLoadInfo" source/SideloaderLazy/src` 只有 1 处（`Core_Sideloader_Studio/Core.Sideloader.Studio.ListLoader.cs:23`），而且只是**读**：

```csharp
// Core.Sideloader.Studio.ListLoader.cs:15-30
internal static HashSet<int> InternalStudioItemList
{
    get
    {
        if (_internalStudioItemList == null)
        {
            _internalStudioItemList = new HashSet<int>();
            foreach (var x in Singleton<Studio.Info>.Instance.dicItemLoadInfo)
                foreach (var y in x.Value)
                    foreach (var z in y.Value)
                        _internalStudioItemList.Add(z.Key);
        }
        return _internalStudioItemList;
    }
}
```

⚠️ **`_internalStudioItemList` 是永久缓存，只建一次。** 它被 `Core_Sideloader_Studio/UniversalAutoResolver/Core.UAR.Studio.cs:363` 用于「无 extData 的硬 mod 兼容解析」判断。**注入新物品后必须 `Lists._internalStudioItemList = null` 让它重建**，否则 UAR 会把新 ID 误判为「不存在于 item list」并尝试兼容重映射。

### `RegisterStudioListWithSlotReuse` 做什么 / 不做什么

位置：`source/SideloaderLazy/src/Core_Sideloader/Core.Sideloader.HotReload.cs:533-550`

```csharp
private static void RegisterStudioListWithSlotReuse(Manifest manifest, Lists.StudioListData data,
                                                    IDictionary<StudioResolveInfo, StudioResolveInfo> oldLookup)
{
    var start = UniversalAutoResolver.LoadedStudioResolutionInfos.Count;
    UniversalAutoResolver.GenerateStudioResolutionInfo(manifest, data);   // 生成 StudioResolveInfo
    var generated = UniversalAutoResolver.LoadedStudioResolutionInfos;
    var replacements = new Dictionary<int, int>();
    for (var i = start; i < generated.Count; i++)
    {
        var current = generated[i];
        if (!oldLookup.TryGetValue(current, out var old)) continue;
        replacements[current.LocalSlot] = old.LocalSlot;
        current.LocalSlot = old.LocalSlot;
    }
    ReplaceFirstColumnIds(data.Entries, replacements);                     // 改写 CSV 第一列
}
```

**做**：生成 `StudioResolveInfo` 并加入 `LoadedStudioResolutionInfos`；把已存在条目的 `LocalSlot` 复用回去；据此改写 `data.Entries` 的 `row[0]`。

**不做**：❌ 不碰 `Studio.Info.dicItemLoadInfo`；❌ 不碰 `dicItemGroupCategory`；❌ 不调 `LoadItemLoadInfoCoroutine`；❌ 不碰 `Lists.ExternalStudioDataList`（那是调用方 `RegisterHotReloadedLists` 里的 `AddExternalStudioList(data)` 干的，`Core.Sideloader.HotReload.cs:507`）。

### `RefreshStudioItemAssets` 做什么 / 不做什么

位置：`source/SideloaderLazy/src/KK_Sideloader/KK.Sideloader.HotReloadAssets.cs:1140-1175`（`RefreshStudioItemAsset` 在 `:1177`）

```csharp
private static void RefreshStudioItemAssets(ZipmodHotReloadResult result)
{
    if (result.AffectedStudioItemSlots.Count == 0 || !Singleton<global::Studio.Studio>.IsInstance())
        return;

    var studio = Singleton<global::Studio.Studio>.Instance;
    var affectedItems = studio.dicObjectCtrl.Values.OfType<OCIItem>()
        .Where(x => x.itemInfo != null && result.AffectedStudioItemSlots.Contains(x.itemInfo.no))
        .ToArray();
    ...
    foreach (var item in affectedItems)
        if (RefreshStudioItemAsset(studio, item)) result.RefreshedStudioItemCount++;
    ...
}
```

`RefreshStudioItemAsset` 的核心动作（`:1195`）：

```csharp
replacement = AddObjectItem.Load(info, null, null, false, -1);
```

即：对**已经摆在场景里**的 `OCIItem`，用它的 `itemInfo` 重新 `AddObjectItem.Load` 一次，然后把 `guideObject` / `treeNodeObject` / `transform` 从旧对象迁到新对象上（`:1199-1220` 一带）。

**做**：重建已存在物件的 3D 资源实例（换 bundle 后重新加载 mesh/material）。

**不做**：❌ 不往 `dicItemLoadInfo` 里加任何条目 —— `AddObjectItem.Load` 里 `GetLoadInfo` 查不到时走 `(0,0,0)` 回退，再失败就 `Studio.Studio::DeleteIndex(dicKey)` 静默丢弃；❌ 不重建 `dicItemGroupCategory`；❌ 不刷新物品调色板 UI；❌ 不处理「场景还没加载」的情况（`dicObjectCtrl` 为空 → 直接 return）。

### 唯一能重建 `dicItemLoadInfo` 的现成机制

只有游戏自己的 `<LoadExcelDataCoroutine>c__Iterator0`。fork 里没有任何封装。

---

## 附：给你的最小增量配方（基于以上全部 IL 证据）

> 在 `Studio.SceneInfo::Load(String, ref Version)` 的 Prefix 里同步执行；`SceneInfo::Load` 的 `stind.ref` 在 @IL_04B8（ExtSave 的 `SceneInfoLoadTranspiler` 锚点），UAR 的 `SceneReadEvent` 就在那之后触发 —— **必须在 Prefix 里做完，不能延后**。

```csharp
// 0. 前置：zipmod 已通过 fork 的 TryParseZipmod / LoadAllLists(true) 解析，
//    zipmod.StudioLists / zipmod.BoneLists 就绪。

// 1. UAR slot 分配（fork 现成）
foreach (var data in zipmod.StudioLists)   // 物品表必须先于骨骼表
    RegisterStudioListWithSlotReuse(zipmod.Manifest, data, /*oldLookup*/ 空字典);

// 2. 塞进 Sideloader 的外部列表（这一步是后面所有 Sideloader Prefix 生效的前提）
foreach (var data in zipmod.StudioLists) AddExternalStudioList(data);
foreach (var data in zipmod.BoneLists)   AddExternalStudioList(data);

// 3. 反射拿 Studio.Info 单例 + 私有方法
var info    = Singleton<Studio.Info>.Instance;
var tInfo   = typeof(Studio.Info);
var tGroup  = tInfo.GetNestedType("GroupInfo", BindingFlags.NonPublic | BindingFlags.Public);
var fItemGC = tInfo.GetField("dicItemGroupCategory", BindingFlags.Instance | BindingFlags.NonPublic);
var fWait   = tInfo.GetField("waitTime",             BindingFlags.Instance | BindingFlags.NonPublic);

// 3a. ★ waitTime 必须非 null，否则 LoadItemLoadInfoCoroutine 在 IL_027A NRE
if (fWait.GetValue(info) == null)
    fWait.SetValue(info, Activator.CreateInstance(tInfo.GetNestedType("WaitTime", BindingFlags.NonPublic)));

// 4. 逐 bundle 增量（bundleName 即 StudioListData.AssetBundleName，形如 "xxx.unity3d"）
foreach (var bundleName in 受影响的 bundle 名)
{
    // 4a. 新 group：必须先建 group（否则 ItemCategory 会被 IL_00F5 跳过）
    var groupEd = (ExcelData)InvokePrivate(info, "LoadExcelData", bundleName, "ItemGroup_" + Path.GetFileNameWithoutExtension(bundleName));
    if (groupEd != null)
        InvokePrivate(info, "LoadAnimeGroupInfo", groupEd, fItemGC.GetValue(info));

    // 4b. 新 category
    InvokePrivate(info, "LoadAnimeCategoryInfo", bundleName, @"ItemCategory_(\d*)_(\d*)", fItemGC.GetValue(info));

    // 4c. 物品本体 —— 唯一写 dicItemLoadInfo 的地方
    var co = (IEnumerator)InvokePrivate(info, "LoadItemLoadInfoCoroutine", bundleName, @"ItemList_(\d*)_(\d*)_(\d*)");
    while (co.MoveNext()) { }          // 同步跑完（会因 waitTime 让帧逻辑而"假 yield"，这里直接空转）

    // 4d. 骨骼表（可选，补 ItemLoadInfo.bones）
    InvokePrivate(info, "LoadItemBoneInfo", bundleName, @"ItemBoneList_(\d*)_(\d*)");
}

// 5. ★ 让 UAR 的硬 mod 兼容解析重新建索引，否则新 ID 会被当成"不存在"
typeof(Lists).GetField("_internalStudioItemList", BindingFlags.Static | BindingFlags.NonPublic)
             .SetValue(null, null);

// 6. 若物品调色板 UI 已构建过，需要重建（可选，纯用户体验）
//    三个 InitList 都是 public 实例方法（Cecil 已确认），可直接调用已加载场景里的组件：
//      Studio.ItemGroupList::InitList()
//      Studio.ItemCategoryList::InitList(System.Int32 _group)
//      Studio.ItemList::InitList(System.Int32 _group, System.Int32 _category)
//    用 Resources.FindObjectsOfTypeAll<T>() 取组件（Unity 5.6 的 FindObjectsOfType 会漏 inactive）。
```

### 三个必须注意的坑

1. **`waitTime` 为 null 会 NRE**（IL_0274/027A）。步骤 3a 不可省。
2. **`LoadAnimeCategoryInfo` 有 group 存在性守卫**（IL_00F5），新 group 必须先 `LoadAnimeGroupInfo`。
3. **`Lists._internalStudioItemList` 永久缓存**（`Core.Sideloader.Studio.ListLoader.cs:20`），注入后必须置 null。

### 不需要做的事

- ❌ 不需要碰 `ChaListControl`（Studio 物品不读它）。
- ❌ 不需要碰 `FileListInfo`（`ItemList_` / `ItemCategory_` 都不经它门控）。
- ❌ 不需要碰 `FileCheck.dicConfirmed`（只影响动画）。
- ❌ 不需要把 `isLoadList` 置回 `false` 重跑整条协程（会 Clear 掉正在用的数据，且耗时 = 你想省的那 25 s）。
