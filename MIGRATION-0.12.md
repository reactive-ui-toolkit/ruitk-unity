# Migrating to 0.12.0 — the "Reactive UI Toolkit" rename

0.12.0 is the family-rebrand release: the library is now **Reactive UI Toolkit — Unity**,
part of the Reactive UI Toolkit family (Godot, Unity, Unreal), hosted at
`https://github.com/reactive-ui-toolkit/ruitk-unity`. It is a **BREAKING** release —
nothing changed functionally, but the code identity did:

| Deliverable | 0.11.x | 0.12 wave |
|---|---|---|
| Unity package (UPM) | 0.11.0 | **0.12.0** |
| VS Code extension | 1.7.0 | **1.8.0** |
| VS2022 extension | 1.7.0 | **1.8.0** |
| Rider plugin | 1.4.0 | **1.5.0** |

What stays: the UPM package id `com.reactiveuitoolkit`, the `.uitkx` language and the
`UITKX` tooling brand, the extension marketplace identities, the docs domain, and the
`RUITK` abbreviation (window titles / pref keys) — it abbreviates the new name too.

## What renamed

1. **Namespace / assembly root: `ReactiveUITK` → `Ruitk`** — every `namespace`, `using`,
   and `global::` reference, all 10 asmdefs (`ReactiveUITK.*` → `Ruitk.*`; the file
   `ReactiveUITK.Examples.asmdef` is now `Ruitk.Samples.asmdef`, finally matching its
   assembly name), and the analyzer DLLs (`Analyzers/Ruitk.Language.dll`,
   `Analyzers/Ruitk.SourceGenerator.dll` — same GUIDs, references survive).
2. **Composites:** `ReactiveUITKConfig` → `RuitkConfig`.
3. **Hidden runtime object names:** the media subsystem's internal hosts renamed —
   `__ReactiveUITK_MediaHost` / `__ReactiveUITK_VideoPeer` / `__ReactiveUITK_AudioPeer` /
   `__ReactiveUITK_Sfx` / `__ReactiveUITK_RT_<w>x<h>` are now `__Ruitk_*`. Only relevant
   if you `GameObject.Find` them (you shouldn't — they're internals).
4. **Define:** `REACTIVEUITK_HAS_TEST_FRAMEWORK` → `RUITK_HAS_TEST_FRAMEWORK`.
5. **Install folder casing:** `Assets/ReactiveUIToolKit` → `Assets/ReactiveUIToolkit`
   (lowercase k, matching the package id).
6. **Editor menu root:** `ReactiveUITK/…` → `Reactive UI Toolkit/…`.
7. **Default generated namespace for YOUR `.uitkx` files:** `ReactiveUITK.Uitkx` →
   `Ruitk.Uitkx`, and for function-style components `ReactiveUITK.FunctionStyle` →
   `Ruitk.FunctionStyle`. This applies to every component that does **not** set an
   explicit `@namespace` / `namespacePrefix` — your own components change namespace.
   Compile-time references are fixed by the codemod, but **reflection and serialized
   type names are not**: anything doing
   `Type.GetType("ReactiveUITK.Uitkx.UI.HelloWorld, Assembly-CSharp")`, or a
   `SerializeReference`/addressable/asset-bundle record holding an assembly-qualified
   name, breaks at **runtime**, not at compile time. Grep your project for
   `"ReactiveUITK.Uitkx` and `"ReactiveUITK.FunctionStyle` before shipping. Pin the old
   value with an explicit `@namespace` if you would rather not move.

## Before you start

- **Back up, or commit, first.** The codemod rewrites files in place. It makes no
  backups.
- **Back up your `config.json`.** The user-editable
  `Assets/ReactiveUIToolkit/config.json` (`env`, `traceLevel`, `diffTracing`,
  `exceptionControlFlow`) lives **inside the package folder**, so step 1 below deletes
  it. Copy it somewhere safe and merge your values back after the upgrade, or it
  silently resets to defaults.
- **The codemod needs the .NET 8 SDK.** Unity does not ship one — `dotnet` will not be
  on your PATH just because Unity is installed. Get it from
  <https://dotnet.microsoft.com/download/dotnet/8.0> (`dotnet --version` should print
  8.x or newer).

## Upgrade steps

