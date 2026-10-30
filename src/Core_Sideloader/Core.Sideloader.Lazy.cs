using BepInEx.Configuration;
using HarmonyLib;
using KKLazyZipmods.CardScan;
using Sideloader.AutoResolver;
using Sideloader.ListLoader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if KK || KKS || EC
using ChaCustom;
#endif

namespace Sideloader
{
    /// <summary>
    /// Optional lazy list registration. When enabled, the startup pass still enumerates every
    /// zipmod and still registers its AssetBundles and .png indexes, but it does not inject the
    /// zipmod list data (character item lists / Studio lists) into the game. That injection is
    /// the expensive part of starting CharaStudio: the game walks every registered Studio list
    /// bundle afterwards, which costs roughly 25 seconds with a large mods folder.
    ///
    /// The lists are injected later, on demand, right before a character card, coordinate card or
    /// Studio scene that references those zipmods is loaded. The card itself names the zipmods it
    /// needs, so only those get activated.
    /// </summary>
    public partial class Sideloader
    {
        internal static ConfigEntry<bool> LazyLoadingEnabled { get; private set; }
        internal static ConfigEntry<bool> LazyActivateOnCharacterCard { get; private set; }
        internal static ConfigEntry<bool> LazyActivateOnSceneCard { get; private set; }
        internal static ConfigEntry<bool> LazyVerboseLogging { get; private set; }

        /// <summary>GUIDs whose list data has already been injected in this session.</summary>
        private static readonly HashSet<string> LazyActivatedGuids = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Accumulated resolve infos for the activated subset. UniversalAutoResolver.SetResolveInfos
        /// replaces the whole lookup table, so the previous entries have to be carried forward.
        /// </summary>
        private static readonly List<ResolveInfo> LazyResolveInfos = new List<ResolveInfo>();

        /// <summary>Zipmods activated so far, for diagnostics.</summary>
        internal static readonly List<ZipmodInfo> LazyActivatedZipmods = new List<ZipmodInfo>();

        /// <summary>Nested bulk loads (card/scene list thumbnails) must not trigger activation.</summary>
        private static int _lazyBulkLoadDepth;

        /// <summary>Latched by the startup pass once it decided to defer list registration.</summary>
        private static bool _lazyStartupDeferred;

        /// <summary>
        /// True when the startup pass actually deferred list registration, so activation is the only
        /// thing that can bring lists in. Latched rather than re-read from config so that changing the
        /// setting mid-session cannot leave the game with a half populated list pool.
        /// </summary>
        internal static bool LazyDeferredRegistration => _lazyStartupDeferred;

        /// <summary>Called once the startup registration pass is done.</summary>
        internal static void MarkLazyStartupDeferred()
        {
            _lazyStartupDeferred = LazyLoadingEnabled != null && LazyLoadingEnabled.Value;
        }

        private void InitLazyLoadingConfig()
        {
            LazyLoadingEnabled = Config.Bind("Lazy Loading", "Enabled", true,
                "Do not inject zipmod list data into the game at startup. Lists are injected on demand, " +
                "right before a character card, coordinate card or Studio scene that uses them is loaded. " +
                "Drastically shortens startup with a large mods folder. AssetBundles and .png indexes are " +
                "still registered at startup, so nothing else changes.");

            LazyActivateOnCharacterCard = Config.Bind("Lazy Loading", "Activate on character card load", true,
                "Read the zipmod list out of the character card (and coordinate card) that is about to be loaded " +
                "and activate exactly those zipmods first.");

            LazyActivateOnSceneCard = Config.Bind("Lazy Loading", "Activate on Studio scene load", true,
                "Read the zipmod list out of the Studio scene that is about to be loaded and activate exactly " +
                "those zipmods first. Required for scenes that place mod items; without it those items are dropped.");

            LazyVerboseLogging = Config.Bind("Lazy Loading", "Verbose logging", false,
                "Log every lazy activation with the zipmod names involved.");
        }

