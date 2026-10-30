using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKLazyZipmods.CardScan
{
    /// <summary>
    /// Reads the zipmod GUIDs a Koikatsu card file references, without loading the game,
    /// without any game assembly and without any NuGet dependency.
    ///
    /// Supported inputs:
    ///   * character cards   (PNG + "【KoiKatuChara】" header)
    ///   * coordinate cards  (PNG + "【KoiKatuClothes】" header)
    ///   * Studio scenes     (PNG + "【KStudio】" header)
    ///
    /// All three carry ExtensibleSaveFormat plugin data. The plugin entry of interest is
    /// UniversalAutoResolver (UAR), plugin key
    /// "com.bepis.sideloader.universalautoresolver" (current) or
    /// "EC.Core.Sideloader.UniversalAutoResolver" (legacy). Its value is [int, map] where
    /// the map holds "info" (character/coordinate ResolveInfo list), "itemInfo" /
    /// "patternInfo" (Studio ResolveInfo lists) and the plain-string keys
    /// "mapInfoGUID" / "filterInfoGUID" (Studio map and filter mods).
    ///
    /// See docs/card-format.md for the measured byte layout.
    /// </summary>
    public static class CardScanner
    {
        /// <summary>Current UAR plugin key.</summary>
        public const string UarPluginKey = "com.bepis.sideloader.universalautoresolver";

        /// <summary>Legacy UAR plugin key (pre-BepInEx5 cards).</summary>
        public const string UarPluginKeyLegacy = "EC.Core.Sideloader.UniversalAutoResolver";

        private const int ScanChunkSize = 1 << 20; // 1 MiB
        private const int ScanCarry = 64;

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static readonly string[] PluginKeys = { UarPluginKey, UarPluginKeyLegacy };

        private static readonly byte[][] PluginKeyBytes =
        {
            Encoding.UTF8.GetBytes(UarPluginKey),
            Encoding.UTF8.GetBytes(UarPluginKeyLegacy)
        };

        // Map keys that hold "List<byte[]> of serialized ResolveInfo".
        private static readonly string[] ResolveInfoArrayKeys = { "info", "itemInfo", "patternInfo" };

        // Map keys that hold a plain GUID string. "rampInfoGUID" is the Studio shader-ramp
        // mod (Core.UAR.Studio.Hooks.cs:315), a third plain-string reference besides the map
        // and the filter.
        private static readonly string[] GuidStringKeys = { "mapInfoGUID", "filterInfoGUID", "rampInfoGUID" };

        // Card-kind markers, all starting with 'K' (0x4B) so one byte filter covers them.
        private static readonly byte[] MarkKStudio = Encoding.ASCII.GetBytes("KStudio");
        private static readonly byte[] MarkKoiKatuChara = Encoding.ASCII.GetBytes("KoiKatuChara");
        private static readonly byte[] MarkKoiKatuClothes = Encoding.ASCII.GetBytes("KoiKatuClothes");

        // ------------------------------------------------------------ public API

        /// <summary>
        /// Reads the distinct zipmod GUIDs referenced by a character, coordinate or Studio card.
        /// Never throws.
        /// </summary>
        /// <param name="filePath">Path to a .png card / scene file.</param>
        /// <param name="guids">Distinct GUIDs, sorted with <see cref="StringComparer.Ordinal"/>.</param>
        /// <param name="error">Null on success, otherwise a human readable reason.</param>
        /// <returns>
        /// False when the file is missing / not a PNG / cannot be read.
        /// True when the file was understood; a card that references no mods yields
        /// an empty list and True.
        /// </returns>
        public static bool TryGetRequiredGuids(string filePath, out List<string> guids, out string error)
        {
            guids = new List<string>();
            error = null;

            CardScanResult result;
            if (!TryScan(filePath, out result, out error))
                return false;

            guids = result.Guids;
            return true;
        }

        /// <summary>
        /// Same as <see cref="TryGetRequiredGuids"/> but also returns the card kind, the
        /// plugin keys seen, per-GUID reference counts and the elapsed milliseconds.
        /// Never throws.
        /// </summary>
        public static bool TryScan(string filePath, out CardScanResult result, out string error)
        {
            result = null;
            error = null;

            if (filePath == null)
            {
                error = "filePath is null.";
                return false;
            }

            int startTicks = Environment.TickCount;

            ByteSource source = null;
            try
            {
                if (!File.Exists(filePath))
                {
                    error = "File not found: " + filePath;
                    return false;
                }

                source = new BufferedFileSource(filePath);

                if (source.Length < PngSignature.Length)
                {
                    error = "File is too small to be a PNG card (" + source.Length + " bytes).";
                    return false;
                }

                if (!HasPngSignature(source))
                {
                    error = "File does not start with the PNG signature.";
                    return false;
                }

                // One whole-file pass collects both the UAR plugin-key offsets and the
                // card-kind markers.
                ScanHits hits = ScanFile(source);

                CardScanResult scan = new CardScanResult(ClassifyKind(hits), source.Length);

                // Validate every candidate key by actually parsing the value behind it.
                // A raw byte scan also hits the key string inside serialized ResolveInfo
                // blobs; those are rejected here because their "value" is not a UAR payload.
                for (int i = 0; i < hits.PluginKeyOffsets.Count; i++)
                {
                    long keyOffset = hits.PluginKeyOffsets[i];
                    string keyName = ReadPluginKeyAt(source, keyOffset);
                    if (keyName == null)
                        continue;

                    long valueOffset = keyOffset + PluginKeyBytes[keyName == UarPluginKey ? 0 : 1].Length;
                    if (ReadUarValue(source, valueOffset, scan, keyName))
                        scan.AddPluginKey(keyName);
                }

                scan.SetElapsed(Environment.TickCount - startTicks);
                result = scan;
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to parse card: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (source != null)
                    source.Dispose();
            }
        }

        // ---------------------------------------------------------- PNG helpers

        private static bool HasPngSignature(ByteSource source)
        {
            source.Position = 0;
            byte[] head = source.ReadBytes(PngSignature.Length);
            if (head.Length < PngSignature.Length)
                return false;
            for (int i = 0; i < PngSignature.Length; i++)
            {
                if (head[i] != PngSignature[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Walks PNG chunks from the start of the file and returns the offset just past the
        /// first IEND chunk, or -1. Only used for diagnostics: the ExtSave payload of a real
        /// card is not appended after the PNG, so the scanner does not depend on this.
        /// </summary>
        internal static long FindFirstPngEnd(ByteSource source)
        {
            long length = source.Length;
            long position = PngSignature.Length;

            byte[] header = new byte[8];
            while (position + 8 <= length)
            {
                source.Position = position;
                source.ReadExact(header, 0, 8);

                long chunkLength = ((long)header[0] << 24) | ((long)header[1] << 16) |
                                   ((long)header[2] << 8) | header[3];
                bool isEnd = header[4] == (byte)'I' && header[5] == (byte)'E' &&
                             header[6] == (byte)'N' && header[7] == (byte)'D';

                position = position + 8 + chunkLength + 4; // data + CRC
                if (isEnd)
                    return position;
                if (position <= 0 || position > length)
                    return -1;
            }

            return -1;
        }

        /// <summary>
        /// Detects the card kind from the markers found by <see cref="ScanFile"/>.
        ///
        /// "【KStudio】" wins outright: neither a character card nor a coordinate card ever
        /// contains it, while a Studio scene almost always also embeds full character cards
        /// (measured: 30 of 40 scenes contain "KoiKatuChara"), so "first marker in the file"
        /// would misreport most scenes as character cards.
        /// </summary>
        internal static CardKind ClassifyKind(ScanHits hits)
        {
            if (hits.SawKStudio)
                return CardKind.StudioScene;
            if (hits.SawKoiKatuClothes)
                return CardKind.Coordinate;
            if (hits.SawKoiKatuChara)
                return CardKind.Character;
            return CardKind.Unknown;
        }

        /// <summary>Head-only kind probe, kept for diagnostics and cheap callers.</summary>
        internal static CardKind DetectKind(ByteSource source)
        {
            return ClassifyKind(ScanFile(source));
        }

        // ------------------------------------------------- whole-file byte scan

        /// <summary>Result of one whole-file pass: UAR key offsets plus card-kind markers.</summary>
        internal sealed class ScanHits
        {
            public readonly List<long> PluginKeyOffsets = new List<long>();
            public bool SawKStudio;
            public bool SawKoiKatuChara;
            public bool SawKoiKatuClothes;
        }

        /// <summary>
        /// Scans the whole file once and returns every UAR plugin-key offset plus which
        /// card-kind markers are present.
        ///
        /// A UAR hit is only recorded when the two bytes in front of the text form a
        /// MessagePack str8 header with exactly the matching length (0xD9 0x2A / 0xD9 0x24).
        /// That check is also the hot filter: it rejects &gt;99.99% of positions before any
        /// byte comparison, which keeps the scan at roughly memory bandwidth.
        /// </summary>
        internal static ScanHits ScanFile(ByteSource source)
        {
            ScanHits hits = new ScanHits();
            long length = source.Length;

            byte[] buffer = new byte[ScanChunkSize + ScanCarry];
            long position = 0;
            int carried = 0;

            while (position < length)
            {
                source.Position = position;
                int want = ScanChunkSize;
                if (position + want > length)
                    want = (int)(length - position);

                int read = 0;
                while (read < want)
                {
                    int n = source.Read(buffer, carried + read, want - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
                if (read <= 0)
                    break;

                int available = carried + read;

                for (int p = 0; p + 2 <= available; p++)
                {
                    byte b = buffer[p];

                    if (b == 0x4B) // 'K'
                    {
                        if (p + MarkKStudio.Length <= available && MatchAt(buffer, p, MarkKStudio))
                        {
                            hits.SawKStudio = true;
                            p += MarkKStudio.Length - 1;
                            continue;
                        }
                        if (p + MarkKoiKatuChara.Length <= available && MatchAt(buffer, p, MarkKoiKatuChara))
                        {
                            hits.SawKoiKatuChara = true;
                            p += MarkKoiKatuChara.Length - 1;
                            continue;
                        }
                        if (p + MarkKoiKatuClothes.Length <= available && MatchAt(buffer, p, MarkKoiKatuClothes))
                        {
                            hits.SawKoiKatuClothes = true;
                            p += MarkKoiKatuClothes.Length - 1;
                            continue;
                        }
                        continue;
                    }

                    // p is the first byte of the key text, so the MessagePack str8 header
                    // sits at p-2 (0xD9) and p-1 (length).
                    if (p < 2 || buffer[p - 2] != 0xD9)
                        continue;

                    byte len = buffer[p - 1];
                    if (len != 0x2A && len != 0x24)
                        continue;

                    for (int k = 0; k < PluginKeyBytes.Length; k++)
                    {
                        byte[] pattern = PluginKeyBytes[k];
                        if (pattern.Length != len)
                            continue;
                        if (p + pattern.Length > available)
                            continue;
                        if (!MatchAt(buffer, p, pattern))
                            continue;

                        // buffer[p] is file offset (position - carried + p): the chunk was
                        // read into buffer[carried..], so the carried prefix shifts indices.
                        hits.PluginKeyOffsets.Add(position - carried + p);
                        p += pattern.Length - 1;
                        break;
                    }
                }

                carried = Math.Min(ScanCarry, available);
                if (carried > 0)
                    Buffer.BlockCopy(buffer, available - carried, buffer, 0, carried);
                position += read;
            }

            hits.PluginKeyOffsets.Sort();
            return hits;
        }

        private static bool MatchAt(byte[] buffer, int offset, byte[] pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (buffer[offset + i] != pattern[i])
                    return false;
            }
            return true;
        }

        /// <summary>Returns every UAR plugin-key offset in the file (diagnostics only).</summary>
        internal static List<long> FindPluginKeys(ByteSource source)
        {
            return ScanFile(source).PluginKeyOffsets;
        }

        private static string ReadPluginKeyAt(ByteSource source, long offset)
        {
            for (int k = 0; k < PluginKeyBytes.Length; k++)
            {
                byte[] pattern = PluginKeyBytes[k];
                if (offset + pattern.Length > source.Length)
                    continue;
                source.Position = offset;
                byte[] actual = source.ReadBytes(pattern.Length);
                if (actual.Length < pattern.Length)
                    continue;
                if (MatchAt(actual, 0, pattern))
                    return PluginKeys[k];
            }
            return null;
        }

        // --------------------------------------------------- UAR value parsing

        /// <summary>
        /// Parses the UAR plugin value at <paramref name="valueOffset"/>.
        ///
        /// Returns false when the bytes there are not a plausible UAR payload. That matters:
        /// a whole-file scan for the key string also hits byte sequences that merely occur
        /// inside serialized ResolveInfo blobs (measured on real scenes: values of "int 99"
        /// and a stray 7-element array). Requiring the value to be an array containing a map,
        /// or a map containing at least one known UAR key, filters those out without ever
        /// dropping real data.
        /// </summary>
        private static bool ReadUarValue(ByteSource source, long valueOffset,
                                         CardScanResult scan, string pluginKey)
        {
            if (valueOffset >= source.Length)
                return false;

            source.Position = valueOffset;
            MsgPackReader reader = new MsgPackReader(source);
            reader.ReadType();

            if (reader.Type == MsgPackType.Array)
            {
                // [int version, map payload]. reader.Count is clobbered by every nested
                // ReadType call, so it must be captured before iterating.
                int elementCount = reader.Count;
                if (elementCount < 1 || elementCount > 16)
                    return false;

                bool sawMap = false;
                bool sawKnownKey = false;
                for (int i = 0; i < elementCount; i++)
                {
                    reader.ReadType();
                    if (i > 0 && reader.Type == MsgPackType.Map)
                    {
                        sawMap = true;
                        if (ReadUarMap(reader, scan, pluginKey))
                            sawKnownKey = true;
                    }
                    else
                    {
                        reader.SkipValue();
                    }
                }
                return sawMap && sawKnownKey;
            }

            if (reader.Type == MsgPackType.Map)
                return ReadUarMap(reader, scan, pluginKey);

            return false;
        }

        /// <summary>
        /// Reads one UAR payload map. Returns true when at least one recognised UAR key was
        /// present, which is the signal that this really is the UAR plugin value.
        /// </summary>
        private static bool ReadUarMap(MsgPackReader reader, CardScanResult scan, string pluginKey)
        {
            int entries = reader.Count;
            if (entries < 0 || entries > 4096)
                return false;

            bool sawKnownKey = false;

            for (int i = 0; i < entries; i++)
            {
                reader.ReadType();
                if (reader.Type != MsgPackType.String)
                {
                    reader.SkipValue();
                    reader.ReadType();
                    reader.SkipValue();
                    continue;
                }

                string key = reader.ReadStringValue();
                reader.ReadType();

                if (IsResolveInfoArrayKey(key))
                {
                    sawKnownKey = true;
                    if (reader.Type == MsgPackType.Array)
                        ReadResolveInfoArray(reader, scan, pluginKey);
                    else
                        reader.SkipValue();
                    continue;
                }

                if (IsGuidStringKey(key))
                {
                    sawKnownKey = true;
                    if (reader.Type == MsgPackType.String)
                        scan.AddReference(Clean(reader.ReadStringValue()), pluginKey, 1);
                    else
                        reader.SkipValue();
                    continue;
                }

                reader.SkipValue();
            }

            return sawKnownKey;
        }

        /// <summary>Walks a MessagePack array of bin8/16/32 blobs, each a serialized ResolveInfo.</summary>
        private static void ReadResolveInfoArray(MsgPackReader reader, CardScanResult scan, string pluginKey)
        {
            int count = reader.Count;
            if (count < 0 || count > 65536)
            {
                reader.SkipValue();
                return;
            }

            for (int i = 0; i < count; i++)
            {
                reader.ReadType();
                if (reader.Type == MsgPackType.Binary)
                    ReadResolveInfo(reader.ReadBinaryValue(), scan, pluginKey);
                else
                    reader.SkipValue();
            }
        }

        /// <summary>
        /// Reads the "ModID" field out of one serialized ResolveInfo / StudioResolveInfo map.
        /// Supports string keys (current writer) and integer keys (positional fallback).
        /// </summary>
        private static void ReadResolveInfo(byte[] blob, CardScanResult scan, string pluginKey)
        {
            if (blob == null || blob.Length == 0)
                return;

            ByteSource memory = null;
            try
            {
                memory = new MemorySource(blob);
                MsgPackReader reader = new MsgPackReader(memory);
                reader.ReadType();
                if (reader.Type != MsgPackType.Map)
                    return;

                int entries = reader.Count;
                if (entries < 0 || entries > 256)
                    return;

                for (int i = 0; i < entries; i++)
                {
                    reader.ReadType();

                    bool isModIdKey = false;
                    if (reader.Type == MsgPackType.String)
                    {
                        isModIdKey = string.Equals(reader.ReadStringValue(), "ModID", StringComparison.Ordinal);
                    }
                    else if (reader.Type == MsgPackType.Integer)
                    {
                        // A MessagePackObject without explicit string keys serialises ModID first.
                        isModIdKey = reader.Integer == 0;
                    }
                    else
                    {
                        reader.SkipValue();
                    }

                    reader.ReadType();
                    if (isModIdKey && reader.Type == MsgPackType.String)
                        scan.AddReference(Clean(reader.ReadStringValue()), pluginKey, 1);
                    else
                        reader.SkipValue();
                }
            }
            finally
            {
                if (memory != null)
                    memory.Dispose();
            }
        }

        private static bool IsResolveInfoArrayKey(string key)
        {
            for (int i = 0; i < ResolveInfoArrayKeys.Length; i++)
            {
                if (string.Equals(key, ResolveInfoArrayKeys[i], StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool IsGuidStringKey(string key)
        {
            for (int i = 0; i < GuidStringKeys.Length; i++)
            {
                if (string.Equals(key, GuidStringKeys[i], StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static string Clean(string guid)
        {
            if (guid == null)
                return null;
            string trimmed = guid.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
