#if UNITY_EDITOR
using System;
using UnityEngine;
using System.Collections.Generic;

namespace Ruitk.Builder
{
    /// <summary>
    /// UB-73: one ordered log of every builder action, with undo/redo that walks
    /// it atomically ACROSS files.
    ///
    /// A per-file history cannot express a user gesture: a drop that inserts a
    /// tag in one file and an import line in another is two edits and one
    /// ACTION, and undoing it file-by-file leaves the tree in a state the user
    /// never authored. An entry here owns the whole set of (file, before, after)
    /// triples a single gesture produced, so one Ctrl+Z reverts all of them or
    /// none.
    ///
    /// The ledger SURVIVES a domain reload, because the tree does. It used to be
    /// NonSerialized on the reasoning that "an undo whose other half was compiled
    /// away is worse than no undo" - true of a live object reference, but an
    /// entry is (file, before, after) TEXT, which reloads perfectly well. Dropping
    /// it meant an unsaved tree kept an hour of work with no way to step back
    /// through it, and every recompile - including one caused by a package edit
    /// the user did not make - silently threw the history away.
    ///
    /// What does NOT survive is the OPEN entry (a gesture interrupted mid-flight)
    /// and any entry naming a file the restored tree no longer has: see
    /// <see cref="PruneMissing"/>. Those are the cases the old comment was right
    /// about, and they are handled by name rather than by discarding everything.
    ///
    /// Redo is the tail past the cursor. Recording a new action truncates it,
    /// which is the standard linear-history rule — a branch the user walked away
    /// from is not reachable again.
    /// </summary>
    [Serializable]
    internal sealed class BuilderActionLedger
    {
        [Serializable]
        internal sealed class Change
        {
            public string FilePath;
            public string Before;
            public string After;

            /// <summary>A module LEAVING the tree. Undo puts it back and redo
            /// removes it again; nothing on disk moves either way, because the
            /// file is only trashed at Save.</summary>
            public bool IsDeletion;

            /// <summary>A NEW module. Undo removes it from the tree, redo puts it
            /// back from <see cref="After"/>. Nothing on disk moves either way -
            /// the file is only written at Save.</summary>
            public bool IsCreation;

            /// <summary>A pending MOVE. <see cref="Before"/> is the path the
            /// module came from and <see cref="After"/> is where it went;
            /// undo and redo just move it the other way. Nothing on disk
            /// moves either way - the projection happens at Save.</summary>
            public bool IsMove;

            /// <summary>The module a deletion removed, held so undo can put the
            /// SAME module back - its identity, its buffer and its DiskPath. A
            /// deletion used to be a mark that undo cleared; now the module
            /// genuinely leaves the tree, so undo needs the thing itself.</summary>
            public BuilderModule Removed;

            /// <summary>The module's stable identity at record time. A ledger entry
            /// outlives the PATH it was recorded against - a rename moves the module,
            /// and a replay that looked the session up by path then wrote to a name
            /// nothing answered to. Replay resolves identity first.</summary>
            public string ModuleId;
        }

        [Serializable]
        internal sealed class Entry
        {
            public string Description;

            /// <summary>Unity serializes fields, not DateTime, so the timestamp
            /// travels as ticks and <see cref="At"/> stays the API.</summary>
            public long AtTicks;

            public DateTime At
            {
                get => AtTicks == 0 ? DateTime.MinValue : new DateTime(AtTicks);
                set => AtTicks = value.Ticks;
            }

            /// <summary>Free typing, as opposed to a discrete gesture. Consecutive
            /// keystrokes in the same file merge into one of these.</summary>
            public bool IsTyping;
            public List<Change> Changes = new List<Change>();

            public string FileSummary
            {
                get
                {
                    if (Changes.Count == 0)
                        return "";
                    string first = System.IO.Path.GetFileName(Changes[0].FilePath);
                    return Changes.Count == 1 ? first : first + " +" + (Changes.Count - 1);
                }
            }
        }

        [SerializeField] private List<Entry> _entries = new List<Entry>();

        /// <summary>Entries BELOW the cursor are applied; entries at or above it
        /// have been undone and form the redo tail.</summary>
        [SerializeField] private int _cursor;

        /// <summary>The gesture currently being recorded. Deliberately NOT
        /// serialized: a reload lands mid-gesture at most once, and a half-open
        /// entry restored as if complete would undo half a move.</summary>
        [NonSerialized] private Entry _open;

