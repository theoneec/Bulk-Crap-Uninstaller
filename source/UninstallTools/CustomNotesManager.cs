using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Klocman.Tools;

// Modified 2026 (BCU personal fork, theoneec): diagnostics go to stderr instead of
// stdout (keeps `bcu ... --format json` output clean); added NotesFile, GetAllNotes
// and TrySetNote so the CLI can list notes and report save failures.

namespace UninstallTools
{
    public static class CustomNotesManager
    {
        private static readonly string NotesFilePath = Path.Combine(UninstallToolsGlobalConfig.AssemblyLocation, "CustomNotes.xml");
        private static Dictionary<string, string> _notesCache = new Dictionary<string, string>();

        static CustomNotesManager()
        {
            if (File.Exists(NotesFilePath))
            {
                try
                {
                    var loaded = SerializationTools.DeserializeFromXml<List<NoteEntry>>(NotesFilePath);
                    if (loaded != null)
                    {
                        _notesCache = loaded
                            .Where(x => !string.IsNullOrEmpty(x.CacheId) && !string.IsNullOrEmpty(x.Note))
                            .ToDictionary(x => x.CacheId, x => x.Note);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Failed to load CustomNotes.xml: " + ex.Message);
                }
            }
        }

        public static string GetNote(string cacheId)
        {
            if (string.IsNullOrEmpty(cacheId)) return string.Empty;
            return _notesCache.TryGetValue(cacheId, out var note) ? note : string.Empty;
        }

        /// <summary>Full path of the XML file the notes are stored in.</summary>
        public static string NotesFile => NotesFilePath;

        /// <summary>Snapshot of every stored note, keyed by the entry's cache id.</summary>
        public static IReadOnlyDictionary<string, string> GetAllNotes() => new Dictionary<string, string>(_notesCache);

        public static void SetNote(string cacheId, string note) => TrySetNote(cacheId, note, out _);

        /// <summary>Like <see cref="SetNote"/>, but reports whether the notes file could be written.</summary>
        public static bool TrySetNote(string cacheId, string note, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(cacheId)) { error = "Entry has no cache id"; return false; }

            if (string.IsNullOrEmpty(note))
                _notesCache.Remove(cacheId);
            else
                _notesCache[cacheId] = note;

            return Save(out error);
        }

        private static bool Save(out string error)
        {
            try
            {
                var listToSave = _notesCache.Select(kv => new NoteEntry { CacheId = kv.Key, Note = kv.Value }).ToList();
                SerializationTools.SerializeToXml(NotesFilePath, listToSave);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Console.Error.WriteLine("Failed to save CustomNotes.xml: " + ex.Message);
                return false;
            }
        }

        public class NoteEntry
        {
            public string CacheId { get; set; }
            public string Note { get; set; }
        }
    }
}