        /// <summary>
        /// Injects the list data of the given zipmod GUIDs. Main thread only. Idempotent per GUID.
        /// Returns the number of zipmods that were newly activated.
        /// </summary>
        internal static int ActivateGuids(IEnumerable<string> guids, string reason)
        {
            if (!LazyDeferredRegistration || guids == null) return 0;

            var activated = new List<ZipmodInfo>();
            var missing = new List<string>();

            foreach (var guid in guids)
            {
                if (string.IsNullOrEmpty(guid)) continue;
                if (LazyActivatedGuids.Contains(guid)) continue;

                if (!ZipArchives.TryGetValue(guid, out var fileName) || !Zipmods.TryGetValue(fileName, out var zipmod))
                {
                    missing.Add(guid);
                    continue;
                }

                if (!zipmod.Valid)
                {
                    missing.Add(guid);
                    continue;
                }

                LazyActivatedGuids.Add(guid);
                activated.Add(zipmod);
            }

            if (activated.Count == 0)
            {
                if (missing.Count > 0 && LazyVerboseLogging.Value)
                    Logger.LogInfo($"[Lazy] {reason}: no zipmod to activate, {missing.Count} referenced GUID(s) are not installed.");
                return 0;
            }

            var activatedNames = new List<string>();
            var addedResolveInfos = new List<ResolveInfo>();

            foreach (var zipmod in activated)
            {
                try
                {
                    // Lists are already parsed: cache hits deserialize them, cache misses ran
                    // LoadAllLists on the background loading thread. Only the registration was deferred.
                    // The Studio list types that the game reads exactly once were already registered
                    // at startup; what is left is the character half and the per-item Studio lists.
                    AddCharacterLists(zipmod, addedResolveInfos);
                    AddDeferredStudioLists(zipmod);
                    LazyActivatedZipmods.Add(zipmod);
                    activatedNames.Add(zipmod.Manifest.Name ?? zipmod.RelativeFileName);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[Lazy] Failed to register lists of \"{zipmod.RelativeFileName}\", " +
                                    $"its items will stay unavailable: {ex}");
                }
            }

            if (addedResolveInfos.Count > 0)
            {
                LazyResolveInfos.AddRange(addedResolveInfos);
                UniversalAutoResolver.SetResolveInfos(LazyResolveInfos);
            }

            // Character side: the live ChaListControl only exists after ChaListControl.LoadListInfoAll ran.
            // If it does not exist yet the data simply stays in Lists.ExternalDataList and is picked up there.
            // Every live control has to be fed: third party plugins read the manager's control directly
            // instead of going through Lists.CurrentInstance.
            foreach (var chaListControl in GetLiveChaListControls())
            {
                try
                {
                    foreach (var zipmod in activated)
                        foreach (var chaListData in zipmod.CharaLists)
                            chaListControl.LoadList(chaListData);

                    chaListControl.LoadItemID();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[Lazy] Failed to inject character item lists into the live ChaListControl: {ex}");
                }
            }

#if !EC
            // Studio side: dicItemLoadInfo is a completely separate data source from ChaListControl.
            // Only the per-item lists registered by AddDeferredStudioLists need pushing, the
            // once-read panels were already fed by the game's own boot coroutine.
            ActivateStudioLists(activated);
            // Accessory derived items are built from the character lists that just became live.
            RefreshAccessoryStudioItems();
#endif

            if (LazyVerboseLogging.Value)
                Logger.LogInfo($"[Lazy] {reason}: activated {activated.Count} zipmod(s): {string.Join(", ", activatedNames.ToArray())}" +
                               (missing.Count > 0 ? $" | {missing.Count} referenced GUID(s) not installed." : string.Empty));
            else
                Logger.LogInfo($"[Lazy] {reason}: activated {activated.Count} zipmod(s)" +
                               (missing.Count > 0 ? $", {missing.Count} referenced GUID(s) not installed." : "."));

            return activated.Count;
        }

        /// <summary>
        /// The live ChaListControl instances that character item lists have to reach.
        /// Lists.CurrentInstance is the one ChaListControl.LoadListInfoAll last ran on, which is not
        /// necessarily the one Manager.Character hands out. Plugins that derive their own data from
        /// the accessory categories (AccessoriesToStudioItems, for one) read the manager's control
        /// directly, so both have to be fed.
        /// </summary>
        private static List<ChaListControl> GetLiveChaListControls()
        {
            var controls = new List<ChaListControl>();
            if (Lists.CurrentInstance != null) controls.Add(Lists.CurrentInstance);

            try
            {
                var characterType = AccessTools.TypeByName("Manager.Character");
                if (characterType != null)
                {
                    var singletonType = typeof(Singleton<>).MakeGenericType(characterType);
                    var instanceProperty = singletonType.GetProperty("Instance",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.FlattenHierarchy);
                    object manager = instanceProperty == null ? null : instanceProperty.GetValue(null, null);
                    if (manager != null)
                    {
                        var controlProperty = AccessTools.Property(characterType, "chaListCtrl");
                        var control = controlProperty == null
                            ? null
                            : controlProperty.GetValue(manager, null) as ChaListControl;
                        if (control != null && !controls.Contains(control)) controls.Add(control);
                    }
                }
            }
            catch
            {
                // Manager.Character is not up yet; Lists.CurrentInstance is still fed by the caller.
            }

            return controls;
        }

        /// <summary>
        /// Scans a card file for the zipmod GUIDs it references and activates them.
        /// Returns the number of newly activated zipmods.
        /// </summary>
        internal static int ActivateForCardFile(string path, string reason)
        {
            if (!LazyDeferredRegistration) return 0;
            if (string.IsNullOrEmpty(path)) return 0;
            if (!File.Exists(path)) return 0;

            List<string> guids;
            string error;
            if (!CardScanner.TryGetRequiredGuids(path, out guids, out error))
            {
                if (LazyVerboseLogging.Value)
                    Logger.LogInfo($"[Lazy] Could not scan \"{Path.GetFileName(path)}\" ({reason}): {error}");
                return 0;
            }

            if (guids.Count == 0) return 0;
            return ActivateGuids(guids, reason);
        }