        /// <summary>Resolves a path to the owning module's stable identity, set by
        /// the window. Capturing it at record time is what makes replay immune to
        /// the paths moving underneath it.</summary>
        [NonSerialized] internal Func<string, string> IdOf;
        [NonSerialized] private int _depth;

        /// <summary>Suppresses recording while the ledger itself is rewriting
        /// buffers — an undo must not be logged as a new action.</summary>
        [NonSerialized] private bool _replaying;

        public bool Replaying
        {
            get => _replaying;
            private set => _replaying = value;
        }

        [NonSerialized] private Action _changed;

        public event Action Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public IReadOnlyList<Entry> Entries => _entries;

        public int Cursor => _cursor;

        /// <summary>
        /// Drops history that the restored tree cannot honour: entries naming a
        /// file no longer in it. An undo that would write into a module that does
        /// not exist is the failure the old no-serialization rule avoided by
        /// throwing everything away; this removes only the entries that actually
        /// have the problem.
        ///
        /// An entry is dropped whole. Half-applying a gesture would leave the
        /// tree in a state the user never authored, which is the one thing undo
        /// must never do. If dropping breaks the run, the cursor is clamped so
        /// what remains is still a straight line.
        /// </summary>
        public int PruneMissing(Func<string, bool> stillPresent)
        {
            if (stillPresent == null || _entries.Count == 0)
                return 0;
            int removed = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                bool ok = true;
                foreach (var change in _entries[i].Changes)
                {
                    if (change.IsCreation || change.IsDeletion || change.IsMove)
                        continue;
                    if (!stillPresent(change.FilePath))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    continue;
                _entries.RemoveAt(i);
                if (i < _cursor)
                    _cursor--;
                removed++;
            }
            if (_cursor > _entries.Count)
                _cursor = _entries.Count;
            if (_cursor < 0)
                _cursor = 0;
            if (removed > 0)
                _changed?.Invoke();
            return removed;
        }

        public bool CanUndo => _cursor > 0;

        public bool CanRedo => _cursor < _entries.Count;

        public string UndoLabel => CanUndo ? _entries[_cursor - 1].Description : null;

        public string RedoLabel => CanRedo ? _entries[_cursor].Description : null;

        /// <summary>Opens a grouping scope. Nested Begin/End pairs collapse into
        /// the OUTERMOST one, so a compound gesture that internally reuses a
        /// single-file primitive still lands as one entry.</summary>
        public void Begin(string description)
        {
            if (Replaying)
                return;
            _depth++;
            if (_open == null)
                _open = new Entry { Description = description ?? "edit", At = DateTime.Now };
            else if (!string.IsNullOrEmpty(description) && _depth == 1)
                _open.Description = description;
        }

        /// <summary>How long a typing burst stays open for merging. Only affects
        /// UNDO GRANULARITY - nothing downstream is timed off it.</summary>
        private static readonly TimeSpan TypingWindow = TimeSpan.FromSeconds(1.5);

        /// <summary>Records free typing. Consecutive keystrokes in the same file
        /// merge into ONE entry instead of one entry per character, which is what
        /// the source pane produced - a hundred history rows for typing a name, and
        /// a Ctrl+Z that walked back one letter at a time.
        ///
        /// Merging only happens at the tip of the history and outside any gesture
        /// scope: an undo moves the cursor, and a compound action owns its own
        /// entry, so neither can be silently extended by the next keystroke.</summary>
        public void RecordTyping(string filePath, string before, string after)
        {
            if (Replaying || string.IsNullOrEmpty(filePath))
                return;
            if (string.Equals(before, after, StringComparison.Ordinal))
                return;
            if (_open != null)
            {
                Record(filePath, before, after);
                return;
            }

            var last = _cursor > 0 && _cursor == _entries.Count ? _entries[_cursor - 1] : null;
            if (last != null && last.IsTyping && last.Changes.Count == 1
                && string.Equals(
                    last.Changes[0].FilePath, filePath, StringComparison.OrdinalIgnoreCase)
                && DateTime.Now - last.At < TypingWindow)
            {
                last.Changes[0].After = after;
                last.At = DateTime.Now;
                _changed?.Invoke();
                return;
            }

            _open = new Entry
            {
                Description = "type in " + System.IO.Path.GetFileName(filePath),
                At = DateTime.Now,
                IsTyping = true,
            };
            _open.Changes.Add(new Change
            {
                FilePath = filePath, ModuleId = IdOf?.Invoke(filePath),
                Before = before, After = after,
            });
            Commit();
        }

