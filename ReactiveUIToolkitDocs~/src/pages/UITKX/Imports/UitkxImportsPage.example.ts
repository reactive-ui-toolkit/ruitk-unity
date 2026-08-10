// Code samples for the Imports & Exports docs page (ES-modules dialect, 0.9.0).

export const EXAMPLE_GRAMMAR = `import { StatusChip } from "./components/StatusChip"
import { useCounter, FormatCount } from "~/Shared/Counter"

export VirtualNode Screen(int Start = 0) {
    var (count, setCount) = useCounter(Start);
    return (
        <Box>
            <StatusChip label={FormatCount(count)} />
        </Box>
    );
}

export (int value, Action bump) useCounter(int start) { /* ... */ }
export string FormatCount(int c) => $"#{c}";
export Style barStyle = new Style { /* ... */ };

VirtualNode LocalHelper() { /* ... */ }   // no export = file-private`

export const EXAMPLE_SPECIFIERS = `import { Card } from "./Card"              // same folder
import { Theme } from "../theme/Theme"     // parent folder
import { Icons } from "~/Shared/Icons"     // '~/' = UI source root (default Assets/)

// Extensionless — '.uitkx' is implied. Engine-native forms are NOT valid
// import specifiers (they never resolve → UITKX2300):
//   import { X } from "Assets/UI/X"       // ✗
//   import { X } from "Packages/p/X"      // ✗`

export const EXAMPLE_FULL_SURFACE = `// The full ES import surface (0.9.0):

import { Card } from "./Card"                 // named
import { Card as Tile } from "./Card"         // rename-on-import (hooks must keep 'use')
import * as Tokens from "../shared/Tokens"    // namespace import: Tokens.Gap in C#,
                                              //   <Tokens.Circle /> in markup
import ScorePanel from "./ScorePanel"         // default import — binds the target's
                                              //   'export default' declaration
import Fallback, { Card, useDeck } from "./Deck"   // combined: default + named (0.9.1)
import Fallback2, * as Deck from "./Deck"          // combined: default + namespace

// Export forms compose the same way:
export int MaxItems = 5;                      // inline export
int Threshold = 3;
export { Threshold };                         // deferred export list
export default ScorePanel;                    // one default per file`

export const EXAMPLE_NAMESPACE_IMPORT = `// Two shapes, two jobs — both live in the preamble:

import { Card } from "./Card"          // FILE import  — braces + 'from "./path"'
                                       //   pulls in a peer .uitkx export (name-checked)
import "@Ruitk.Router"          // NAMESPACE import — quoted "@Namespace"
                                       //   brings a C# namespace into scope

// import "@Ns" is exactly equivalent to @using Ns — the same generated 'using'.
// It also accepts the full using grammar:
import "@static UnityEngine.Mathf"     // = using static UnityEngine.Mathf;
import "@V = UnityEngine.Vector2"      // = using V = UnityEngine.Vector2;

// You rarely need either: System, System.Linq, UnityEngine, Ruitk[.Core],
// and the typed-style helpers are already in scope. Write a namespace import only
// when the editor red-squiggles a C# name that isn't from another .uitkx file.`

export const EXAMPLE_MIXED = `// A file is a SEQUENCE of declarations, any kind, any order:
import { Palette } from "./Palette"

export VirtualNode Header() { return (<Box style={Palette.Bar} />); }
export VirtualNode Footer() { return (<Box style={Palette.Bar} />); }
export void useNav() { /* ... */ }
int gap = 8;                                       // file-private value`

export const EXAMPLE_STRICT = `// Screen references StatusChip but never imports it:
export VirtualNode Screen() {
    return (<StatusChip />);   // UITKX2305: 'StatusChip' is defined in
                               // StatusChip.uitkx but not imported —
                               // add: import { StatusChip } from "./StatusChip"
}`

export const EXAMPLE_CODEMOD = `# Migrate wrapper-keyword files to plain declarations (the wrappers were
# removed in 0.16.0 — un-migrated files raise UITKX2320):
node scripts/migrate-uitkx.mjs Assets --es-modules

# Dry run / idempotence gate — reports which files WOULD change, exits non-zero if any:
node scripts/migrate-uitkx.mjs Assets --es-modules --check

# Companion sets (X.uitkx + X.*.uitkx) migrate atomically. Generic hooks migrate
# too (first-class since 0.16.0); the one shape the plain dialect cannot express —
# type definitions (enums/classes/structs) — is reported: move those to a
# hand-written .cs beside the components.`

export const EXAMPLE_NAMESPACE = `// No @namespace? The default derives from the file's path INCLUDING its stem
// (a file IS a module):  Samples/Components/Board/Board.uitkx  (asmdef at Samples/)
//   → namespace Ruitk.Uitkx.Components.Board.Board
//
// @namespace is an OPTIONAL interop override (for hand-written C# that
// references the generated type by a fixed namespace):
@namespace MyGame.UI
export VirtualNode Board() { /* ... */ }`