1. **Delete the old package folder first**, then import 0.12.0:
   remove `Assets/ReactiveUIToolKit` (and its `.meta`) before adding the new
   `Assets/ReactiveUIToolkit`. (Save your `config.json` first — see above.)
   **Linux note:** Windows/macOS filesystems merge the two casings, so an in-place
   upgrade collapses into one folder. On Linux (case-sensitive) you end up with **BOTH**
   `ReactiveUIToolKit` and `ReactiveUIToolkit` side by side — delete the old capital-K
   folder or every type exists twice.
   **The casing matters on Linux even if you never see two folders.** Every path
   literal in the package now says `ReactiveUIToolkit` (lowercase k). If a capital-K
   folder survives on a case-sensitive filesystem, `config.json` is not found and the
   library silently falls back to built-in defaults — typically on a Linux build agent,
   days later, rather than on your machine. Fix the folder casing, and run the codemod
   (step 3) so your own path literals move too.

   **UPM (Package Manager) install instead?** You have no `Assets/ReactiveUIToolKit`
   folder — skip to step 2.

2. **UPM users: update the git URL in `Packages/manifest.json`.** The repository moved,
   so the old URL will not deliver 0.12.0 (and once it stops resolving, the project
   will not open):

   ```jsonc
   {
     "dependencies": {
       // was: "com.reactiveuitoolkit": "https://github.com/<old-org>/<old-repo>.git#dist"
       "com.reactiveuitoolkit": "https://github.com/reactive-ui-toolkit/ruitk-unity.git#dist"
     }
   }
   ```

   The package id `com.reactiveuitoolkit` is unchanged. Pin a release with
   `...ruitk-unity.git#v0.12.0` if you prefer tags to the rolling `#dist` branch.
   Then let Package Manager re-resolve (Window ▸ Package Manager ▸ refresh), or delete
   `Library/PackageCache/com.reactiveuitoolkit@*` to force it.

3. **Run the codemod** over your own code (it never edits inside the package folder).
   The tool lives in the repository, not in the shipped package — Asset Store exports
   and UPM installs do not contain `SourceGenerator~` (Unity never imports `~` folders).
   So clone the repo and run it **from there, against your project**:

   ```bash
   git clone https://github.com/reactive-ui-toolkit/ruitk-unity.git
   cd ruitk-unity

   # Point it at YOUR project's Assets folder (absolute path is easiest):
   dotnet run --project SourceGenerator~/Tools/RuitkMigrateBrand -- /path/to/YourProject/Assets

   # then prove idempotence / CI-gate it:
   dotnet run --project SourceGenerator~/Tools/RuitkMigrateBrand -- /path/to/YourProject/Assets --check
   ```

   On Windows the path looks like `C:\Users\you\YourProject\Assets`. You can also <!-- path-gate-allow: generic placeholder ("you"/"YourProject"), teaching the reader the shape of a Windows path -->
   download the repo as a ZIP from the GitHub "Code" button instead of cloning.
   `--help` prints the full rule list.

   It rewrites `.cs`, `.uitkx`, `.asmdef`, `.asmref` and `.rsp` files with per-file
   counts; a second run reports 0. It preserves each file's byte-order mark, skips
   (with a warning) any file that is not valid UTF-8 rather than corrupting it, and if
   a file cannot be written — Perforce and Plastic keep unopened files read-only, so
   **check your sources out first** — it reports the failure, lists every file it *did*
   write, and exits non-zero instead of dying half-way with a stack trace.

4. **Delete `Assets/ReactiveUITK/`** (and its `.meta`) if it exists. This is **not** the
   package folder — it is the small folder the editor integration generates, and 0.12.0
   moved it to `Assets/Ruitk/`. Leaving it behind causes two real failures:

   - `Assets/ReactiveUITK/Resources/__uitkx_registry.asset` — the asset registry is
     loaded **by name** (`Resources.Load("__uitkx_registry")`), so a leftover copy
     competes with the new one at `Assets/Ruitk/Resources/`. When the stale one wins,
     every `Asset<T>()` / `Ast<T>()` lookup and every `uss=` stylesheet added or changed
     after the upgrade returns null — in the editor **and** in player builds. Nothing
     self-heals it. (The editor logs a warning if it spots this.)
   - `Assets/ReactiveUITK/UITKX_GeneratorTrigger.g.cs` — an obsolete recompile trigger.

   The codemod deliberately skips this folder, so the trigger keeps its old namespace
   and does **not** collide with the new one. Deleting the folder is the clean end state.

5. **Check the manual leftovers the codemod cannot reach** (see "Manual steps" below).

6. Let Unity recompile. Done.

## Manual steps (the codemod cannot do these)