        public void Record(string filePath, string before, string after)
        {
            if (Replaying || string.IsNullOrEmpty(filePath))
                return;
            if (string.Equals(before, after, StringComparison.Ordinal))
                return;
            bool standalone = _open == null;
            if (standalone)
                _open = new Entry { Description = "edit", At = DateTime.Now };
            // A gesture that writes the same file twice keeps ONE change whose
            // Before is the state the gesture started from.
            foreach (var existing in _open.Changes)
            {
                if (!string.Equals(existing.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                existing.After = after;
                if (standalone)
                    Commit();
                return;
            }
            _open.Changes.Add(new Change
            {
                FilePath = filePath, ModuleId = IdOf?.Invoke(filePath),
                Before = before, After = after,
            });
            if (standalone)
                Commit();
        }

        /// <summary>Records a module changing PATH as ONE change, rather than an
        /// unrelated creation and deletion. The pair was never two events: it is
        /// one module in two places, and describing it as two is what let undo
        /// put the module back without its history.</summary>
        public void RecordMove(string fromPath, string toPath)
        {
            if (Replaying || string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath))
                return;
            bool standalone = _open == null;
            if (standalone)
                _open = new Entry { Description = "rename", At = DateTime.Now };
            _open.Changes.Add(new Change
            {
                FilePath = toPath,
                ModuleId = IdOf?.Invoke(fromPath),
                Before = fromPath,
                After = toPath,
                IsMove = true,
            });
            if (standalone)
                Commit();
        }

        /// <summary>Records that a file was marked for deletion. Carries no text
        /// because none is needed: the file is still on disk until Save.</summary>
        public void RecordDeletion(string filePath, BuilderModule removed = null)
        {
            if (Replaying || string.IsNullOrEmpty(filePath))
                return;
            bool standalone = _open == null;
            if (standalone)
                _open = new Entry { Description = "delete", At = DateTime.Now };
            _open.Changes.Add(new Change
            {
                FilePath = filePath, ModuleId = IdOf?.Invoke(filePath),
                IsDeletion = true, Removed = removed,
            });
            if (standalone)
                Commit();
        }

        /// <summary>Records a new module. The text rides along so redo can
        /// re-open the session with exactly what the user had.</summary>
        public void RecordCreation(string filePath)
        {
            if (Replaying || string.IsNullOrEmpty(filePath))
                return;
            bool standalone = _open == null;
            if (standalone)
                _open = new Entry { Description = "create", At = DateTime.Now };
            _open.Changes.Add(new Change
            {
                FilePath = filePath, ModuleId = IdOf?.Invoke(filePath), IsCreation = true,
            });
            if (standalone)
                Commit();
        }

        /// <summary>Closes the outermost scope and pushes it. An empty scope is
        /// dropped — a gesture the user cancelled leaves no history.</summary>
        public void End()
        {
            if (Replaying)
                return;
            if (_depth > 0)
                _depth--;
            if (_depth > 0)
                return;
            Commit();
        }

        private void Commit()
        {
            var entry = _open;
            _open = null;
            _depth = 0;
            if (entry == null || entry.Changes.Count == 0)
                return;
            if (_cursor < _entries.Count)
                _entries.RemoveRange(_cursor, _entries.Count - _cursor);
            _entries.Add(entry);
            _cursor = _entries.Count;
            if (_entries.Count > MaxEntries)
            {
                int drop = _entries.Count - MaxEntries;
                _entries.RemoveRange(0, drop);
                _cursor -= drop;
            }
            _changed?.Invoke();
        }

        private const int MaxEntries = 400;

        /// <summary>Steps the cursor back one entry and returns it, for the caller
        /// to replay in reverse. Null when there is nothing to undo.</summary>
        public Entry Undo()
        {
            if (!CanUndo)
                return null;
            _cursor--;
            _changed?.Invoke();
            return _entries[_cursor];
        }

        public Entry Redo()
        {
            if (!CanRedo)
                return null;
            var entry = _entries[_cursor];
            _cursor++;
            _changed?.Invoke();
            return entry;
        }

        public IDisposable Suppress() => new Suppression(this);

        private sealed class Suppression : IDisposable
        {
            private readonly BuilderActionLedger _ledger;
            private readonly bool _was;

            public Suppression(BuilderActionLedger ledger)
            {
                _ledger = ledger;
                _was = ledger.Replaying;
                ledger.Replaying = true;
            }

            public void Dispose() => _ledger.Replaying = _was;
        }

        public void Clear()
        {
            _entries.Clear();
            _cursor = 0;
            _open = null;
            _depth = 0;
            _changed?.Invoke();
        }
    }
}
#endif
