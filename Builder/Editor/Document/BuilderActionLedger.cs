#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace Ruitk.Builder
{
    /// <summary>
    /// UB-73: one ordered log of every builder action, with undo/redo that walks
    /// it atomically ACROSS files.
    ///
    /// The per-file <see cref="BuilderDocumentSession"/> stacks are still the
    /// buffer's own history, but they cannot express a user gesture: a drop that
    /// inserts a tag in one file and an import line in another is two session
    /// edits and one ACTION, and undoing it file-by-file leaves the tree in a
    /// state the user never authored. An entry here owns the whole set of
    /// (file, before, after) triples a single gesture produced, so one Ctrl+Z
    /// reverts all of them or none.
    ///
    /// Redo is the tail past the cursor. Recording a new action truncates it,
    /// which is the standard linear-history rule — a branch the user walked away
    /// from is not reachable again.
    /// </summary>
    internal sealed class BuilderActionLedger
    {
        internal sealed class Change
        {
            public string FilePath;
            public string Before;
            public string After;

            /// <summary>A pending-deletion mark rather than a buffer rewrite.
            /// Undo un-marks it, redo re-marks it — nothing on disk moves either
            /// way, because the deletion itself only happens at Save.</summary>
            public bool IsDeletion;

            /// <summary>A pending NEW module. Undo closes the never-saved
            /// session, redo re-opens it from <see cref="After"/>. Nothing on
            /// disk moves either way — the file is only written at Save.</summary>
            public bool IsCreation;
        }

        internal sealed class Entry
        {
            public string Description;
            public DateTime At;
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

        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>Entries BELOW the cursor are applied; entries at or above it
        /// have been undone and form the redo tail.</summary>
        private int _cursor;

        private Entry _open;
        private int _depth;

        /// <summary>Suppresses recording while the ledger itself is rewriting
        /// buffers — an undo must not be logged as a new action.</summary>
        public bool Replaying { get; private set; }

        public event Action Changed;

        public IReadOnlyList<Entry> Entries => _entries;

        public int Cursor => _cursor;

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
            _open.Changes.Add(new Change { FilePath = filePath, Before = before, After = after });
            if (standalone)
                Commit();
        }

        /// <summary>Records that a file was marked for deletion. Carries no text
        /// because none is needed: the file is still on disk until Save.</summary>
        public void RecordDeletion(string filePath)
        {
            if (Replaying || string.IsNullOrEmpty(filePath))
                return;
            bool standalone = _open == null;
            if (standalone)
                _open = new Entry { Description = "delete", At = DateTime.Now };
            _open.Changes.Add(new Change { FilePath = filePath, IsDeletion = true });
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
            _open.Changes.Add(new Change { FilePath = filePath, IsCreation = true });
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
            Changed?.Invoke();
        }

        private const int MaxEntries = 400;

        /// <summary>Steps the cursor back one entry and hands the caller every
        /// (file, text) pair to restore. Null when there is nothing to undo.</summary>
        public Entry Undo()
        {
            if (!CanUndo)
                return null;
            _cursor--;
            Changed?.Invoke();
            return _entries[_cursor];
        }

        public Entry Redo()
        {
            if (!CanRedo)
                return null;
            var entry = _entries[_cursor];
            _cursor++;
            Changed?.Invoke();
            return entry;
        }

        /// <summary>Walks the cursor to <paramref name="target"/>, returning the
        /// buffer writes needed, in order. Used by the history panel, where a
        /// click can cross several entries at once.</summary>
        public List<(string FilePath, string Text)> WalkTo(int target)
        {
            var writes = new List<(string, string)>();
            if (target < 0)
                target = 0;
            if (target > _entries.Count)
                target = _entries.Count;
            while (_cursor > target)
            {
                _cursor--;
                var entry = _entries[_cursor];
                for (int i = entry.Changes.Count - 1; i >= 0; i--)
                    writes.Add((entry.Changes[i].FilePath, entry.Changes[i].Before));
            }
            while (_cursor < target)
            {
                var entry = _entries[_cursor];
                _cursor++;
                foreach (var change in entry.Changes)
                    writes.Add((change.FilePath, change.After));
            }
            if (writes.Count > 0)
                Changed?.Invoke();
            return writes;
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
            Changed?.Invoke();
        }
    }
}
#endif
