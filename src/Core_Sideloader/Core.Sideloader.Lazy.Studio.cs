#if !EC
using HarmonyLib;
using Sideloader.AutoResolver;
using Sideloader.ListLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Sideloader
{
    public partial class Sideloader
    {
        // Studio keeps its item palette in Studio.Info.dicItemLoadInfo, a completely separate data
        // source from ChaListControl. It is only ever filled by Studio.Info.LoadItemLoadInfo, whose
        // single caller is the LoadItemLoadInfoCoroutine iterator started by
        // Studio.Info.LoadExcelDataCoroutine at boot.
        //
        // The lazy startup pass skips the list registration, so that boot coroutine only sees the
        // vanilla lists. When a scene turns up that uses mod items we have to push those items into
        // dicItemLoadInfo ourselves before Studio reads the scene. Otherwise
        // Studio.AddObjectItem.Load cannot find the item and silently deletes the object.
        //
        // We deliberately do NOT re-run LoadExcelDataCoroutine: it clears eleven dictionaries,
        // re-walks every studio/info bundle and yields a frame every 30ms, which is exactly the
        // 25 second cost we are trying to avoid. Instead we call the individual loaders for the
        // newly activated bundles. None of those loaders consult Studio.Info/FileListInfo, so the
        // fact that its snapshot cannot be rebuilt does not matter here.

        /// <summary>
        /// List types whose loader takes a pre-parsed ExcelData plus a target dictionary.
        /// FieldName is null for loaders that take no dictionary.
        /// </summary>
        private static readonly string[][] StudioExcelDataLoaders =
        {
            // Groups must be registered before the matching category list.
            new[] { "itemgroup", "LoadAnimeGroupInfo", "dicItemGroupCategory" },
            new[] { "animegroup", "LoadAnimeGroupInfo", "dicAGroupCategory" },
            new[] { "voicegroup", "LoadAnimeGroupInfo", "dicVoiceGroupCategory" },
            new[] { "bone", "LoadBoneInfo", "dicBoneInfo" },
            new[] { "bgm", "LoadSoundLoadInfo", "dicBGMLoadInfo" },
            new[] { "map", "LoadMapLoadInfo", "dicMapLoadInfo" },
            new[] { "filter", "LoadSoundLoadInfo", "dicFilterLoadInfo" },
            new[] { "light", "LoadLightLoadInfo", null },
            new[] { "accessorypoint", "LoadAccessoryPointInfo", null },
            new[] { "accessorypointgroup", "LoadAccessoryGroupInfo", null },
        };

        /// <summary>
        /// List types whose loader takes (bundlePath, regex[, dictionary]) and finds the files
        /// itself through Studio.Info.FindAllAssetName (which Sideloader short-circuits onto
        /// Lists.ExternalStudioDataList).
        /// </summary>
        private static readonly string[][] StudioBundleLoaders =
        {
            new[] { "itemcategory", "LoadAnimeCategoryInfo", @"ItemCategory_(\d*)_(\d*)", "dicItemGroupCategory" },
            new[] { "animecategory", "LoadAnimeCategoryInfo", @"AnimeCategory_(\d*)_(\d*)", "dicAGroupCategory" },
            new[] { "voicecategory", "LoadAnimeCategoryInfo", @"VoiceCategory_(\d*)_(\d*)", "dicVoiceGroupCategory" },
            new[] { "itembonelist", "LoadItemBoneInfo", @"ItemBoneList_(\d*)_(\d*)", null },
            new[] { "handanime", "LoadHandAnimeInfo", @"HandAnime_(\d*)_(\d*)", "dicHandAnime" },
        };

        /// <summary>List types whose loader is a coroutine that must be driven to completion.</summary>
        private static readonly string[][] StudioCoroutineLoaders =
        {
            new[] { "itemlist", "LoadItemLoadInfoCoroutine", @"ItemList_(\d*)_(\d*)_(\d*)" },
            new[] { "voice", "LoadVoiceLoadInfoCoroutine", @"Voice_(\d*)_(\d*)_(\d*)" },
        };

        private static Type _studioInfoType;
        private static object _studioInfoInstance;
        private static FieldInfo _studioWaitTimeField;
        private static FieldInfo _studioWaitTimeNextFrameTimeField;
        private static bool _studioReflectionInitialized;
        private static bool _studioReflectionFailed;

        private static void EnsureStudioReflection()
        {
            if (_studioReflectionInitialized) return;
            _studioReflectionInitialized = true;

            try
            {
                _studioInfoType = AccessTools.TypeByName("Studio.Info");
                if (_studioInfoType == null)
                {
                    _studioReflectionFailed = true;
                    return;
                }

                _studioWaitTimeField = AccessTools.Field(_studioInfoType, "waitTime");

                var waitTimeType = _studioInfoType.GetNestedType("WaitTime", BindingFlags.Public | BindingFlags.NonPublic);
                if (waitTimeType != null)
                    _studioWaitTimeNextFrameTimeField = waitTimeType.GetField("nextFrameTime",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception ex)
            {
                _studioReflectionFailed = true;
                Logger.LogError("[Lazy] Studio reflection setup failed, Studio item lists will not be injected on demand: " + ex);
            }
        }

        private static object GetStudioInfoInstance()
        {
            if (_studioInfoInstance != null) return _studioInfoInstance;
            if (_studioInfoType == null) return null;

            try
            {
                var singletonType = typeof(Singleton<>).MakeGenericType(_studioInfoType);
                var instanceProperty = singletonType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (instanceProperty != null)
                    _studioInfoInstance = instanceProperty.GetValue(null, null);
            }
            catch
            {
                // Not available yet; retried on the next activation.
            }

            return _studioInfoInstance;
        }

        /// <summary>
        /// Pushes the Studio lists of the given zipmods into Studio.Info.dicItemLoadInfo and the
        /// related dictionaries. Must run on the main thread before the scene is parsed.
        /// </summary>
        private static void ActivateStudioLists(List<ZipmodInfo> zipmods)
        {
            if (zipmods == null || zipmods.Count == 0) return;
            if (!BepisPlugins.Constants.InsideStudio) return;

            EnsureStudioReflection();
            if (_studioReflectionFailed || _studioInfoType == null) return;

            var info = GetStudioInfoInstance();
            if (info == null)
            {
                // Studio.Info does not exist yet. Its own boot coroutine will pick the data up
                // from Lists.ExternalStudioDataList when it eventually runs.
                return;
            }

            var bundles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var zipmod in zipmods)
            {
                // The once-read list types were registered eagerly at startup and the game's own
                // boot coroutine already turned them into dictionary entries. Re-injecting them
                // here would only cost time.
                foreach (var list in zipmod.StudioLists)
                    if (!IsEagerStudioListType(ListTypeOf(list)))
                        bundles.Add(list.AssetBundleName);

                foreach (var list in zipmod.BoneLists) bundles.Add(list.AssetBundleName);
            }

            if (bundles.Count == 0) return;

            try
            {
                EnsureStudioWaitTime(info);

                foreach (var bundle in bundles)
                    InjectBundleLists(info, bundle);
            }
            catch (Exception ex)
            {
                Logger.LogError("[Lazy] Failed to inject Studio item lists: " + ex);
            }
            finally
            {
                // The internal item id cache is built once and never invalidated by the game.
                // Leaving it stale makes UAR treat every newly injected id as missing.
                Lists._internalStudioItemList = null;
            }
        }

        /// <summary>
        /// The AccessoriesToStudioItems plugin transpiles a call into
        /// Studio.Info.LoadExcelDataCoroutine that converts every accessory in the live
        /// ChaListControl into a Studio item of its own item group. That conversion runs once at
        /// boot, at a point where lazy mode has not injected the character lists yet, so
        /// accessories that come from zipmods never reach dicItemLoadInfo and a scene that
        /// placed one of them loses the object outright.
        ///
        /// Replaying the conversion after the lists are live restores them. The plugin is
        /// invoked reflectively so the dependency stays optional, and it is skipped entirely
        /// when the plugin is not installed.
        /// </summary>
        private static void RefreshAccessoryStudioItems()
        {
            if (!BepisPlugins.Constants.InsideStudio) return;
            if (Lists.CurrentInstance == null) return;

            EnsureStudioReflection();
            if (_studioReflectionFailed || _studioInfoType == null) return;

            var pluginType = AccessTools.TypeByName("AccessoriesToStudioItems.AccessoriesToStudioItemsPlugin");
            if (pluginType == null) return;

            var convert = AccessTools.Method(pluginType, "AddAccsToStudioItems", Type.EmptyTypes);
            if (convert == null) return;

            var info = GetStudioInfoInstance();
            if (info == null) return;

            try
            {
                int group = ReadAccessoryItemGroup(pluginType);

                // The conversion uses Dictionary.Add, so replaying it over the previous output
                // would throw for every entry. Drop the group and rebuild it from scratch.
                var dicField = AccessTools.Field(_studioInfoType, "dicItemLoadInfo");
                var dicItemLoadInfo = dicField == null ? null : dicField.GetValue(info) as IDictionary;
                if (dicItemLoadInfo != null && dicItemLoadInfo.Contains(group))
                    dicItemLoadInfo.Remove(group);

                UniversalAutoResolver.RemoveStudioResolutionInfosByGroup(group);

                object target = null;
                if (!convert.IsStatic)
                {
                    target = UnityEngine.Object.FindObjectOfType(pluginType);
                    if (target == null) return;
                }

                convert.Invoke(target, null);
                Lists._internalStudioItemList = null;
            }
            catch (Exception ex)
            {
                Logger.LogError("[Lazy] Failed to rebuild the accessory Studio items: " + ex);
            }
        }

        /// <summary>Reads the item group the accessory conversion writes into, defaulting to its hardcoded value.</summary>
        private static int ReadAccessoryItemGroup(Type pluginType)
        {
            const int Fallback = 647158;
            try
            {
                var field = AccessTools.Field(pluginType, "AccessoriesAsItemsGroupNumber");
                if (field == null) return Fallback;

                object value = field.GetValue(null);
                return value == null ? Fallback : Convert.ToInt32(value);
            }
            catch
            {
                return Fallback;
            }
        }

        private static void InjectBundleLists(object info, string bundle)
        {
            List<Lists.StudioListData> bundleLists;
            if (!Lists.ExternalStudioDataList.TryGetValue(bundle, out bundleLists) || bundleLists.Count == 0)
                return;

            var byType = new Dictionary<string, List<Lists.StudioListData>>(StringComparer.Ordinal);
            foreach (var data in bundleLists)
            {
                var listType = ListTypeOf(data);
                if (listType.Length == 0) continue;

                // Registered eagerly at startup, so the game already read it once. Do not repeat it.
                if (IsEagerStudioListType(listType)) continue;

                List<Lists.StudioListData> bucket;
                if (!byType.TryGetValue(listType, out bucket))
                {
                    bucket = new List<Lists.StudioListData>();
                    byType[listType] = bucket;
                }
                bucket.Add(data);
            }

            // 1. ExcelData based loaders. Groups first, because LoadAnimeCategoryInfo skips any
            //    group that is not in its dictionary yet.
            foreach (var entry in StudioExcelDataLoaders)
            {
                List<Lists.StudioListData> data;
                if (!byType.TryGetValue(entry[0], out data)) continue;

                var fieldName = entry[2];
                object dict = null;
                if (fieldName != null)
                {
                    var field = AccessTools.Field(_studioInfoType, fieldName);
                    if (field == null) continue;
                    dict = field.GetValue(info);
                    if (dict == null) continue;
                }

                var method = FindStudioMethod(entry[1], fieldName == null ? 1 : 2);
                if (method == null) continue;

                foreach (var listData in data)
                {
                    var excelData = InvokeLoadExcelData(info, bundle, listData.FileNameWithoutExtension);
                    if (excelData == null) continue;
                    InvokeStudioLoader(method, info, fieldName == null
                        ? new object[] { excelData }
                        : new object[] { excelData, dict });
                }
            }

            // 2. Bundle + regex loaders. One call per bundle handles every matching file.
            foreach (var entry in StudioBundleLoaders)
            {
                if (!byType.ContainsKey(entry[0])) continue;

                var fieldName = entry[3];
                object dict = null;
                if (fieldName != null)
                {
                    var field = AccessTools.Field(_studioInfoType, fieldName);
                    if (field == null) continue;
                    dict = field.GetValue(info);
                    if (dict == null) continue;
                }

                var method = FindStudioMethod(entry[1], fieldName == null ? 2 : 3);
                if (method == null) continue;

                InvokeStudioLoader(method, info, fieldName == null
                    ? new object[] { bundle, entry[2] }
                    : new object[] { bundle, entry[2], dict });
            }

            // 3. Coroutine loaders. LoadItemLoadInfoCoroutine is what actually fills dicItemLoadInfo.
            foreach (var entry in StudioCoroutineLoaders)
            {
                if (!byType.ContainsKey(entry[0])) continue;

                var method = AccessTools.Method(_studioInfoType, entry[1], new[] { typeof(string), typeof(string) });
                if (method == null) continue;

                try
                {
                    DrainEnumerator(method.Invoke(info, new object[] { bundle, entry[2] }) as IEnumerator);
                }
                catch (Exception ex)
                {
                    if (LazyVerboseLogging != null && LazyVerboseLogging.Value)
                        Logger.LogInfo("[Lazy] Studio coroutine " + entry[1] + " failed for bundle " + bundle + ": " + ex.Message);
                }
            }
        }

        /// <summary>Finds a Studio.Info instance method by name and parameter count.</summary>
        private static MethodInfo FindStudioMethod(string name, int parameterCount)
        {
            return _studioInfoType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount);
        }

        private static void InvokeStudioLoader(MethodInfo method, object info, object[] args)
        {
            try
            {
                method.Invoke(info, args);
            }
            catch (Exception ex)
            {
                if (LazyVerboseLogging != null && LazyVerboseLogging.Value)
                    Logger.LogInfo("[Lazy] Studio loader " + method.Name + " failed: " + ex.Message);
            }
        }

        private static void EnsureStudioWaitTime(object info)
        {
            if (_studioWaitTimeField == null) return;

            var waitTime = _studioWaitTimeField.GetValue(info);
            if (waitTime != null) return;

            var waitTimeType = _studioInfoType.GetNestedType("WaitTime", BindingFlags.Public | BindingFlags.NonPublic);
            if (waitTimeType == null) return;

            try
            {
                _studioWaitTimeField.SetValue(info, Activator.CreateInstance(waitTimeType, true));
            }
            catch (Exception ex)
            {
                if (LazyVerboseLogging != null && LazyVerboseLogging.Value)
                    Logger.LogInfo("[Lazy] Could not create Studio.Info/WaitTime: " + ex.Message);
            }
        }

        /// <summary>
        /// Drives one of the Studio.Info list coroutines to completion without yielding a frame.
        /// The coroutine waits on Studio.Info/WaitTime (30ms per step); zeroing nextFrameTime
        /// makes get_isOver() return true immediately, so the whole thing runs in one go.
        /// </summary>
        private static void DrainEnumerator(IEnumerator enumerator)
        {
            if (enumerator == null) return;

            object waitTime = null;
            if (_studioWaitTimeField != null)
                waitTime = _studioWaitTimeField.GetValue(_studioInfoInstance);

            int guard = 0;
            while (true)
            {
                if (_studioWaitTimeNextFrameTimeField != null && waitTime != null)
                {
                    try { _studioWaitTimeNextFrameTimeField.SetValue(waitTime, 0f); }
                    catch { /* the guard below still bounds the loop */ }
                }

                bool moved;
                try { moved = enumerator.MoveNext(); }
                catch (Exception ex)
                {
                    if (LazyVerboseLogging != null && LazyVerboseLogging.Value)
                        Logger.LogInfo("[Lazy] Studio list coroutine threw: " + ex.Message);
                    return;
                }

                if (!moved) return;

                // The coroutines take one step per list file, but never spin forever.
                if (++guard > 200000) return;
            }
        }

        private static ExcelData InvokeLoadExcelData(object info, string bundle, string fileName)
        {
            var method = AccessTools.Method(_studioInfoType, "LoadExcelData", new[] { typeof(string), typeof(string) });
            if (method == null) return null;

            try
            {
                return method.Invoke(info, new object[] { bundle, fileName }) as ExcelData;
            }
            catch (Exception ex)
            {
                if (LazyVerboseLogging != null && LazyVerboseLogging.Value)
                    Logger.LogInfo("[Lazy] LoadExcelData failed for " + bundle + "/" + fileName + ": " + ex.Message);
                return null;
            }
        }

        private static string ListTypeOf(Lists.StudioListData data)
        {
            var name = data.FileNameWithoutExtension;
            if (string.IsNullOrEmpty(name)) return string.Empty;

            int underscore = name.IndexOf('_');
            return (underscore < 0 ? name : name.Substring(0, underscore)).ToLowerInvariant();
        }

        /// <summary>Patches Studio.SceneInfo.Load so scenes can activate their zipmods first.</summary>
        internal static void InstallStudioLazyHooks(Harmony harmony)
        {
            var sceneInfoType = AccessTools.TypeByName("Studio.SceneInfo");
            if (sceneInfoType == null) return;

            var load = AccessTools.Method(sceneInfoType, "Load", new[] { typeof(string), typeof(Version).MakeByRefType() });
            if (load == null) return;

            harmony.Patch(load, new HarmonyMethod(typeof(Sideloader).GetMethod(nameof(SceneInfoLoadPrefix), AccessTools.all)));
        }

        /// <summary>
        /// Runs before Studio parses the scene file. Everything Studio needs to resolve mod
        /// objects has to be in place before the scene body runs, because a mod object that
        /// cannot be resolved is deleted outright and cannot be restored afterwards.
        /// </summary>
        private static void SceneInfoLoadPrefix(string _path)
        {
            if (!LazyActivateOnSceneCard.Value) return;
            if (!LazyDeferredRegistration) return;

            ActivateForCardFile(_path, "Studio scene");
        }
    }
}
#endif
