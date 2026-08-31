using System;

namespace Ruitk.Builder
{
    /// <summary>
    /// One source-pane edit session: the buffer snapshot taken when "edit" was
    /// pressed, AND the file it was taken from.
    ///
    /// The two used to be separate - a bare snapshot string, with every consumer
    /// assuming it belonged to whatever was focused at the moment they ran. It
    /// does not. Focus moves while a session is open, and cancelling then wrote
    /// one module's entire text into ANOTHER module's buffer: a card that said
    /// MiddleSide holding NewComponent, imports and all, while the file on disk
    /// stayed perfectly correct. In a save-only editor that is silent data loss,
    /// reachable by an ordinary gesture - edit, click another card, press Esc.
    ///
    /// So the snapshot is not allowed to exist without its file. Everything the
    /// window asks of a session is answered from <see cref="FilePath"/>, never
    /// from the current focus, and this type is deliberately free of Unity so the
    /// rule is checked outside the editor rather than by hand.
    /// </summary>
    public sealed class BuilderSourceEditSession
    {
        private string _filePath;
        private string _snapshot;

        /// <summary>True while an edit session is open.</summary>
        public bool IsOpen => _filePath != null;

        /// <summary>The file this session belongs to - the ONLY file a cancel or
        /// an apply may touch. Null when no session is open.</summary>
        public string FilePath => _filePath;

        /// <summary>The buffer as it was when the session opened, for cancel to
        /// restore. Null when no session is open.</summary>
        public string Snapshot => _snapshot;

        /// <summary>Opens a session on <paramref name="filePath"/>. A path or a
        /// snapshot of null opens nothing: a session that cannot name its file is
        /// the defect this type exists to make unrepresentable.</summary>
        public void Begin(string filePath, string snapshot)
        {
            if (string.IsNullOrEmpty(filePath) || snapshot == null)
            {
                End();
                return;
            }
            _filePath = filePath;
            _snapshot = snapshot;
        }

        /// <summary>Closes the session and returns the file it belonged to, or
        /// null when none was open.</summary>
        public string End()
        {
            string closed = _filePath;
            _filePath = null;
            _snapshot = null;
            return closed;
        }

        /// <summary>Does this session belong to <paramref name="filePath"/>?
        /// Compared case-insensitively, because the window's focus path and the
        /// tree's module path agree on the file while differing in case.</summary>
        public bool Owns(string filePath) =>
            IsOpen
            && !string.IsNullOrEmpty(filePath)
            && string.Equals(_filePath, filePath, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Tells the session the focus has moved. An edit session belongs to ONE
        /// file, so moving to another ends it, and true is returned so the caller
        /// can drop the pane out of edit mode.
        ///
        /// Ending it loses nothing: typing is applied to the buffer as it is
        /// typed, so the text is already in the file it was typed into, and the
        /// action ledger still holds it for undo. The snapshot only ever backed
        /// "cancel", and cancel does not survive leaving the file.
        /// </summary>
        public bool FocusMovedTo(string filePath)
        {
            if (!IsOpen || Owns(filePath))
                return false;
            End();
            return true;
        }
    }
}
