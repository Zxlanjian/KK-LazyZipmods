using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKLazyZipmods.CardScan
{
    /// <summary>Which kind of card file was recognised.</summary>
    public enum CardKind
    {
        Unknown = 0,
        Character = 1,
        StudioScene = 2,
        Coordinate = 3
    }

    /// <summary>A single mod reference found inside a card.</summary>
    public sealed class ModReference
    {
        private readonly string _guid;
        private readonly string _pluginKey;
        private readonly int _resolveInfoCount;

        public ModReference(string guid, string pluginKey, int resolveInfoCount)
        {
            _guid = guid;
            _pluginKey = pluginKey;
            _resolveInfoCount = resolveInfoCount;
        }

        /// <summary>zipmod GUID (manifest.xml &lt;guid&gt;).</summary>
        public string Guid
        {
            get { return _guid; }
        }

        /// <summary>ExtSave plugin key the reference came from.</summary>
        public string PluginKey
        {
            get { return _pluginKey; }
        }

        /// <summary>How many ResolveInfo entries referenced this GUID.</summary>
        public int ResolveInfoCount
        {
            get { return _resolveInfoCount; }
        }

        public override string ToString()
        {
            return _guid + " (" + _resolveInfoCount + "x, " + _pluginKey + ")";
        }
    }

    /// <summary>Detailed result of scanning one card file.</summary>
    public sealed class CardScanResult
    {
        private readonly CardKind _kind;
        private readonly List<ModReference> _references = new List<ModReference>();
        private readonly List<string> _pluginKeys = new List<string>();
        private readonly long _fileLength;
        private long _elapsedMilliseconds;

        internal CardScanResult(CardKind kind, long fileLength)
        {
            _kind = kind;
            _fileLength = fileLength;
        }

        internal void SetElapsed(long milliseconds)
        {
            _elapsedMilliseconds = milliseconds;
        }

        public CardKind Kind
        {
            get { return _kind; }
        }

        public long FileLength
        {
            get { return _fileLength; }
        }

        /// <summary>ExtSave plugin keys that were found (whether or not they yielded GUIDs).</summary>
        public List<string> PluginKeys
        {
            get { return _pluginKeys; }
        }

        /// <summary>Every distinct mod reference, ordered by GUID.</summary>
        public List<ModReference> References
        {
            get { return _references; }
        }

        /// <summary>Distinct GUIDs, sorted ordinally.</summary>
        public List<string> Guids
        {
            get
            {
                List<string> guids = new List<string>();
                for (int i = 0; i < _references.Count; i++)
                    guids.Add(_references[i].Guid);
                guids.Sort(StringComparer.Ordinal);
                return guids;
            }
        }

        /// <summary>Time spent scanning, in milliseconds (filled in by the scanner).</summary>
        public long ElapsedMilliseconds
        {
            get { return _elapsedMilliseconds; }
        }

        internal void AddReference(string guid, string pluginKey, int count)
        {
            if (string.IsNullOrEmpty(guid))
                return;
            for (int i = 0; i < _references.Count; i++)
            {
                if (string.Equals(_references[i].Guid, guid, StringComparison.Ordinal))
                    return;
            }
            _references.Add(new ModReference(guid, pluginKey, count));
        }

        internal void AddPluginKey(string key)
        {
            for (int i = 0; i < _pluginKeys.Count; i++)
            {
                if (string.Equals(_pluginKeys[i], key, StringComparison.Ordinal))
                    return;
            }
            _pluginKeys.Add(key);
        }
    }
}
