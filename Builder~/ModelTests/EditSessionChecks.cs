using System;
using Ruitk.Builder;

/// <summary>
/// The source-pane edit session, checked outside Unity.
///
/// This exists because of a real corruption: edit NewComponent, click the
/// MiddleSide card, press Esc, and MiddleSide's buffer became NewComponent -
/// imports and all - while the file on disk stayed correct. The snapshot was a
/// bare string and cancel restored it into "whatever is focused now". In a
/// save-only editor that is silent data loss from three ordinary clicks.
///
/// The fix was to make a snapshot without its file unrepresentable. These checks
/// pin that: a session always knows its file, cancel and apply always name it,
/// and moving the focus ends the session rather than carrying it.
/// </summary>
static class EditSessionChecks
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Source-pane edit session");

        const string parent = @"C:\proj\Assets\UI\NewComponent\NewComponent.uitkx";
        const string child = @"C:\proj\Assets\UI\NewComponent\components\MiddleSide\MiddleSide.uitkx";

        var s = new BuilderSourceEditSession();
        check(!s.IsOpen, "a fresh session is closed");
        check(s.FilePath == null && s.Snapshot == null, "and names no file and no text");

        s.Begin(parent, "export VirtualNode NewComponent() { }");
        check(s.IsOpen, "edit opens a session");
        check(s.FilePath == parent, "the session knows the file it was opened on");
        check(s.Snapshot == "export VirtualNode NewComponent() { }", "and holds that file's text");
        check(s.Owns(parent), "it owns the file it opened on");
        check(!s.Owns(child), "and owns no other file");

        // THE corruption, as a check: focus moves to another module while an edit
        // is open. The session must not follow it.
        bool ended = s.FocusMovedTo(child);
        check(ended, "moving the focus to another file ENDS the session");
        check(!s.IsOpen, "so nothing is left holding the previous file's text");
        check(s.Snapshot == null,
              "and there is no snapshot for a later cancel to write into the wrong module");

        // Re-open and confirm the same focus does not end it.
        s.Begin(parent, "text-A");
        check(!s.FocusMovedTo(parent), "re-focusing the SAME file leaves the session open");
        check(s.IsOpen, "the session survives a focus event for its own file");

        // Case: the window's focus path and the tree's module path agree on the
        // file but not on case. That must not read as a different file.
        check(!s.FocusMovedTo(parent.ToUpperInvariant()),
              "a case-different path is the same file, not a move away");
        check(s.IsOpen, "so the session is not ended by a case difference");

        // End returns the file, which is what cancel restores into.
        string closed = s.End();
        check(closed == parent, "End names the file the session belonged to");
        check(!s.IsOpen, "and closes it");
        check(s.End() == null, "ending an already-closed session names nothing");

        // A session that cannot name its file must not exist at all.
        s.Begin(null, "orphan text");
        check(!s.IsOpen, "a session with no file does not open");
        s.Begin(parent, null);
        check(!s.IsOpen, "a session with no snapshot does not open");

        // Owns/FocusMovedTo on a closed session are inert rather than throwing -
        // the window asks both on every focus change, open or not.
        check(!s.Owns(parent), "a closed session owns nothing");
        check(!s.FocusMovedTo(child), "and reports no move to act on");
        check(!s.Owns(null), "a null path owns nothing, rather than throwing");
    }
}
