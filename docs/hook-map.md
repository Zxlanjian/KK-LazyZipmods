# Koikatsu 懒加载 zipmod —— 钩子地图（Hook Map）

> 目标：让 zipmod **不在启动时全量加载**，只在加载角色卡 / Studio 场景卡时加载该卡所需 mod。
>
> 全部结论来自 **Mono.Cecil 实际枚举 + IL 方法体 dump + 源码只读复核**，未凭记忆断言。
> 扫描工具：`<repo>\tools\ilscan\`（net8 + Mono.Cecil，`ilscan.exe`）
> 游戏程序集：`<KoikatsuDir>\Koikatu_Data\Managed\Assembly-CSharp.dll`（types=1924）
> 基座源码：`<upstream>\src\`
>
> **Note on paths.** These notes were written against a specific local install, so absolute paths
> have been replaced with placeholders: `<KoikatsuDir>` is the game folder (the one containing
> `Koikatu_Data\`), `<repo>` is this repository, `<upstream>` is the BepisPlugins checkout this fork
> is based on, and `<work>` is any scratch build directory. File names, offsets, byte counts and IL
> are reproduced verbatim from the actual disassembly.

---

## 0. 实测基线（Lead 提供，直接采用）

| 场景 | Chainloader | Studio 初始化 |
|---|---|---|
| 有 mods | 19.8 s | **59.1 s** |
| `mods` 目录禁用 | 18.6 s | **27.2 s** |

进一步拆分（Lead 实测）：**只跳过 zipmod list-data 注入**（`AddAllLists` → `Lists.ExternalDataList`）就能把 Studio 初始化从 58.8 s 降到 33.4 s（省 25.0 s）；只跳过 bundle 注册 + png 索引仅省 2.9 s。

→ **结论：懒加载的全部收益在于「列表注入时机」，不在 bundle 注册。**

mods 规模：`<KoikatsuDir>\mods` 下 **14 979 个 `.zipmod`，合计 211 891.6 MB（约 207 GB）**。

---

## A. 角色卡加载调用链

### A1. `ChaFileControl` 的读卡重载（精确签名）

`ChaFileControl : ChaFile`。**`ChaFileControl` 没有 `LoadFile`**，只有 `LoadCharaFile` / `LoadFileLimited` / `LoadFrom*`：

```csharp
// ChaFileControl（public）
public System.Boolean ChaFileControl::LoadCharaFile(System.String filename, System.Byte sex, System.Boolean noLoadPng, System.Boolean noLoadStatus)
public System.Boolean ChaFileControl::LoadCharaFile(System.IO.Stream st, System.Boolean noLoadPng, System.Boolean noLoadStatus)
public System.Boolean ChaFileControl::LoadCharaFile(System.IO.BinaryReader br, System.Boolean noLoadPng, System.Boolean noLoadStatus)

public System.Boolean ChaFileControl::LoadFileLimited(System.String filename, System.Byte sex, System.Boolean face, System.Boolean body, System.Boolean hair, System.Boolean parameter, System.Boolean coordinate)
public System.Boolean ChaFileControl::LoadFileLimited(System.String assetBundleName, System.String assetName, System.Boolean face, System.Boolean body, System.Boolean hair, System.Boolean parameter, System.Boolean coordinate)