        /// <summary>Bulk (thumbnail/list) loads are suppressed so they cannot activate the whole library.</summary>
        internal static void EnterBulkCardLoad() => _lazyBulkLoadDepth++;

        /// <summary>See <see cref="EnterBulkCardLoad"/>.</summary>
        internal static void ExitBulkCardLoad()
        {
            if (_lazyBulkLoadDepth > 0) _lazyBulkLoadDepth--;
        }

        internal static bool SuppressLazyActivation => _lazyBulkLoadDepth > 0;

        // ------------------------------------------------------------------ hooks

        internal static partial class LazyHooks
        {
            internal static void Install(Harmony harmony)
            {
                var loadCharaFile = AccessTools.Method(typeof(ChaFileControl), nameof(ChaFileControl.LoadCharaFile),
                    new[] { typeof(string), typeof(byte), typeof(bool), typeof(bool) });
                if (loadCharaFile != null)
                    harmony.Patch(loadCharaFile, new HarmonyMethod(typeof(LazyHooks).GetMethod(nameof(LoadCharaFilePrefix), AccessTools.all)));

                var loadFileBr = AccessTools.Method(typeof(ChaFile), nameof(ChaFile.LoadFile),
                    new[] { typeof(BinaryReader), typeof(bool), typeof(bool) });
                if (loadFileBr != null)
                    harmony.Patch(loadFileBr, new HarmonyMethod(typeof(LazyHooks).GetMethod(nameof(LoadFilePrefix), AccessTools.all)));

                var loadCoord = AccessTools.Method(typeof(ChaFileCoordinate), nameof(ChaFileCoordinate.LoadFile),
                    new[] { typeof(Stream) });
                if (loadCoord != null)
                    harmony.Patch(loadCoord, new HarmonyMethod(typeof(LazyHooks).GetMethod(nameof(LoadCoordinatePrefix), AccessTools.all)));

                // Suppress activation while the game builds card thumbnail lists.
#if KK || KKS || EC
                PatchBulk(harmony, typeof(CustomCharaFile), nameof(CustomCharaFile.Initialize));
                PatchBulk(harmony, typeof(CustomCoordinateFile), nameof(CustomCoordinateFile.Initialize));
#endif
#if !EC
                PatchBulk(harmony, AccessTools.TypeByName("Studio.CharaList"), "InitCharaList", typeof(bool));
                PatchBulk(harmony, AccessTools.TypeByName("Studio.CharaList"), "InitFemaleList");
                PatchBulk(harmony, AccessTools.TypeByName("Studio.CharaList"), "InitMaleList");
#endif
            }

            private static void PatchBulk(Harmony harmony, Type type, string name, params Type[] args)
            {
                if (type == null) return;
                var method = AccessTools.Method(type, name, args.Length == 0 ? Type.EmptyTypes : args);
                if (method == null) return;
                harmony.Patch(method,
                    new HarmonyMethod(typeof(LazyHooks).GetMethod(nameof(BulkPrefix), AccessTools.all)),
                    new HarmonyMethod(typeof(LazyHooks).GetMethod(nameof(BulkPostfix), AccessTools.all)));
            }

            private static void BulkPrefix() => EnterBulkCardLoad();
            private static void BulkPostfix() => ExitBulkCardLoad();

            /// <summary>Main entry with the file path already known.</summary>
            private static void LoadCharaFilePrefix(string filename)
            {
                if (!LazyActivateOnCharacterCard.Value) return;
                if (SuppressLazyActivation) return;
                if (!ExtensibleSaveFormat.ExtendedSave.LoadEventsEnabled) return;
                ActivateForCardFile(filename, "character card");
            }

            /// <summary>Fallback entry for loads that only provide a stream.</summary>
            private static void LoadFilePrefix(BinaryReader br)
            {
                if (!LazyActivateOnCharacterCard.Value) return;
                if (SuppressLazyActivation) return;
                if (!ExtensibleSaveFormat.ExtendedSave.LoadEventsEnabled) return;

                var fileStream = br?.BaseStream as FileStream;
                if (fileStream == null) return;

                string path;
                try { path = fileStream.Name; }
                catch { return; }

                ActivateForCardFile(path, "character card");
            }

            private static void LoadCoordinatePrefix(Stream st)
            {
                if (!LazyActivateOnCharacterCard.Value) return;
                if (SuppressLazyActivation) return;
                if (!ExtensibleSaveFormat.ExtendedSave.LoadEventsEnabled) return;

                var fileStream = st as FileStream;
                if (fileStream == null) return;

                string path;
                try { path = fileStream.Name; }
                catch { return; }

                ActivateForCardFile(path, "coordinate card");
            }
        }
    }
}
