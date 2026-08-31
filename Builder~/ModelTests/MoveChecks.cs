using System;
using System.Collections.Generic;
using Ruitk.Builder;

/// <summary>
/// Dropping a row INTO a self-closing tag.
///
/// The owner's repro, exactly: drag a `&lt;Router /&gt;` in from the library so it
/// lands as a sibling above two components, then drag those components into it.
/// The move path had no way to open a self-closing tag - it degraded an
/// inside-drop to an after-drop, which is where the rows already were, so the
/// gesture did nothing while reporting success. The insert path had been
/// re-opening `/&gt;` since it was written; only the move path never learned to.
/// </summary>
static class MoveChecks
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Move into a self-closing tag");

        // <Router /> sits ABOVE the rows being moved - the owner's case.
        var lines = new List<string>
        {
            "  return (",                       // 0
            "    <VisualElement>",              // 1
            "      <Router />",                 // 2  <- target
            "      <LeftSide />",               // 3  <- moved
            "      <RightSide />",              // 4
            "    </VisualElement>",             // 5
            "  );",                             // 6
        };

        bool ok = BuilderText.TryMoveIntoSelfClosingTag(
            lines, targetIndex: 2, fromIndex: 3, toIndex: 3, "Router", out var r);
        check(ok, "a row moves into a self-closing tag above it");
        check(ok && r[2] == "      <Router>", "the target is re-opened - '/>' becomes '>'");
        check(ok && r[3] == "        <LeftSide />", "the moved row is indented as a child");
        check(ok && r[4] == "      </Router>", "and a closing tag is written after it");
        check(ok && r[5] == "      <RightSide />", "the sibling below is left where it was");
        check(ok && r.Count == lines.Count + 1, "exactly one line is added - the close tag");

        // Target BELOW the moved row: lifting the block first must not leave the
        // target index pointing at the wrong line.
        var below = new List<string>
        {
            "    <VisualElement>",              // 0
            "      <LeftSide />",               // 1  <- moved
            "      <Router />",                 // 2  <- target
            "    </VisualElement>",             // 3
        };
        bool ok2 = BuilderText.TryMoveIntoSelfClosingTag(
            below, targetIndex: 2, fromIndex: 1, toIndex: 1, "Router", out var r2);
        check(ok2, "a row moves into a self-closing tag BELOW it");
        check(ok2 && r2[1] == "      <Router>",
              "the target index follows the block being lifted out from above it");
        check(ok2 && r2[2] == "        <LeftSide />", "and the row lands inside");
        check(ok2 && r2[3] == "      </Router>", "with its close tag after");

        // A multi-line block keeps its internal shape.
        var block = new List<string>
        {
            "    <Router />",                   // 0  <- target
            "    <Panel>",                      // 1  <- moved, 3 lines
            "      <Label text=\"hi\" />",      // 2
            "    </Panel>",                     // 3
        };
        bool ok3 = BuilderText.TryMoveIntoSelfClosingTag(
            block, targetIndex: 0, fromIndex: 1, toIndex: 3, "Router", out var r3);
        check(ok3, "a multi-line block moves in");
        check(ok3 && r3[1] == "      <Panel>", "the block's first line is re-indented");
        check(ok3 && r3[2] == "        <Label text=\"hi\" />",
              "and its inner lines keep their relative indent");
        check(ok3 && r3[3] == "      </Panel>", "including its own close tag");
        check(ok3 && r3[4] == "    </Router>", "before the target's close tag");

        // Refusals.
        var notSelfClosing = new List<string> { "    <Router>", "    <A />", "    </Router>" };
        check(!BuilderText.TryMoveIntoSelfClosingTag(
                  notSelfClosing, 0, 1, 1, "Router", out _),
              "a tag that is not self-closing is refused - the caller has a path for it");

        check(!BuilderText.TryMoveIntoSelfClosingTag(lines, 2, 2, 2, "Router", out _),
              "a tag cannot be moved into itself");
        check(!BuilderText.TryMoveIntoSelfClosingTag(lines, 2, 1, 4, "Router", out _),
              "nor into a range that contains it");
        check(!BuilderText.TryMoveIntoSelfClosingTag(lines, 99, 3, 3, "Router", out _),
              "an out-of-range target is refused rather than throwing");
        check(!BuilderText.TryMoveIntoSelfClosingTag(lines, 2, 3, 3, "", out _),
              "an empty tag name is refused - the close tag would be malformed");
        check(!BuilderText.TryMoveIntoSelfClosingTag(null, 2, 3, 3, "Router", out _),
              "a null buffer is refused rather than throwing");
    }
}