public System.Boolean ChaFileControl::LoadFromAssetBundle(System.String assetBundleName, System.String assetName, System.Boolean noSetPNG, System.Boolean noLoadStatus)
public System.Boolean ChaFileControl::LoadFromTextAsset(UnityEngine.TextAsset ta, System.Boolean noSetPNG, System.Boolean noLoadStatus)
public System.Boolean ChaFileControl::LoadFromBytes(System.Byte[] bytes, System.Boolean noSetPNG, System.Boolean noLoadStatus)
public System.Void    ChaFileControl::LoadFace(System.String path)
public System.Void    ChaFileControl::LoadFacePreset()
```

> ⚠️ 任务书里假设的 `ChaFileControl.LoadFile(..., bool noLoadEyes)` **不存在**。`noLoadPng` / `noLoadStatus` 是仅有的两个跳过开关。

### A2. `ChaFile.LoadFile` 的确切签名（全部 **protected**）

```csharp
protected System.Boolean ChaFile::LoadFile(System.String path,     System.Boolean noLoadPNG, System.Boolean noLoadStatus)
protected System.Boolean ChaFile::LoadFile(System.IO.Stream st,    System.Boolean noLoadPNG, System.Boolean noLoadStatus)
protected System.Boolean ChaFile::LoadFile(System.IO.BinaryReader br, System.Boolean noLoadPNG, System.Boolean noLoadStatus)
```

> ⚠️ 是 **3 参数**，不是 `(BinaryReader, bool)`。

### A3. 三种重载如何汇聚（IL callers）

| 方法 | 调用者 |
|---|---|
| `ChaFile::LoadFile(string)` | **仅 1 处**：`ChaFileControl::LoadFileLimited` @IL_0014 |
| `ChaFile::LoadFile(Stream, bool, bool)` | **仅 1 处**：`ChaFile::LoadFile(BinaryReader, …)` @IL_002E |
| `ChaFile::LoadFile(BinaryReader, bool, bool)` | 2 处：`ChaFile::LoadFile(Stream, …)` @IL_000B、`ChaFileControl::LoadCharaFile(BinaryReader, …)` @IL_0004 |

→ **唯一收口 = `protected ChaFile::LoadFile(System.IO.BinaryReader br, System.Boolean noLoadPNG, System.Boolean noLoadStatus)`**

`ChaFileControl::LoadCharaFile(Stream, …)` 的调用者 4 处：自身 string 重载 @IL_0024、`LoadFromTextAsset` @IL_0037、`LoadFromBytes` @IL_001F、`UploaderScene::GetCharaFileHashBytes` @IL_004A。
`ChaFileControl::LoadCharaFile(string, …)` → `new FileStream(path, FileMode.Open, FileAccess.Read)` @IL_0019 → `LoadCharaFile(Stream, …)` @IL_0024。
`LoadCharaFile(Stream, …)` → `new BinaryReader(stream)` @IL_0001 → `LoadCharaFile(BinaryReader, …)` @IL_000B。
`ChaFile::LoadFile(Stream, …)` → `new BinaryReader(stream)` @IL_0001 → `LoadFile(BinaryReader, …)` @IL_000B。

**因此：`ChaFile::LoadFile(BinaryReader, …)` 里的 `br.BaseStream` 通常就是原始 `FileStream`，`.Name` 即卡文件绝对路径。**

### A4. KK 分支的 `ChaFile::LoadFile(BinaryReader, …)` 读序

`PngFile::GetPngSize(br)` → （`noLoadPNG` 则 `Seek`，否则 `ReadBytes` 存 `pngData`）→ `ReadInt32` → `loadProductNo`（>100 则 `errorCode = -3`，返回 false）→ `ReadString` 与 `"【KoiKatuChara】"` 比较（不等 `errorCode = -1`）→ `ReadString` → `new Version(...)` → `loadVersion`，与 `ChaFileDefine::ChaFileVersion` `CompareTo`（旧则 `errorCode = -2`）→ `ReadInt32` + `ReadBytes` → `facePngData` → `ReadInt32` + `ReadBytes` → `MessagePackSerializer::Deserialize<BlockHeader>` → `ReadInt64` → `BaseStream.Position` → `header.SearchInfo(ChaFileCustom::BlockName)` → `Seek(pos + info.pos)` → `ReadBytes(size)` → `ChaFile::SetCustomBytes(bytes, version)`。

字面量证据：`strlit "KoiKatuChara"` 命中 `ChaFile::LoadFile(BinaryReader,bool,bool)` @IL_008E 与 `ChaFile::SaveFile(BinaryWriter,bool)` @IL_0026。

### A5. ExtensibleSaveFormat 挂进读卡流程的方式

源码：`<upstream>\src\Core_ExtensibleSaveFormat\Core.ExtendedSave.Hooks.cs`（546 行）

| 行 | 类型 | 目标 | 作用 |
|---|---|---|---|
| L50/52 | `HarmonyPrefix` | `ChaFile::LoadFile(BinaryReader, bool, bool)` | `ChaFileLoadFilePreHook()` → `cardReadEventCalled = false` |
| L94/96 | `HarmonyTranspiler` | 同上 | `ChaFileLoadFileTranspiler`：定位第一个 `SearchInfo` 调用与最后一个 `Seek`，在 `lastSeekIndex + 2` 处插入 `ldarg.0 / blockHeader / ldarg.1 / call Hooks.ChaFileLoadFileHook` |
| L123/125 | `HarmonyPostfix` | 同上 | `ChaFileLoadFilePostHook(ChaFile __instance, bool __result, BinaryReader br)`，KK 兼容 ver1/2 尾巴，保证 `CardReadEvent` 必发 |

`ChaFileLoadFileHook(ChaFile file, BlockHeader header, BinaryReader reader)`：`header.SearchInfo("KKEx" Marker)` 命中则 `reader.BaseStream.Position = basePosition + info.pos`（`basePosition = 当前位置 - header.lstInfo.Sum(x => x.size)`）→ `ReadBytes(info.size)` → `MessagePackSerializer.Deserialize<Dictionary<string, PluginData>>` → `internalCharaDictionary.Set(file, dict)` → `CardReadEvent(file)`。

> **时机结论：ExtSave 数据在 `ChaFile.LoadFile` 内部、custom block 之后解出，早于 `LoadFile` 返回。**

服装卡：`HarmonyTranspiler` on `ChaFileCoordinate::LoadFile(System.IO.Stream)` 与 `(Stream, int)`（L296/298）。
保存侧：`ChaFile::SaveFile(BinaryWriter, bool)`（L188 Prefix / L234 Postfix / L247 Transpiler）。

### A6. 服装卡（coordinate）链路

`ChaFileCoordinate : ChaFileAssist`：

```csharp
public System.Boolean ChaFileCoordinate::LoadFile(UnityEngine.TextAsset ta)
public System.Boolean ChaFileCoordinate::LoadFile(System.String path)          // 唯一有路径的重载
public System.Boolean ChaFileCoordinate::LoadFile(System.IO.Stream st)         // ← 收口
public System.Boolean ChaFileCoordinate::LoadBytes(System.Byte[] data, System.Version ver)
```

`LoadFile(string)` body：`File::Exists(path)`（否则 `lastLoadErrorCode = -6`，返回 false）→ `set_coordinateFileName(Path::GetFileName(path))` → `new FileStream(path, Open, Read)` → **`call ChaFileCoordinate::LoadFile(Stream st)` @IL_002C**。

`LoadFile(string)` 调用者 9 处：`ChaControl::AssignCoordinate` @IL_000F、`ChaControl::SetNowCoordinate` @IL_000F、`ChaCustom.CustomCoordinateFile::Initialize` @IL_0067、`ChaCustom.CustomCoordinateFile::<Start>m__0` @IL_00B6、`ChaCustom.clothesFileControl::Initialize` @IL_0067、`ChaCustom.clothesFileControl::<Start>m__1` @IL_0061、`StageTest/<Start>c__Iterator0::MoveNext` @IL_013E、`Studio.MPCharCtrl/CostumeInfo::InitFileList` @IL_0051、`Studio.OCIChar::LoadClothesFile` @IL_000C。

### A7. 【关键】A4 的 prefix 目标建议 —— 三条路

#### 单个收口点（推荐主钩子）

```
类型全名 : ChaFile
方法名   : LoadFile
参数签名 : (System.IO.BinaryReader br, System.Boolean noLoadPNG, System.Boolean noLoadStatus)
返回类型 : System.Boolean
访问级别 : protected（Harmony 可 patch；ExtSave / KKAPI / KK_WTFix / KK_Fix_PartyCardCompatibility / KK_TranslationHelper 已在 patch 它）
```

**路径获取**：`br.BaseStream` 在 string 路径下就是 `FileStream`，`(br.BaseStream as FileStream)?.Name` 拿到绝对路径；`br.BaseStream is MemoryStream` 时无路径（`LoadFromBytes` / `UploaderScene`）。
**辅助**：`ChaFile::get_charaFileName()` 只有 `LoadCharaFile(string)` @IL_0007 会写入（`set_charaFileName(Path::GetFileName(filename))`），Stream/Bytes 路径下为空 —— 字段无其它写入点。

#### 三条路各自的最早可拿路径点

| 路径 | 最早可拿路径的方法（public） | 说明 |
|---|---|---|
| **Maker 列表缩略图预读** | `ChaCustom.CustomCharaFile::Initialize` → `ChaFileControl::LoadCharaFile(string, byte, bool, bool)` @IL_008A | 会遍历 `UserData/chara/female|male` 全部卡，`noLoadPng=true, noLoadStatus=true` |
| **Maker 实际换装/换人** | `ChaFileControl::LoadFileLimited(string filename, byte sex, bool face, bool body, bool hair, bool parameter, bool coordinate)`（`ChaCustom.CustomCharaFile::<Start>m__2` @IL_011C） | 有 `filename`；KKAPI 已 Postfix 它（`ChaFileControl_LoadLimitedPostHook`） |
| **Studio 导入角色卡** | `Studio.AddObjectFemale::Add(string _path)` @IL_0026 / `Studio.AddObjectMale::Add(string _path)` @IL_0027 | body：`Manager.Character::CreateFemale(...)` → `chaFile.LoadCharaFile(_path, 255, true, true)` @IL_0026 |
| **Studio 场景内嵌角色** | ❌ **无独立卡文件** | `Studio.OICharInfo::Load(BinaryReader)` @IL_002A 从场景流里读卡（`ChaFileControl::LoadCharaFile(br, …)`），只能靠场景卡前置加载解决 |
| **服装卡（Maker / Studio）** | `ChaFileCoordinate::LoadFile(System.String path)`（public，返回 `System.Boolean`） | 9 个调用点覆盖全部 UI 路径 |

#### 推荐组合

1. **`ChaFileControl::LoadCharaFile(string filename, byte sex, bool noLoadPng, bool noLoadStatus)` 的 Prefix** —— 只做「记录路径」：`ConditionalWeakTable<ChaFileControl, string>` 或 `[ThreadStatic]` 当前路径。
2. **`ChaFile::LoadFile(BinaryReader, bool, bool)` 的 Prefix** —— 真正干活：拿到路径（先用 `FileStream.Name`，回退到第 1 步记录）→ 读卡内 UAR/ExtSave → 定位所需 zipmod → 加载 → 刷新列表 → `return true` 放行。
3. **服装卡另加 `ChaFileCoordinate::LoadFile(System.IO.Stream)` 的 Prefix**（配合 `LoadFile(string)` 记录路径）。

> Prefix 返回值：返回 `false` 会跳过原方法（读卡失败）。**加载 mod 成功时务必返回 `true`。**
> 与 ExtSave 的 Prefix 共存无冲突（Harmony 支持多 Prefix）；ExtSave 的 Prefix 无返回值，只是清标志位。

#### 「是否存在单一 choke point，能在任何 mod item 解析之前拿到卡路径？」

- **角色卡：是** —— `ChaFile::LoadFile(BinaryReader, bool, bool)`。全部 33 个 `LoadCharaFile` 调用点都汇聚到这里，且都在它内部才开始解 ExtSave/UAR 数据。
- **服装卡：是** —— `ChaFileCoordinate::LoadFile(Stream)`。
- **Studio 场景卡：是** —— `Studio.SceneInfo::Load(string, ref Version)`（见 B3）。
- **Studio 场景内的角色：否** —— 角色数据内嵌在场景文件里，无独立卡路径；必须由场景卡的前置钩子统一解决。

### A8. KKAPI 三个事件的时机

| 事件 | 时机 | 能拿路径？ | 证据 |
|---|---|---|---|
| `MakerAPI.MakerStartedLoading` | Maker 加载**早期**，早于任何读卡 | ❌ 无路径 | KKAPI.xml L832：「Early in the process of maker loading. Most game components are initialized and had their Start methods ran. Warning: Some components and objects might not be loaded or initialized yet, especially if they are mods.」 |
| `MakerAPI.ChaFileLoaded` | 读卡**完成之后**（`ChaFileControl::LoadFileLimited` 的 Postfix 里构造事件） | ✅ **有 `Filename`** | `ChaFileLoadedEventArgs` ctor `(string filename, byte sex, bool face, bool body, bool hair, bool parameter, bool coordinate, ChaFileControl characterInstance, ChaFile loadedChaFile)`；ctor 调用点仅 2 处：`MakerAPI::OnCreateCustomControls` @IL_0048、`MakerAPI/Hooks::ChaFileControl_LoadLimitedPostHook` @IL_0021。XML L872：「only fired when inside the character maker」 |
| `CharacterApi.CharacterReloaded` | 读卡**完成之后**（所有 `CharaCustomFunctionController` 更新完） | ❌ 无路径 | KKAPI.xml L2162：「Fired after all CharaCustomFunctionController have updated.」 |

其它相关（KKAPI.xml）：`MakerBaseLoaded` L840（fully loaded）、`MakerFinishedLoading` L848（avoid heavy work）、`InsideMakerChanged` L892（「the earliest event fired when user starts the character maker」）、`CharaListIsLoading` L860（「Use to avoid unnecessary processing cards when they are loaded to the character list」）、`CharacterApi.CoordinateLoaded` L2167（「Not filed if the coordinate file was not loaded into a character (so not during list updates)」）。

> **`MakerAPI.ChaFileLoaded` 太晚**：它触发时 `LoadFileLimited` 已返回，卡里的 item ID 已解析完毕，来不及补 mod。只能用来「事后校验缺 mod」。

---

## B. Studio 场景卡加载调用链

### B1. 场景读取入口（Cecil 枚举确认）

**不存在 `Studio.Scene` 类型。** `Studio.*` 中与 Scene 相关的类型：

- `Studio.SceneInfo`（`System.Object`，public）← **真正的场景数据结构 + 反序列化**
- `Studio.SceneLoadScene : MonoBehaviour`（public）+ 嵌套 `<LoadScene>c__Iterator0` / `<NotificationLoadCoroutine>c__Iterator1` / `<NotificationImportCoroutine>c__Iterator2` / `<InitInfo>c__AnonStorey3`
- `Studio.CheckScene` / `ExitScene` / `NotificationScene` / `ShortcutMenuScene` / `StartScene`（均 MonoBehaviour）
- `Studio.Studio/<LoadSceneCoroutine>c__Iterator2`

`Studio.SceneInfo` 相关方法：

```csharp
public System.Boolean Studio.SceneInfo::Load(System.String _path)                                   // 薄转发
public System.Boolean Studio.SceneInfo::Load(System.String _path, ref System.Version _dataVersion)  // ← 真正反序列化
public System.Boolean Studio.SceneInfo::Import(System.String _path)
public System.Void    Studio.SceneInfo::Import(System.IO.BinaryReader _reader, System.Version _version)
public System.Boolean Studio.SceneInfo::Save(System.String _path)
public System.Void    Studio.SceneInfo::Save(System.IO.BinaryWriter _writer, System.Collections.Generic.Dictionary<System.Int32, Studio.ObjectInfo> _dicObject)
```

`Studio.Studio : Singleton<Studio.Studio>` 相关方法：

```csharp
public System.Boolean Studio.Studio::LoadScene(System.String _path)                       // callers = 0（游戏内不调用）
public System.Collections.IEnumerator Studio.Studio::LoadSceneCoroutine(System.String _path)
public System.Boolean Studio.Studio::ImportScene(System.String _path)
public System.Void    Studio.Studio::InitScene(System.Boolean _close)
public System.Void    Studio.Studio::Init()
public System.Void    Studio.Studio::Duplicate()
public System.Void    Studio.Studio::SaveScene()
```

#### 实际调用链（IL 已 dump）

**加载（点 Load 按钮）**
```
Studio.SceneLoadScene::OnClickLoad()
  → CanvasGroupExtensions::Enable → List<string>::get_Item(index)  [选中场景路径]
  → SceneLoadScene::LoadScene(path)  → MonoBehaviour::StartCoroutine
  → <LoadScene>c__Iterator0::MoveNext
        @IL_0026 Studio.Studio::get_Instance()
        @IL_0031 Studio.Studio::LoadSceneCoroutine(_path)          ← callers=1
        @IL_0076 Behaviour::set_enabled
        @IL_00A1 Manager.Scene::LoadReserve("StudioNotification")
  → <LoadSceneCoroutine>c__Iterator2::MoveNext
        @IL_002B File::Exists(_path)
        @IL_0041 Studio.Studio::InitScene(false)                    ← 先清空当前场景
        @IL_006C set_sceneInfo(new SceneInfo())
        @IL_0082 Studio.SceneInfo::Load(_path)                      ← 反序列化
        @IL_009E Studio.AddObjectAssist::LoadChild(get_sceneInfo().dicObject, null, null)   ← 放置物件
        @IL_00D7 Studio.Studio::AddMapCoroutine(map, _close, _wait)
        @IL_0100 Studio.MapList::UpdateInfo()
        @IL_011E ChangeAmount::Copy → @IL_0128 MapCtrl::Reflect()
        @IL_0148 BGMCtrl::Play(no) → @IL_0168 ENVCtrl::Play(no) → OutsideSoundCtrl