- **Project Settings scripting defines.** If you added
  `REACTIVEUITK_HAS_TEST_FRAMEWORK` under *Project Settings ▸ Player ▸ Other Settings ▸
  Scripting Define Symbols* (per build target!), rename it to
  `RUITK_HAS_TEST_FRAMEWORK` by hand. The codemod rewrites the define in `.cs`, `.asmdef`
  and `.rsp` files, but Unity stores Player defines in `ProjectSettings/ProjectSettings.asset`,
  which is not scanned. A stale define fails **silently** — the guarded block simply
  stops compiling in, with no error.
- **Assembly Definition References set by object, not by name.** `.asmdef`/`.asmref`
  files that reference assemblies by GUID need no change (GUIDs are unchanged); ones
  that reference by name are rewritten for you.
- **Your own `uitkx.config.json`**, if it names `ReactiveUIToolKit` paths — `.json` is
  deliberately not scanned (a user's `.vscode/settings.json` legitimately contains the
  frozen `ReactiveUITK.uitkx` extension id, which must not be rewritten).
- **Reflection / serialized type-name strings** — see "What renamed" item 7.

## Hand-migration rules (what the codemod does, verbatim)

Apply in this order:

- **F — frozen identities: DO NOT TOUCH.** The extension marketplace identities are
  permanent and were deliberately left on the old brand token. If any of these appear in
  your files, leave them exactly as they are — rewriting them points you at an extension
  that does not exist:
  `ReactiveUITK.uitkx`, `ReactiveUITK.uitkx-visualstudio`, `UitkxVsix.ReactiveUITK`,
  `marketplace.visualstudio.com/items?itemName=ReactiveUITK…`,
  `marketplace.visualstudio.com/manage/publishers/ReactiveUITK`, `vsce login ReactiveUITK`.
  (The codemod masks these before any other rule runs.)
- **C1 — composites (enumerated):**
  `ReactiveUITKConfig` → `RuitkConfig`,
  `__ReactiveUITK_MediaHost` → `__Ruitk_MediaHost`,
  `__ReactiveUITK_VideoPeer` → `__Ruitk_VideoPeer`,
  `__ReactiveUITK_AudioPeer` → `__Ruitk_AudioPeer`,
  `__ReactiveUITK_Sfx` → `__Ruitk_Sfx`,
  `__ReactiveUITK_RT_` → `__Ruitk_RT_`.
- **C1b — editor menu root (inside string literals):** `"ReactiveUITK/` →
  `"Reactive UI Toolkit/`. This must run **before** C2, because `/` is not a word
  character: the bare-token rule would otherwise turn `[MenuItem("ReactiveUITK/My Tool")]`
  into `"Ruitk/My Tool"`, which is a *third* orphan menu that looks plausible and greps
  clean. Applies to your own `[MenuItem(...)]` attributes and to any
  `EditorApplication.ExecuteMenuItem("ReactiveUITK/…")` call.
- **C2 — bare token (regex, do not widen):**
  `(?<![A-Za-z0-9_.])ReactiveUITK(?![A-Za-z_])` → `Ruitk`.
  Covers `using`/`namespace`/`global::` lines and asmdef `references` entries like
  `ReactiveUITK.Runtime` → `Ruitk.Runtime`. **Both** boundaries are required: without the
  left one, text merely *ending* in the token (`UitkxVsix.ReactiveUITK`) is corrupted.
- **Define:** `REACTIVEUITK_HAS_TEST_FRAMEWORK` → `RUITK_HAS_TEST_FRAMEWORK`
  (also in `.rsp` files: `-define:REACTIVEUITK_HAS_TEST_FRAMEWORK`).
- **D — install-path segment:** `ReactiveUIToolKit` → `ReactiveUIToolkit` (capital K to
  lowercase k) wherever it is a whole path segment, with **both** word boundaries so a
  folder of your own like `ReactiveUIToolKitExtras` is left alone. This covers every
  spelling, not just the `Assets/`-prefixed one:
  `Assets/ReactiveUIToolKit`, `Assets\ReactiveUIToolKit`, `Assets\\ReactiveUIToolKit`
  (the escaped C# literal), `Packages/ReactiveUIToolKit`, and the **bare** segment form —
  `Path.Combine(Application.dataPath, "ReactiveUIToolKit", "config.json")`, which is the
  package's own idiom and the one most likely to be copied into user code.

## Cosmetic notes

- The editor-prefs key `ReactiveUITK.UitkxNavVerbose` is now `Ruitk.UitkxNavVerbose`;
  your saved value resets once. `RUITK_*` and `UITKX_HMR_*` keys are unchanged.
