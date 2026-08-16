#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace Ruitk.Builder
{
    /// <summary>
    /// One open <c>.uitkx</c> document inside the RUITK Builder: the on-disk text
    /// at open time, the live buffer every edit rewrites, and the session-scoped
    /// undo/redo stacks. NOTHING here touches disk — the save/abort decision
    /// belongs to <see cref="BuilderWorkspace"/> (owner decision VE-D2).
    ///
    /// Buffers are LF-normalized internally (the language pipeline's printer
    /// contract rejects CR); the original file's EOL flavor is recorded so a
    /// Save can write bytes matching what the file used before.
    /// </summary>
    [Serializable]
    public sealed class BuilderDocumentSession
    {
        public string FilePath;
        public string DiskText;
        public string BufferText;
        public bool IsReadOnly;
        public bool UsedCrlf;

        [NonSerialized] private List<string> _undo = new List<string>();
        [NonSerialized] private List<string> _redo = new List<string>();

        public bool IsDirty =>
            !string.Equals(BufferText, DiskText, StringComparison.Ordinal);

        /// <summary>True when this session was created in the builder and never saved.</summary>
        public bool IsNewFile;

        public static string NormalizeLf(string text) =>
            text == null ? string.Empty : text.Replace("\r\n", "\n").Replace("\r", "\n");

        public static BuilderDocumentSession Open(string filePath, string diskTextRaw, bool isReadOnly)
        {
            return new BuilderDocumentSession
            {
                FilePath = filePath,
                DiskText = NormalizeLf(diskTextRaw),
                BufferText = NormalizeLf(diskTextRaw),
                IsReadOnly = isReadOnly,
                UsedCrlf = diskTextRaw != null && diskTextRaw.Contains("\r\n"),
                IsNewFile = false,
            };
        }

        public static BuilderDocumentSession CreateNew(string filePath, string initialBuffer)
        {
            return new BuilderDocumentSession
            {
                FilePath = filePath,
                DiskText = string.Empty,
                BufferText = NormalizeLf(initialBuffer),
                IsReadOnly = false,
                UsedCrlf = false,
                IsNewFile = true,
            };
        }

        /// <summary>
        /// Commits an edit transaction: pushes the current buffer onto the undo
        /// stack and replaces it. Rejected on read-only sessions — callers must
        /// gate the UI, this is the last line of defense.
        /// </summary>
        public void ApplyEdit(string newBufferLf)
        {
            if (IsReadOnly)
                throw new InvalidOperationException(
                    $"'{FilePath}' is read-only (immutable package) - the builder must not edit it.");
            if (newBufferLf == null)
                throw new ArgumentNullException(nameof(newBufferLf));
            if (newBufferLf.IndexOf('\r') >= 0)
                throw new ArgumentException("builder buffers are LF-normalized", nameof(newBufferLf));
            if (string.Equals(newBufferLf, BufferText, StringComparison.Ordinal))
                return;

            EnsureStacks();
            _undo.Add(BufferText);
            _redo.Clear();
            BufferText = newBufferLf;
        }

        public bool CanUndo { get { EnsureStacks(); return _undo.Count > 0; } }
        public bool CanRedo { get { EnsureStacks(); return _redo.Count > 0; } }

        public void Undo()
        {
            EnsureStacks();
            if (_undo.Count == 0)
                return;
            _redo.Add(BufferText);
            BufferText = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
        }

        public void Redo()
        {
            EnsureStacks();
            if (_redo.Count == 0)
                return;
            _undo.Add(BufferText);
            BufferText = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
        }

        /// <summary>Marks the buffer as persisted (Save wrote it) or discarded (Abort).</summary>
        public void MarkClean(string newDiskTextLf)
        {
            DiskText = newDiskTextLf;
            BufferText = newDiskTextLf;
            IsNewFile = false;
        }

        /// <summary>External change (git pull, sync, IDE edit) under a CLEAN
        /// session: adopt the new disk text so open cards and the source pane
        /// never keep serving a stale buffer. The caller enforces the
        /// dirty-session policy (unsaved edits are never clobbered). The undo
        /// history belongs to the abandoned lineage and is cleared. Returns
        /// true when the text actually changed.</summary>
        public bool AdoptDiskText(string diskTextRaw)
        {
            string lf = NormalizeLf(diskTextRaw);
            if (string.Equals(lf, DiskText, StringComparison.Ordinal))
                return false;
            DiskText = lf;
            BufferText = lf;
            UsedCrlf = diskTextRaw != null && diskTextRaw.Contains("\r\n");
            EnsureStacks();
            _undo.Clear();
            _redo.Clear();
            return true;
        }

        // Undo/redo stacks are deliberately [NonSerialized]: only buffers + dirty
        // state must survive a domain reload (plan §4); Unity's window serializer
        // round-trips this object, and after a reload the fields come back null.
        private void EnsureStacks()
        {
            _undo ??= new List<string>();
            _redo ??= new List<string>();
        }
    }
}
#endif