```

**导入（点 Import 按钮）**
```
Studio.SceneLoadScene::OnClickImport()
  → @IL_0016 Studio.Studio::ImportScene(_path)                     ← callers=1
  → @IL_0014 Studio.SceneInfo::Import(_path)
  → @IL_002D Studio.AddObjectAssist::LoadChild(get_sceneInfo().dicImport, null, null)
  → TreeNodeCtrl::RefreshHierachy() → CameraSelector::Init() → ret 1
```

`Studio.SceneInfo::Load(string)` body（纯转发）：
```
ldarg.0 / ldarg.1 / ldloca.s V_0(Version) / call SceneInfo::Load(string, ref Version) @IL_0004 / ret
```

`Studio.SceneInfo::Load(string, ref Version)` body：`new FileStream(_path, Open, Read, FileShare.Read)` @IL_0004 → `new BinaryReader` → `PngFile::SkipPng(br)` @IL_0012 → `set_dataVersion(new Version(br.ReadString()))` @IL_0023 → `ReadInt32`（对象数）→ 循环创建 `OICharInfo` / `OIItemInfo` / `OILightInfo` / `OIFolderInfo` / `OIRouteInfo` / `OICameraInfo` → `ObjectInfo::Load(BinaryReader, Version, bool _import, bool _other)` @IL_00CB → `dicObject.Add` → 之后 `map` / `caMap.Load` / `sunLightType` / `mapOption` / `aceNo` … → **`stind.ref`（写 `ref _dataVersion`）@IL_04B8，`leave IL_04CB`**。

> `stind.ref` + `leave` 这一对正是 ExtSave `SceneInfoLoadTranspiler` 的插入锚点，说明 **ExtSave 场景数据在物件循环全部结束之后才解出**。

### B2. KKAPI `StudioSaveLoadApi.SceneLoad` 的时机

KKAPI 对 `Studio.Studio::LoadScene` / `LoadSceneCoroutine` / `ImportScene` 各挂 **Prefix + Postfix**；`SceneLoadComplete(SceneOperationKind)` 在场景**完全加载之后**触发。

`SceneLoadEventArgs` ctor `(SceneOperationKind, ReadOnlyDictionary<int, ObjectCtrlInfo>)`，属性 `Operation` / `LoadedObjects`。
`SceneOperationKind`：`Load`（替换当前场景）、`Import`（追加）、`Clear`（**仅用户点 Reset 触发，Studio 启动时不触发**）。

KKAPI.xml L5668：「Fired right after a scene is succesfully imported, loaded or cleared. Runs immediately after all SceneCustomFunctionController objects trigger their events.」

> **❌ 拿不到场景文件路径。** 事件只有 operation + loaded objects。
> **❌ 时机太晚。** 场景已反序列化、物件已放置、缺 mod 的物件已被 `DeleteIndex` 丢弃（见 D1）。
> 只能用于「事后校验 / 提示缺 mod」。

### B3. 【关键】场景真正反序列化前的最合适 prefix

#### 首选

```
类型全名 : Studio.SceneInfo
方法名   : Load
参数签名 : (System.String _path, ref System.Version _dataVersion)
返回类型 : System.Boolean
```

- 有完整 `_path`；唯一真正读场景文件的地方。
- 两条入口（`LoadSceneCoroutine` @IL_0082、薄转发 `Load(string)` @IL_0004）都命中它，**不会重复触发**。
- KKAPI **没有** patch 它（KKAPI 只 patch `Studio.Studio::LoadScene`/`LoadSceneCoroutine`/`ImportScene`），无冲突。

#### 备选 / 补充

| 目标 | 签名 | 用途 |
|---|---|---|
| `Studio.SceneLoadScene::LoadScene` | `private System.Collections.IEnumerator LoadScene(System.String _path)` | **比 `SceneInfo::Load` 更早**（早于 `InitScene(false)` 清场）。若希望「加载 mod 失败就不清空当前场景」，用这个 |
| `Studio.Studio::ImportScene` | `public System.Boolean ImportScene(System.String _path)` | 导入路径，有 `_path` |
| `Studio.Studio::LoadSceneCoroutine` | `public System.Collections.IEnumerator LoadSceneCoroutine(System.String _path)` | 协程入口，有 `_path` |
| `Studio.Studio::LoadScene` | `public System.Boolean LoadScene(System.String _path)` | **游戏内 callers=0**，仅 API 存在 |

> Prefix 里应在 `SceneInfo::Load` 之前完成：读场景文件尾部 ExtSave（`"【KStudio】"` 标记）→ 取 UAR `itemInfo`/`patternInfo` 的 GUID 列表 → 加载对应 zipmod → 注入 Studio 列表（见 D2）→ `return true` 放行。

---

## C. 启动期「必须有 mod 数据」的路径清单

### C1. `ChaListControl::LoadListInfoAll` 何时被调用

```csharp
public System.Boolean ChaListControl::LoadListInfoAll()
```

**调用者仅 1 处：`Manager.Character::Awake` @IL_006F**（callvirt）。

`Manager.Character::Awake` 顺序：`CheckInstance()` → `set_enableCharaLoadGCClear(true)` → `DontDestroyOnLoad` → `new ChaListControl()` → `set_chaListCtrl` → `new ChaLoadingTask()` → `new SortedDictionary<int, ChaControl>` → `new List<ChaControl>` → `set_editChara(null)` → `set_netRandChara(null)` → `set_netRandCharaNum(0)` → `new List<string>` → **`get_chaListCtrl().LoadListInfoAll()` @IL_006F** → `new Manager.Character/RandamNameInfo()` → `ChaListControl::LoadExcelData("list/random_name/random_name.unity3d", "random_name", 2, 1)` @IL_008C。

> **结论：游戏启动时（`Manager.Character.Awake`，`DontDestroyOnLoad` 单例）调用一次，早于 Maker / Studio 打开。**
> 不是「Maker 打开时」也不是「Studio 打开时」。

`LoadListInfoAll()` body：
- `Enum.GetValues(ChaListDefine/CategoryNo)` → 遍历类目
- **`CommonLib::GetAssetBundleNameListFromPath("list/characustom/", false)` @IL_001B**
- 每个 bundle：`AssetBundleManager::LoadAllAsset(bundleName, typeof(UnityEngine.TextAsset), null)` → `GetAllAssets()`
- 每个 TextAsset：`Object::get_name()` → `YS_Assist::GetRemoveStringRight(name, "_", false)`，与 `categoryNo + "_"` 比较 → `ChaListControl::LoadListInfo(dictData, ta)` @IL_0127
- 末尾 `ChaListControl::LoadItemID()` @IL_0175

**Sideloader 注入点**：`Core_Sideloader.ListLoader.Hooks.cs` L48
```csharp
[HarmonyPostfix, HarmonyPatch(typeof(ChaListControl), nameof(ChaListControl.LoadListInfoAll))]
LoadListInfoAllPostHook(ChaListControl __instance) => LoadAllLists(__instance);
```
`LoadAllLists` 遍历 `Lists.ExternalDataList` 逐条 `LoadList`，**末尾一次 `instance.LoadItemID()`**。

### C2. `AssetBundleCheck.IsFile` / `AssetBundleData.isFile` 的启动期调用者

```csharp
public static System.Boolean AssetBundleCheck::IsFile(System.String assetBundleName, System.String fileName)
public System.Boolean AssetBundleData::get_isFile()
```

`AssetBundleCheck::IsFile` 调用者 **41 处**（全量 Cecil），关键：`ActionGame.ActionControl::Load*` 一批（`LoadBaseInfo`/`LoadPersonalityInfo`/`LoadCustomInfo`/`LoadMapInfo`/… 各 @IL_0013）、`ActionGame.EnvArea3D::Initialize` @IL_0043、`ActionGame.FootStepSEDefault::InitFootStep`/`InitMapSE` @IL_002A、`AnimationKeyInfo::LoadInfo` @IL_0013、`AssignedAnotherWeights::CreateBoneList` @IL_000D、**`ChaListControl::LoadExcelData` @IL_0002**、`CommonLib::LoadAsset` @IL_0013、`GlobalMethod::LoadExcelData` @IL_0002、`Illusion.Game.Utils/Sound::InitMapBGM`/`InitMapEnv`/`InitEnvClipTable`/`InitSEClipTable`、`Manager.Communication::Load*` 一批、`ShapeInfoBase::LoadCategoryInfoList` @IL_0002、`TitleScene/<Start>` @IL_00D3。

`AssetBundleData::get_isFile` 调用者 **10 处**，全部在 `AssetBundleData::LoadBundle*` / `AssetBundleManifestData::LoadBundle*` 与 `ADV.Commands.Chara.KaraokePlay/*::Load`。

> **结论：这些是按名字探测单个 bundle/asset 是否存在（非全量遍历）。** mod 缺失只让 `IsFile` 返回 false，走「无此资源」分支。**不是 25 s 的消耗源。**
> Sideloader 用 Postfix 兜底：`IsFileHook` / `IsFileHook2` → `IsSideloaderAB(...)`。

### C3. `CommonLib::GetAssetBundleNameListFromPath` 的启动期调用者

```csharp
System.Collections.Generic.List<System.String> CommonLib::GetAssetBundleNameListFromPath(System.String path, System.Boolean subdirCheck)
```

**任务书问的三个路径的实际归属（`strlit` 精确命中）**：

| 路径字面量 | 命中处 |
|---|---|
| `"list/characustom/"` | `ChaListControl::LoadListInfoAll` @IL_001B |
| `"studio/info/"` | `Studio.Info/<LoadExcelDataCoroutine>c__Iterator0::MoveNext` @IL_0193 |
| `"h/list/"` | `ClubReport::CreateAllAnimationList` @IL_0007、`ClubReport::LoadResultAddTaii` @IL_0000、`ClubReport::LoadHResultAddTaii` @IL_0000、`EliminateScale::LoadList` @IL_0000、`FaceListCtrl::LoadInfo` @IL_0000、`FreeHScene::LoadDarkList` @IL_0000、`H3PDarkHoushi::Proc` @IL_01E4/0214、`H3PHoushi::Proc` @IL_021B（共 9 处） |
| `"map/list/mapinfo/"` | `BaseMap::LoadMapInfo` @IL_000B（**仅 1 处**，callers 只有 `BaseMap::Start` @IL_0001） |
| `"map/list/mapthumbnailinfo/"` | ❌ **Assembly-CSharp 里 0 命中** |

> **重要发现：`"map/list/mapthumbnailinfo/"` 在 KK 本体不存在。** 它只出现在 Sideloader 的 `Core.Sideloader.Hooks.cs:73` 硬编码判断里（跨游戏兼容 AI/HS2 的残留）。UTF-16 字节搜索 `mapthumbnailinfo` 也返回 -1。

Sideloader 的三路径注入（`Core_Sideloader/Core.Sideloader.Hooks.cs` L70-84）：
```csharp
[HarmonyPostfix, HarmonyPatch(typeof(CommonLib), nameof(CommonLib.GetAssetBundleNameListFromPath))]
static void GetAssetBundleNameListFromPath(string path, List<string> __result)
{
    if (path == "h/list/" || path == "map/list/mapinfo/" || path == "map/list/mapthumbnailinfo/")
        foreach (var assetBundleName in BundleManager.Bundles.Keys.Where(x => x.StartsWith(path)))
            if (!__result.Contains(assetBundleName)) __result.Add(assetBundleName);
}
```

Studio 版（`Core_Sideloader_Studio/Core.Sideloader.Studio.Hooks.cs` L93）：
```csharp
if (path == "studio/info/")
    // 把 Lists.ExternalStudioDataList.Keys 全加入 __result
```

其它 `CommonLib::GetAssetBundleNameListFromPath` 调用者（41 处，节选）：`GlobalMethod::LoadAllListText`/`LoadAllListTextFromList`/`LoadExcelDataAlFindlFile`/`LoadAllFolderInOneFile`/`LoadAllFolder`、`ActionGame.ActionMap::CreateActionPoint`（`"map/actionpoint/"`）、`LoadCalcGate`/`LoadNavigationInfo`/`LoadBestPlaceArea`、`ActionGame.ActionControl::LoadData`（`"action/actioncontrol/"`）、`ActionGame.CharacterPosSet::Initialize`（`"map/waitpoint/"`）、`ActionGame.Cycle::Initialize`（`"action/list/fixevent/"`）、`ActionGame.EnvArea3D::Initialize`（`"action/list/sound/se/env/"`）、`ActionGame.FootStepSEDefault::InitFootStep`（`"action/list/sound/se/footstep/"`）、`ActionScene/<Start>` ×4、`ADV.*`、`CustomTitleTrial::Start`、`LogoScene/<Start>`、`FixCharaEvent.*`、`FixEventSceneEx::Start`、`Illusion.Game.Utils/Sound::InitMapBGM`（`"action/list/sound/bgm/"`）/`InitMapEnv`/`InitEnvClipTable`/`InitSEClipTable`、`Manager.Communication/<LoadInfo>`、`Manager.Game::GetFixCharaTextAsset`、`Manager.Game::Awake` @IL_006B。

### C4. `Studio.Info.dicItemLoadInfo` 在启动期如何构建

```csharp
// 字段（实例，非 static）
System.Collections.Generic.Dictionary<System.Int32,
    System.Collections.Generic.Dictionary<System.Int32,
        System.Collections.Generic.Dictionary<System.Int32, Studio.Info/ItemLoadInfo>>> Studio.Info::dicItemLoadInfo
```

`Studio.Info : Singleton<Studio.Info>`。构建者：**`Studio.Info::LoadExcelDataCoroutine()`**（public IEnumerator），真实逻辑在 `Studio.Info/<LoadExcelDataCoroutine>c__Iterator0::MoveNext`。

**调用者仅 2 处**：
- `Studio.StartScene/<LoadCoroutine>c__Iterator0::MoveNext` @IL_0027
- `StudioScene/<Start>c__Iterator0::MoveNext` @IL_0094

`StudioScene/<Start>c__Iterator0::MoveNext` 启动顺序（IL 已 dump）：
```
IL_005B Studio.Info::get_isLoadList()      ← 一次性锁
IL_0060 brtrue IL_00F9                     ← 已加载则跳过
IL_0076 Manager.Scene::LoadReserve("StudioWait")
IL_008F/0094 Studio.Info::LoadExcelDataCoroutine()      ← ★ 25 s 的所在
IL_00D0 WaitWhile(<>m__0)                  ← 等 isLoadList 变 true
IL_00F3 Manager.Scene::UnLoad()
IL_00F9 StudioScene/CameraInfo::Init()
         … Delegate::Combine 注册 / StudioScene::CreatePatternList() / InputInfo::Init()
IL_02CF Studio.Studio::get_Instance().Init()
         … UpdateUI()
```

`<LoadExcelDataCoroutine>c__Iterator0::MoveNext` 内部（IL 已 dump）：
```
IL_006F get_isLoadList() 重入保护
IL_00A9–IL_014D  Clear() 掉 11 个字典（含 dicItemLoadInfo）
IL_0193 ldstr "studio/info/"
IL_0199 CommonLib::GetAssetBundleNameListFromPath(path, false)
IL_01A9 List<string>::Sort()
IL_01F5 new Studio.Info/FileListInfo(pathList)     ← 内部对每个 bundle 调 Studio.AssetBundleCheck::GetAllFileName
IL_020D 循环每个 bundlePath：
    <fileName> = Path::GetFileNameWithoutExtension(bundlePath)
    FileListInfo::Check(bundlePath, prefix + fileName) 命中才加载：
      "AccessoryPointGroup_" → LoadExcelData → LoadAccessoryGroupInfo
      "AccessoryPoint_"      → LoadAccessoryPointInfo
      "Bone_"                → LoadBoneInfo
      "HandAnime_(\d*)_(\d*)" → LoadHandAnimeInfo
      "ItemGroup_"           → LoadExcelData → LoadAnimeGroupInfo(→ dicItemGroupCategory)
      "ItemCategory_(\d*)_(\d*)" → LoadAnimeCategoryInfo(→ dicItemGroupCategory)
      "ItemList_(\d*)_(\d*)_(\d*)" → LoadItemLoadInfoCoroutine  ★ item 主列表
      "ItemBoneList_(\d*)_(\d*)"  → LoadItemBoneInfo
      "Light_"               → LoadLightLoadInfo
      "AnimeGroup_" / "AnimeCategory_" / "Anime_" / "HAnime_"
      "VoiceGroup_" / "VoiceCategory_" / "Voice_"
      "BGM_" / "Map_" / "Filter_"
    每个前缀之间都有 WaitTime::get_isOver() 让帧
IL_0AE2 set_isLoadList(true)
```

**`Studio.Info/WaitTime`**：`get_isOver() => !(Time.realtimeSinceStartup < nextFrameTime)`；`Next() => nextFrameTime = Time.realtimeSinceStartup + 0.03f`
→ **每 30 ms 让出一帧，整个流程跨几百帧。这就是「Studio 初始化 59.1 s」的主体。**

**`Studio.Info::FindAllAssetName(string _bundlePath, string _regex)`**（`LoadItemLoadInfoCoroutine` / `LoadAnimeLoadInfoCoroutine` / `LoadVoiceLoadInfoCoroutine` / `LoadItemBoneInfo` / `LoadAnimeCategoryInfo` / `LoadHandAnimeInfo` 共 6 个调用者）：
`AssetBundleManager::get_ManifestBundlePack()` → 遍历所有 `BundlePack` → `AssetBundleManifest::GetAllAssetBundles()` → `FindIndex` 找 `_bundlePath` → `LoadAssetBundle(_bundlePath, false, bundlePack.Key)` → `GetAllAssetNames()` → `Select(Path::GetFileNameWithoutExtension)` → `Where(regex)` → `ToArray()`。

> **Sideloader 源码注释原文（`Core_Sideloader_Studio/Core.Sideloader.Studio.Hooks.cs` L83）：**
> `// This patch gets called millions of times`
> 这是对 `Studio.Info::FindAllAssetName` 的 HarmonyPrefix —— 命中 `ExternalStudioDataList` 就直接短路返回，绕开上面那个全 manifest 扫描。

**`dicItemLoadInfo` 的字段读写点（10 处）**：`Studio.AddObjectItem::GetLoadInfo` @IL_0005（ldfld）、`Studio.Info::.ctor` @IL_001C（stfld）、`Studio.Info::LoadItemLoadInfo` @IL_0082/0093/00A4/00BC/00D4（ldfld）、`Studio.Info::LoadItemBoneInfo` @IL_0010（ldfld）、`<LoadExcelDataCoroutine>c__Iterator0::MoveNext` @IL_00C4（ldfld）、`Studio.ItemList::InitList` @IL_0061（ldfld）。

### C5. 其它插件启动期是否遍历全部 mod item / shader

扫描范围：`<KoikatsuDir>\BepInEx\plugins\**\*.dll`（100+ DLL），逐个 `ilscan <path> patch ".*"` + `callers`。

| 插件 | 启动期全量遍历？ | 证据 |
|---|---|---|
| **MaterialEditor.dll**（`plugins\API_Plugins\`，types=300） | ❌ **无任何 HarmonyPatch 属性** | `ilscan mat patch ".*"` 输出为空；用 KKAPI 事件/自有机制，启动期不遍历 mod item |
| **ShaderFixer.Koikatu.dll**（types=5） | ❌ 被动 | 仅 `POSTFIX MaterialAPIAfterSetShader -> MaterialEditorAPI.MaterialAPI::SetShader` |
| **MassShaderEditor.Koikatu.dll**（types=8） | ❌ 全是 UI 交互时 | `AddClickableNames -> MaterialEditorUI::PopulateList`、`ColorPaletteAfterSetup/AfterClose -> Studio.ColorPalette::Setup/Close`、`CustomChangeMainMenuAfterChangeWindowSetting`、`FillFieldsOnClick` |
| **KK_ChaAlphaMask.dll**（types=8） | ❌ 无启动期全量遍历 | `KCOXMaskHook_Post -> Material::SetTexture`、`SetClothesState_Post`、`ChangeCustomClothes_Post`、`ChangeAlphaMask_Post`、`CvsClothes_Start_Post`、`UpdateSelectClothes_Post` |
| **ShaderManagementTool.dll**（types=2） | ❌ 无 patch | `ilscan` patch 输出为空 |
| **XUnity.AutoTranslator.Plugin.Core.dll**（5.6.2.0，types=415） | ❌ | `patch ".*"` 输出为空；`GetAllAssetName` / `LoadAllAsset` / `LoadListInfoAll` / `GetCategoryInfo` 的 callers 查询均返回 0 |
| **XUnity.ResourceRedirector.dll**（2.1.0.0，types=72） | ❌ | `patch` 输出为空（用自有 redirect 注册机制） |
| **KKP_Fix_CharacterListOptimizations.dll** | ❌ | 只 patch `Localize.Translate.CustomFileListSelecter::Initialize/Awake`（读卡列表优化） |
| **AnimationLoader.Koikatu.dll** | ⚠️ 在 Studio 列表协程**结束时**插一脚 | `POSTFIX LoadStudioAnims -> Studio.Info::LoadExcelDataCoroutine` |
| **KK_Fix_StudioOptimizations.dll** | ⚠️ 包一层 Studio 启动协程 | `POSTFIX OverrideLoadCoroutine -> Studio.StartScene::LoadCoroutine` |
| BetterSceneLoader / CameraFrameMask / LightManager / StudioAddonLite | ❌ | 各 `PREFIX StudioEntrypoint -> StudioScene::Start` |
| DefaultStudioScene / DefaultParamEditor / KK_StudioMiscSearch / HideAllUI | ❌ | 各 `POSTFIX ... -> Studio.Studio::Init` |
| KK_Fix_ResourceUnloadOptimizations | ❌ | `PREFIX/POSTFIX -> Studio.Studio::LoadScene` / `LoadSceneCoroutine` |
| KK_StudioSceneNavigation / KK_StudioSceneInitialCamera / KK_TreeNodeNaming / KK_TreeNodeOrdering / KK_QuickAccessBox / KK_Fix_UnlimitedMapLights / KK_BrowserFolders_Hooks_KK / KK_CharacterReplacer / KK_Fix_Heterochromia / KK_Fix_LoadFileLimited / KK_WTFix / KK_Fix_PartyCardCompatibility / KK_TranslationHelper / KK_DarknessWithStrawberryFlavor / ExtensibleSaveFormat | ❌ | 均只 patch 单个读/存/UI 方法，无全量遍历 |

遍历 `ChaListControl` 的插件（`GetCategoryInfo` / `GetListInfo` callers）：`KKBP_Exporter.PmxBuilder::CreateCharacterInfoData`、`KK_Fix_RestoreMissingFunctions::MakerAPI_RegisterCustomSubCategories`、`KK_ItemBlacklist::PrintInfo`、`KK_RandomCharacterGenerator.Randomizer*`、`KKVMDPlayEyeTextureCache::PreloadGagEyeTextures`、`KK_StudioPseudoMaker.PseudoMaker.ItemBlacklist::PrintInfo`、`PseudoMaker.UI.PickerPanel::InitializeCategories`、`KK_ClothingBlendShape`、`KK_PantyFairy`、`MoreAccessoriesKOI::ArraySync`、`KK_CoordinateLoadOption`、`KK_ClothesToAccessories`、`KoiClothesOverlayX`、`AccMover` —— **全部是 UI 交互/生成角色时，非启动期。**

**`LoadListInfoAll` 的插件调用者只有 1 处**：`PseudoMaker.UI.PickerPanel::InitializeCategories` @IL_0019（Studio 里点开 Picker 面板时才调）。

> ### C5 结论
> **没有任何第三方插件在启动期遍历全部 zipmod 的 item / shader。**
> 启动期真正遍历 mod 数据的只有 **游戏本体 + Sideloader 自己的三条路径**：
> 1. `Manager.Character::Awake` → `ChaListControl::LoadListInfoAll` → `GetAssetBundleNameListFromPath("list/characustom/")` → 每 bundle `LoadAllAsset(..., typeof(TextAsset), ...)`
> 2. Studio 启动 `StudioScene/<Start>` 与 `Studio.StartScene/<LoadCoroutine>` → `Studio.Info::LoadExcelDataCoroutine` → `GetAssetBundleNameListFromPath("studio/info/")` → `FindAllAssetName` → `LoadItemLoadInfoCoroutine` 等 → `LoadExcelData` → `LoadItemLoadInfo` 填 `dicItemLoadInfo`
> 3. 上面第 2 条内部的 `WaitTime`（每 30 ms 一帧）—— **25 s 就在这一段**

---

## D. 可行性与风险判断

### D1. 技术上是否可行？

**可行，但必须做「前置加载」，不能做「事后补加载」。**

关键机制：
1. **Studio 物品缺失不会崩溃，但会静默丢弃。**
   `Studio.AddObjectItem::Load(OIItemInfo, ObjectCtrlInfo, TreeNodeObject, bool, int)`：
   - `GetLoadInfo(group, category, no)` @IL_0018 → 查 `Studio.Info::dicItemLoadInfo[group][category][no]`；未命中则 @IL_0024-002C **用 `(0,0,0)` 再查一次**（回退到默认 item）
   - 拿到 `ItemLoadInfo` 后 `CommonLib::LoadAsset(bundlePath, fileName, clone, manifest)` @IL_0047 → 返回 null 则 **`Studio.Studio::DeleteIndex(dicKey)` @IL_005F + 返回 null**
   → 物件消失，且索引已被删掉，事后无法再补。
2. **UAR 解析发生在 `SceneInfo::Load` 内部。** ExtSave 的 `SceneInfoLoadHook` 在 `stind.ref` @IL_04B8 之后触发 `SceneReadEvent(path)` → `UniversalAutoResolver.ExtendedSceneLoad` → `ResolveStudioObjects` 立即用**当前** mod 列表改写 slot。此时 mod 还没加载 → 解析失败，slot 保持 `BaseSlotID`（100000000）以上的无效值。
3. **`Studio.Info.isLoadList` 是一次性锁。** `Studio.Info::Awake` @IL_0017-0018 `set_isLoadList(false)`；`<LoadExcelDataCoroutine>::MoveNext` @IL_006F 判重入、@IL_0AE2 `set_isLoadList(true)`。`set_isLoadList` 调用者只有这 2 处。
   → **`LoadExcelDataCoroutine` 跑完就不会再跑；想重跑必须反射把 `<isLoadList>k__BackingField` 置回 false。**

### D2. 加载卡所需 mod 后，如何让已初始化的列表认识新 item

#### 角色卡（Maker 本体侧）—— **可以直接复用热重载设施**

`Sideloader.ListLoader.Lists`（`internal static partial class Lists`）：

| 方法 | 行号 | 说明 |
|---|---|---|
| `internal static void LoadAllLists(ChaListControl instance)` | `Core.Sideloader.ListLoader.cs:41` | `CurrentInstance = instance;` → `InternalDataList = r_dictListInfo.GetValue<...>(instance);` → `foreach (ChaListData data in ExternalDataList) LoadList(instance, data);` → **末尾 `instance.LoadItemID();`** |
| `internal static bool ApplyHotReloadedLists(IEnumerable<ResolveInfo> removedResolveInfos, IEnumerable<ChaListData> lists)` | `Core.Sideloader.ListLoader.cs:56` | `if (CurrentInstance == null) return false;` → removed 按 `(CategoryNo, LocalSlot)` 去重后 `category.Remove(info.LocalSlot)` → `foreach (var data in lists) CurrentInstance.LoadList(data);` → `return true;`（**不调 LoadItemID**） |
| `LoadList(this ChaListControl instance, ChaListData data)` | `:80` | 转发到 `(ChaListDefine.CategoryNo)data.categoryNo` 重载 |
| `LoadList(this ChaListControl, ChaListDefine.CategoryNo, ChaListData)` | `:83` | 取 `dictListInfo`，`TryGetValue` 命中 → `LoadListInternal` |
| `LoadListInternal(this ChaListControl, Dictionary<int,ListInfoBase> dictData, ChaListData chaListData)` | `:105` | 逐行 `new ListInfoBase().Set(...)` → **`if (!dictData.ContainsKey(listInfoBase.Id))`** 才 `dictData[Id] = listInfoBase;` + `instance.AddItemID(CalculateGlobalID(Category, Id), (byte)GetInfoInt(KeyType.Possess))` |

`Lists.CurrentInstance` 由 `LoadListInfoAllPostHook` → `LoadAllLists` 在启动时设置好，之后一直有效。

> **注入角色列表的最省事路径**：把目标 zipmod 的 `CharaLists` 加进 `Lists.ExternalDataList`，然后调 `Lists.ApplyHotReloadedLists(Array.Empty<ResolveInfo>(), newCharaLists)` —— 或者直接 `ChaListControl::LoadList(data)` 逐条。**注意 `ApplyHotReloadedLists` 不调 `LoadItemID`，要自己补一次。**

**Maker UI 快照问题（已知坑，源码注释原文）**：
`KK_Sideloader/KK.Sideloader.HotReloadAssets.cs` L127-135 注释：
> *「Koikatu's Maker takes a one-time snapshot of each item category when its selection page is first opened. Updating ChaListControl alone therefore leaves previously visited pages stale, while pages opened after a hot reload see the new entries.」*

对应实现：`RefreshMakerSelectionLists(ZipmodHotReloadResult)`（同文件 **L136**）：
- `if (!result.CharacterListsInjected || AffectedCharacterListCategories.Count == 0) return;`
- `FindLoadedSceneComponents<CustomSelectKind>()`（空则 return）
- 7 个反射成员任一为 null → `Logger.LogWarning("Maker selection list hot refresh is not compatible with this game build.")`
- 遍历 `selectKind`，`MakerSelectKindInitializedField.GetValue(...)` 为 false 则 skip；取 `listController`（`CustomSelectListCtrl`）与 `entries`（`MakerSelectInfoListField` 的 IList）
- `processedControllers`（`GetInstanceID()`）与 `processedSurfaces`（`((long)content.GetInstanceID() << 32) ^ (uint)category`）去重

> **对懒加载的含义：Maker 里如果用户已经打开过某类目的选单，之后再注入 mod 列表，那一页不会自动刷新，需要复用 `RefreshMakerSelectionLists` 的逻辑。**

#### Studio 物品列表 —— **需要额外一步重跑 `LoadItemLoadInfoCoroutine`**

Studio 物品调色板读的**就是** `Studio.Info::dicItemLoadInfo`（唯一数据源）。相关读取点：
- `Studio.AddObjectItem::GetLoadInfo(int _group, int _category, int _no)` @IL_0005 — 场景放置物品时
- `Studio.ItemList::InitList(int _group, int _category)` @IL_0061 — 物品调色板填格子时（**每次调用都重新读 `dicItemLoadInfo`**，见 body @IL_005C-0067）
- `Studio.ItemCategoryList::InitList(int _group)` @IL_005F — 读 `dicItemGroupCategory`
- `Studio.ItemGroupList::InitList()` @IL_005A-005F — 读 `dicItemGroupCategory`

`Studio.ItemList::InitList` 唯一调用者：`Studio.ItemCategoryList::OnSelect` @IL_0025。

`dicItemLoadInfo` **只在 `Studio.Info::LoadItemLoadInfo(ExcelData _ed)` 里被写入**（body @IL_0082/0093/00A4/00BC/00D4），而 `LoadItemLoadInfo` 的唯一调用者是 `<LoadItemLoadInfoCoroutine>c__Iterator1::MoveNext` @IL_026F，后者由 `<LoadExcelDataCoroutine>c__Iterator0::MoveNext` @IL_04E5 启动。

> **所以：只往 `Lists.ExternalStudioDataList` 加数据、再 `ChaListControl::LoadList` 是没用的** —— Studio 物品调色板根本不读 `ChaListControl`，它读 `Studio.Info.dicItemLoadInfo`。
>
> **正确做法（二选一）：**
> 1. 反射调 `Studio.Info::LoadItemLoadInfoCoroutine(bundlePath, "ItemList_(\\d*)_(\\d*)_(\\d*)")` 并**把返回的 IEnumerator 跑完**（它会走 `FindAllAssetName` → Sideloader Prefix 命中 `ExternalStudioDataList` → 短路 → 再 `LoadExcelData` → Sideloader Postfix 合并 CSV → `LoadItemLoadInfo` 写入 `dicItemLoadInfo`）。新类目/新 group 还需同样跑 `LoadAnimeGroupInfo` + `LoadAnimeCategoryInfo(bundlePath, "ItemCategory_(\\d*)_(\\d*)", dicItemGroupCategory)`。
> 2. 或直接反射调 `Studio.Info::LoadItemLoadInfo(ExcelData)`，自己构造合并后的 `ExcelData`（要自己实现 Sideloader `LoadExcelDataPostfix` 那段合并 + 删空白行/非数字首行）。
>
> 之后 `Studio.ItemCategoryList::OnSelect` / `ItemList::InitList` 因为**每次重读字典**，用户重新点一下分类就能看到新物品；若要立即刷新已打开的页面，再调一次 `ItemList::InitList(group, category)`。

`RefreshStudioItemAssets`（`KK_Sideloader/KK.Sideloader.HotReloadAssets.cs:1140`）**只负责「已放置在场景里的物件」重新实例化**（`RefreshStudioItemAsset` L1177 → `AddObjectItem.Load(info, null, null, false, -1)`），**不负责往 `dicItemLoadInfo` 里加条目**。它读 `result.AffectedStudioItemSlots`，并要求 `Singleton<Studio.Studio>.IsInstance()`。

#### 结论表

| 目标 | 复用现有设施够不够 |
|---|---|
| 角色卡 item 出现在 `ChaListControl.dictListInfo` | ✅ `Lists.ApplyHotReloadedLists` / `LoadList` 够 |
| Maker 已打开的选单页刷新 | ✅ `RefreshMakerSelectionLists` 够（依赖 7 个反射成员） |
| Studio 物品出现在 `Studio.Info.dicItemLoadInfo` | ❌ **不够**，必须重跑 `LoadItemLoadInfoCoroutine`（或直接 `LoadItemLoadInfo`） |
| Studio 场景里已放置的物件换资产 | ✅ `RefreshStudioItemAssets` / `RefreshStudioItemAsset` 够 |
| UAR slot 解析 | ✅ `UniversalAutoResolver.SetResolveInfos` + `RebuildStudioResolutionLookups`（`Core.UAR.Studio.cs:102`）够，但**必须在 `SceneInfo::Load` 之前完成** |

### D3. `ChaListControl::LoadItemID` 的成本

```csharp
public System.Void ChaListControl::LoadItemID()
```

body（IL 已 dump）：读 `UserData/save/checkitem.dat`（`UserData::get_Path() + "save/checkitem.dat"`）→ `File::Exists` → `FileStream(path, Open, Read)` → `BinaryReader` → `ReadInt32`（条数）→ 循环 `ReadInt32`（pid）+ `ReadByte`（flags）→ `itemIDInfo.TryGetValue` 命中则**仅在 `existing < new` 时覆盖**（`bge` 跳过），未命中则 `Add`。

> **成本 = O(checkitem.dat 的条数) + 一次文件 IO，不是 O(总 item 数)。**
> 它是「持久化的 possess/新物品标记」合并，语义是 **max-merge**，重复调用幂等。

**批量建议**：`LoadList`（内部逐条 `AddItemID`）与 `LoadItemID`（文件合并）职责不同，**逐 mod 调用不会爆，但会有 N 次文件 IO**。推荐：

```
foreach (var data in allNewLists) chaListControl.LoadList(data);   // 只写 dictListInfo + itemIDInfo
chaListControl.LoadItemID();                                        // 最后调一次
```

注意 `LoadAllLists`（`:41`）就是「全部 LoadList 完再 LoadItemID 一次」的模式，而 `ApplyHotReloadedLists`（`:56`）**不调 `LoadItemID`** —— 复用后者时记得自己补。

### D4. 硬性阻塞点清单

| # | 阻塞点 | 证据 | 影响 / 规避 |
|---|---|---|---|
| **1** | **`Studio.Info.isLoadList` 一次性锁** | `Studio.Info::Awake` @IL_0017-18 `set_isLoadList(false)`；`<LoadExcelDataCoroutine>::MoveNext` @IL_006F 判重入、@IL_0AE2 `set_isLoadList(true)`；`set_isLoadList` 只有这 2 个调用者 | 启动后无法再跑完整 `LoadExcelDataCoroutine`。**规避**：反射置 `<isLoadList>k__BackingField = false` 再跑协程（会把 11 个字典全 `Clear()` 重建，很慢），或按 D2 单点重跑 `LoadItemLoadInfoCoroutine` |
| **2** | **`Studio.Info/FileListInfo.dicFile` 是快照** | `FileListInfo::.ctor(List<string>)` 内对每个 bundle 调 `Studio.AssetBundleCheck::GetAllFileName` 并 `dicFile.Add`；`FileListInfo::Check(path, file)` 只查这个字典 | 启动时构建的快照不会自动更新。**规避**：Sideloader 的 `GetAllFileName` Prefix 直接查 `ExternalStudioDataList`（活数据），但 `dicFile` 是 `FileListInfo` 构造时一次性填的 → **新 bundle 需要重建 `FileListInfo`，否则 `Check` 返回 false 会跳过整个类目** |
| **3** | **`Studio.Info/FileCheck.dicConfirmed` 会缓存 false** | `FileCheck::Check(_path)`：`dicConfirmed.TryGetValue` 命中直接返回缓存值；否则 `File.Exists` → `dicConfirmed.Add(_path, result)` | 一旦缓存了 `false`，之后加载 mod 也不会变 true。**规避**：Sideloader Postfix 命中时会 `___dicConfirmed[_path] = true`；自己注入时要**主动写入 dicConfirmed**。仅影响 `LoadAnimeLoadInfo` / `LoadHAnimeLoadInfo`（`FileCheck::Check` 只有这 2 个调用者 @IL_0059） |
| **4** | **`UAR.GetUniqueSlotID()` 是自增计数器** | `Core.UAR.cs:50-53` `Interlocked.Increment(ref CurrentSlotID)`，起始 `BaseSlotID = 100000000`（`:44`） | 懒加载时**加载顺序 = slot 分配顺序**。若卡里存的是 `GUID + 原始 slot`（`TryGetResolutionInfo(int slot, string property, ChaListDefine.CategoryNo)` @`:100`），GUID 查找不受影响；但**只按 slot 查的旧格式**会解析错。**规避**：优先按 GUID 解析；保证同一 mod 的 slot 在本次会话内稳定（`ReuseCharacterSlots` 就是干这个的，`:519`） |
| **5** | **`LoadListInternal` 有 `ContainsKey` 跳过** | `Core.Sideloader.ListLoader.cs:113` `if (!dictData.ContainsKey(listInfoBase.Id))` | 已存在同 Id 的条目**不会覆盖**。重复注入安全，但**先注入的赢** —— 与 UAR slot 分配顺序耦合 |
| **6** | **场景内嵌角色无独立卡路径** | `Studio.OICharInfo::Load(BinaryReader)` @IL_002A → `ChaFileControl::LoadCharaFile(br, …)` | 场景里的角色所需 mod 只能靠**场景卡前置**统一加载，角色卡钩子救不了 |
| **7** | **缺 mod 的 Studio 物件被静默删除** | `Studio.AddObjectItem::Load` @IL_005F `Studio.Studio::DeleteIndex(dicKey)` + 返回 null | 事后补加载无效，必须前置 |
| **8** | **`Studio.Studio::LoadScene(string)` 游戏内 callers=0** | Cecil callers 查询 | 不要指望 patch 它来拦截正常加载流程；实际走 `LoadSceneCoroutine` |
| **9** | `ChaFile::LoadFile(BinaryReader,…)` 拿不到路径的情形 | `LoadFromBytes` / `UploaderScene::GetCharaFileHashBytes` 用 `MemoryStream` | 这些路径只能退回 `ChaFile::get_charaFileName()`（可能为空）或从卡内容里推断 GUID |

### D5. 三大风险（按严重度）

#### 风险 1：Studio 物品调色板数据源与 `ChaListControl` 完全解耦，注入需要重跑游戏协程

`Studio.Info.dicItemLoadInfo` 是 Studio 物品的唯一数据源，只由 `LoadItemLoadInfo` 写入，而 `LoadItemLoadInfo` 只被 `<LoadItemLoadInfoCoroutine>c__Iterator1::MoveNext` @IL_026F 调用，后者只被 `<LoadExcelDataCoroutine>c__Iterator0::MoveNext` @IL_04E5 启动，而后者被 `isLoadList` 一次性锁保护。

**后果**：懒加载的「场景卡前置加载」必须在 `SceneInfo::Load` 的 Prefix 里**同步**完成「加载 zipmod → 注入 `ExternalStudioDataList` → 重跑 `LoadItemLoadInfoCoroutine`（新类目还要重跑 `LoadAnimeGroupInfo` / `LoadAnimeCategoryInfo`）」这一整套，再放行。任何一步异步/延后都会导致场景物件被 `DeleteIndex` 静默丢弃。
**缓解**：只在场景卡确实需要新 mod 时才付这个代价（大多数场景卡只用已加载的 mod）。

#### 风险 2：UAR slot 分配依赖加载顺序，且缺 mod 的物件无法事后补救

`UniversalAutoResolver.GetUniqueSlotID()`（`Core.UAR.cs:50`）是 `Interlocked.Increment` 自增；`ResolveStudioObjects` 在 `SceneInfoLoadHook` → `SceneReadEvent(path)` 那一刻（`stind.ref` @IL_04B8 之后）就用**当前** mod 列表改写 slot。若此时 mod 未加载，解析失败，`AddObjectItem::Load` 会 `GetLoadInfo` 落空 → 回退 `(0,0,0)` → `CommonLib::LoadAsset` 返回 null → `Studio.Studio::DeleteIndex(dicKey)` 静默删掉物件。

**后果**：一旦放行到反序列化，缺 mod 的物件就永久丢失，用户保存场景后原 ID 也回不去了。
**缓解**：(a) 全部前置加载；(b) 或对「前置加载失败」的场景**拒绝加载**并提示，而不是让游戏静默吞掉物件。

#### 风险 3：启动期一次性的缓存快照（`isLoadList` / `FileListInfo.dicFile` / `FileCheck.dicConfirmed`）与 Maker 选单快照

- `Studio.Info.isLoadList` 一旦 true 永不复位（`set_isLoadList` 只有 `Awake` 与协程末尾两处调用）。
- `Studio.Info/FileListInfo.dicFile` 是构造时的 `GetAllFileName` 快照；`FileListInfo::Check` 只查这个字典，**新 bundle 不在快照里就会被整类目跳过**。
- `Studio.Info/FileCheck.dicConfirmed` 会缓存 `false`。
- Maker 侧：`KK.Sideloader.HotReloadAssets.cs` L127-135 注释明确「Koikatu's Maker takes a one-time snapshot of each item category when its selection page is first opened」——`ChaListControl` 更新后已访问过的选单页仍是旧的，必须靠 `RefreshMakerSelectionLists`（L136，依赖 7 个反射成员，任一缺失就整段失效并只 warning）修补。

**后果**：注入路径必须显式处理这 4 个缓存，否则「数据注入了但 UI/加载看不到」，且表现为**静默失败**（只有 warning，没有异常）。
**缓解**：把「注入后必须重建/失效哪些缓存」写成一张显式清单并在注入函数里逐条执行；为 `RefreshMakerSelectionLists` 的反射失败路径加显式告警。

---

## 附录：证据获取方式

`ilscan` 命令（`<repo>\tools\ilscan\bin\Release\net8.0\ilscan.exe`）：

```
ilscan <asm> types   <typeRegex>      列出类型
ilscan <asm> methods <typeRegex>      列出方法签名
ilscan <asm> find    <methodRegex>    按方法名/签名搜
ilscan <asm> callers <methodRegex>    列调用点 + IL 偏移
ilscan <asm> callees <methodRegex>    列被调用
ilscan <asm> body    <methodRegex>    dump 方法体 IL
ilscan <asm> attrs   <typeRegex>      列自定义属性
ilscan <asm> patch   <methodRegex>    dump HarmonyPatch 目标（类型+方法+argumentTypes）
ilscan <asm> fields  <fieldRegex>     列字段
ilscan <asm> fieldrefs <fieldRegex>   列字段读写点 + IL 偏移
ilscan <asm> strlit  <literalRegex>   列 ldstr 字面量 + 所属方法 + IL 偏移
```

asm 别名：`ac`/`asm` = Assembly-CSharp，`kkapi`，`extsave`，`sideloader`，`mat`，`xua`，`0harmony`，`bepinex`，`chalphamask`，`shaderfixer`，`shadermgmt`，`masse`，`resredirect`，`charalistopt`；也可直接传 DLL 绝对路径。

已知坑：
- 字符串在 `Assembly-CSharp.dll` 里是 **UTF-16** 存储；PowerShell 按字节搜要用 `[Text.Encoding]::Unicode`（UTF-8 全返回 -1）。
- PowerShell 内联命令含 `$var.Count` 时需用单引号包整条 `-Command`，否则 bash 会吃掉 `$`。
