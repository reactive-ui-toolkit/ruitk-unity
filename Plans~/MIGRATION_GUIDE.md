# UITKX Migration Guide

This guide shows how to convert a hand-written C# Func component into a `.uitkx`
file and how to integrate typed props with reactive data sources.

---

## Why migrate?

| Hand-written C# | `.uitkx` |
|---|---|
| Verbose nested `V.Box(...)` calls | Readable XML-like markup |
| No IDE markup highlighting | Full syntax colouring + semantic tokens |
| No element-level completions | IntelliSense for element names and attributes |
| Diagnostics only at Roslyn compile time | Structural diagnostics before the C# build |
| Breakpoints land in generated glue code | `#line` directives map breakpoints to `.uitkx` |

---

## Quick-start: 5-minute migration

### Before — hand-written C# Func component

```csharp
// SimpleCounterFunc.cs
using System.Collections.Generic;
using Ruitk.Core;
using Ruitk.Props.Typed;

namespace Ruitk.Samples.FunctionalComponents
{
    public static class SimpleCounterFunc
    {
        public static VirtualNode Render(
            Dictionary<string, object> props,
            IReadOnlyList<VirtualNode> children)
        {
            var (count, setCount) = Hooks.UseState(0);
            return V.VisualElement(
                null,
                null,
                V.Text($"Count: {count}"),
                V.Button(new ButtonProps { Text = "+", OnClick = () => setCount(count + 1) })
            );
        }
    }
}
```

### After — `.uitkx` file

```uitkx
export VirtualNode SimpleCounter() {
    var (count, setCount) = useState(0);

    return (
        <VisualElement>
            <Text text={$"Count: {count}"}/>
            <Button text="+" onClick={_ => setCount(count + 1)}/>
        </VisualElement>
    );
}
```

That is the whole migration — no companion `.cs` file is needed. The source generator
emits the complete class (namespace, `partial class`, `Render()`) automatically.
You call it the same way:

```csharp
V.Func(SimpleCounter.Render, props)
```

The namespace is file-keyed (folders relative to the owning `.asmdef` plus the file
stem), so the consumer's `using` changes — or add an explicit `@namespace` directive
to pin the old one. A hand-written `partial .cs` in the matching namespace can still
extend the generated class.

---

## Step-by-step migration

### Step 1 — Identify the component

Find the `public static VirtualNode Render(...)` method you want to migrate. Confirm:
- It lives in a known namespace.
- The class name is the component name you want to keep (or pick a new one).

### Step 2 — Create the `.uitkx` file

Create `<ComponentName>.uitkx` next to the existing `.cs` file.

Use a plain typed declaration:

```uitkx
export VirtualNode <ClassName>() {
    // setup C#
    return (
        <VisualElement />
    );
}
```

Namespace: since 0.9.0 the default is **file-keyed** — derived from the file's folders
relative to its owning `.asmdef` plus its file stem (the generator never reads a
companion `.cs` for it). If you have a hand-written companion `partial .cs`, declare an
explicit `@namespace` matching it so the two partials merge. The `component` wrapper
keyword and the `@component` directive-header form were removed in 0.16.0 (`UITKX2320`
error).

Cross-file references are explicit: prefix declarations with `export` and add
`import { X } from "./path"` lines (or run the bundled codemod once over the project —
`node scripts/migrate-uitkx.mjs <dir> --es-modules` does all of this mechanically; see
the wrapper-removal section at the end of this guide).

#### Tidying `@using` → `import "@Ns"` (optional, `--tidy`)

Since 0.8.0 a C# namespace can be brought into scope with either `@using Ns` or the
unified `import "@Ns"` spelling — they are exactly equivalent (same generated `using`).
`import "@Ns"` reads consistently with file imports and is the recommended form for new
code; `@using` keeps working forever. The `--tidy` flag canonicalizes an existing project:

```bash
# Dry run — shows which files would change:
dotnet run --project SourceGenerator~/Tools/UitkxMigrateImports -- Assets --check --tidy

# Apply: rewrites @using X → import "@X" AND deletes redundant baseline usings
# (@using UnityEngine / @using System / … are auto-injected into every generated file):
dotnet run --project SourceGenerator~/Tools/UitkxMigrateImports -- Assets --tidy
```

`--tidy` is idempotent and composes with the import migration. Prefer writing a namespace
import only when the editor red-squiggles a C# name (`UITKX2316`); most files need none.

### Step 3 — Move hook calls to function setup section

Everything that was inside `Render()` before the `return` statement goes into
the setup section before `return (...)` (built-in hooks use their lowercase
`useState`/`useEffect` names in `.uitkx`):

```uitkx
export VirtualNode <ClassName>() {
    var (count, setCount) = useState(0);
    var (mode,  setMode)  = useState("normal");

    return (
        <VisualElement />
    );
}
```

All hook calls, local variable declarations, and helper lambdas belong here.

### Step 4 — Translate the return value to markup

Replace every `V.XXX(new XXXProps { ... }, ...)` call with the equivalent tag.

| C# | UITKX |
|---|---|
| `V.Box(new BoxProps { }, ...)` | `<Box> ... </Box>` |
| `V.Label(new LabelProps { Text = "Hi" })` | `<Label text="Hi"/>` |
| `V.Button(new ButtonProps { Text = "+", OnClick = cb })` | `<Button text="+" onClick={cb}/>` |
| `V.Text("hello")` | `<Text text="hello"/>` |
| `V.Func(MyComp.Render, props)` | `<MyComp data={props}/>` (see typed props section) |

String literals can be unquoted plain values. C# expressions are wrapped in `{ }`:

```uitkx
<Label text="static text"/>
<Label text={$"dynamic: {count}"}/>
```

### Step 5 — Delete the old `.cs` file

Delete (or rename to `Legacy/`) the old static class — the generator emits the
complete class from the `.uitkx` source on the next compile; no companion stub is
required. If other hand-written C# referenced the old class, update its `using` to the
new file-keyed namespace (or pin the old one with `@namespace`).

### Step 6 — Verify

1. Save both files.
2. Unity recompiles — no errors should appear.
3. The component renders identically to the original.

---

## Directive reference

### `@if` / `@else if` / `@else`

```uitkx
@if (health <= 0) {
    return (<Label text="Game Over"/>);
} @else if (health < 20) {
    return (<Label text="Low health!"/>);
} @else {
    return (<Label text={$"HP: {health}"}/>);
}
```

All branches are always wrapped in a `V.Fragment` by the emitter, keeping the
type at each child position stable across re-renders.

### `@switch` / `@case` / `@default`

```uitkx
@switch (mode) {
    @case "fireball":
        return (<Label text="Fireball active"/>);
    @case "shield":
        return (<Label text="Shield active"/>);
    @default:
        return (<Label text="No ability"/>);
}
```

### `@foreach`

```uitkx
@foreach (var item in inventory) {
    return (
        <Box key={item.Id}>
            <Label text={item.Name}/>
        </Box>
    );
}
```

> **Always supply a `key` attribute** on the direct children of `@foreach`.
> The reconciler uses it to match items across list changes. Omitting it
> triggers diagnostic `UITKX0106`.

### `@for`

```uitkx
@for (int i = 0; i < count; i++) {
    if (i % 2 == 0) {
        return (<Label key={i} text={$"Even: {i}"}/>);
    }
    return (<Label key={i} text={$"Odd: {i}"}/>);
}
```

---

## Typed props + `PropsHelper.Bind`

For components that receive external data (not just internal hooks state), declare
the data as typed component parameters. The source generator derives a matching
`<Component>Props` class (one PascalCase property per parameter, implementing
`IProps`) — you do not hand-write it.

### UITKX component

```uitkx
export VirtualNode PlayerHUD(
    int health = 100,
    int maxHealth = 100,
    string activeAbility = "",
    IReadOnlyList<InventoryItem> inventory = null) {

    return (
        <Box>
            <Label text={$"HP: {health} / {maxHealth}"}/>
        </Box>
    );
}
```

From other `.uitkx` markup, pass the parameters as attributes:
`<PlayerHUD health={hp} maxHealth={100}/>`.

### Presenter / MonoBehaviour

From C#, construct the generated `PlayerHUDProps` and pass it to `V.Func`:

```csharp
using Ruitk.Props;

// Create props once (PlayerHUDProps is source-generated from the parameters)
var props = new PlayerHUDProps { Health = 100, MaxHealth = 100 };

// Bind a Signal<int> — fires onChange whenever the signal value changes
_disposables.Add(PropsHelper.Bind(
    selector : (PlayerHUDProps p) => p.Health,
    signal   : _healthSignal,
    onChange : (propName, value) => { props.Health = value; RequestRender(); }));

// Bind an INotifyPropertyChanged source
_disposables.Add(PropsHelper.Bind(
    selector       : (PlayerHUDProps p) => p.Health,
    source         : _viewModel,
    sourceProperty : (PlayerViewModel vm) => vm.Health,
    onChange       : (name, value) => { props.Health = (int)value; RequestRender(); }));

// Render
var node = V.Func(PlayerHUD.Render, props);
```

---

## Common pitfalls

### 1. Missing `partial` keyword

The companion `.cs` file **must** use `partial class`, not `static class`. The
generator emits into the same partial class.

```csharp
// WRONG — static class cannot be partial
public static class MyComp { }

// CORRECT
public partial class MyComp { }
```

### 2. Namespace mismatch

The `@namespace` directive must exactly match the namespace in the companion `.cs`
file. A mismatch produces `CS0101: The namespace already contains a definition for 'MyComp'`.

### 3. Forgetting `key` on `@foreach` children

```uitkx
@foreach (var item in list) {
    return (<Box> ... </Box>);              ← UITKX0106 warning — missing key
}

@foreach (var item in list) {
    return (<Box key={item.Id}> ... </Box>);  ← Correct
}
```

### 4. Expressions that need braces

Attribute values that are C# expressions must be wrapped in `{ }`:

```uitkx
<Label text="literal"/>           ← plain string, no braces needed
<Label text={$"count: {count}"}/>  ← C# expression, braces required
<Button onClick={() => setX(1)}/>  ← lambdas also need braces
```

### 5. Setup-code scope

Variables declared in the setup section (before `return (...)`) are local to the
generated `Render()` method. Do not expect them to persist across renders. For state
shared across renders, use `useState` or `useRef`.

### 6. Parameters, not raw props

Components receive external data through their declared, typed parameters — there is
no raw props dictionary to unpack. If a C# consumer needs to pass data, it constructs
the source-generated `<Component>Props` class (see the typed-props section above).

---

## Full example: counter migration

See [UitkxCounterFunc.uitkx](../Samples/Components/UitkxCounterFunc/UitkxCounterFunc.uitkx)
and [SimpleCounterFunc.uitkx](../Samples/Components/SimpleCounterFunc/SimpleCounterFunc.uitkx).

For a complete directive tour see
[DirectiveSuccessDemo.uitkx](../Samples/Components/DirectiveSuccessDemo/DirectiveSuccessDemo.uitkx);
for typed props via declared parameters see
[PropTypesDemoFunc.uitkx](../Samples/Components/PropTypesDemoFunc/PropTypesDemoFunc.uitkx).

---

## Wrapper keywords — deprecated 0.9.0, REMOVED 0.16.0

### Why

A `.uitkx` file IS a module (like an ES module): its exports are its entire public
surface, its namespace derives from its file path, and everything cross-file goes
through an import. The `component` / `hook` / `module` wrapper keywords were deprecated
in 0.9.0 and **removed in 0.16.0** in favor of plain typed declarations classified from
the signature alone. A file that still uses them fails to compile with the `UITKX2320`
error; the table below is that error's remediation doc.

### Before / after

```
// BEFORE (legacy wrappers)                 // AFTER (plain declarations)
export component ScoreRow(string label) {   export VirtualNode ScoreRow(string label) {
  return ( <Label text={label} /> );          return ( <Label text={label} /> );
}                                           }

export hook useCountdown(int start)         export (int value, Action reset) useCountdown(int start) {
    -> (int value, Action reset) {            ...
  ...                                       }
}

export module Tokens {                      export int Gap = 8;
  public static readonly int Gap = 8;       export string Fmt(int s) { return $"{s}"; }
  public static string Fmt(int s) {...}
}
```

### The five mechanical rules

1. **Wrapper → plain**: `component N` → `VirtualNode N(...)` (parameterless gains `()`);
   `hook useN(p) -> (r)` → `(r) useN(p)` (single-type returns lose the parens); modules
   dissolve — each member becomes a top-level declaration.
2. **Module importers → `* as`**: `import { Tokens } from "./Tokens"` becomes
   `import * as Tokens from "./Tokens"`; every `Tokens.Gap` call site is unchanged.
3. **Companions become modules**: `X.style.uitkx` no longer merges into component `X`.
   The component imports what it uses: `import { container } from "./X.style"`.
4. **File-keyed namespaces move C# `using` lines**: hand-written consumers append the
   declaring FILE's stem — `using My.Ns.Folder;` → `using My.Ns.Folder.FileStem;`.
5. **Hot-reload state resets once** per migrated file (family keys change with the
   namespace); steady-state hot reload is unchanged.

### Codemod

```bash
node scripts/migrate-uitkx.mjs <dir> --es-modules
node scripts/migrate-uitkx.mjs <dir> --es-modules --check    # idempotence gate
node scripts/migrate-uitkx.mjs <dir> --es-modules --report r.txt   # rewrite + namespace-move ledger
```

Companion sets (`X.uitkx` + `X.*.uitkx`) migrate atomically — all or none. Generic
hooks migrate (first-class plain declarations since 0.16.0). The one shape the plain
dialect cannot express — type definitions (enums/classes/structs, plus properties,
events, and constructors on them) — is reported with the fix: move it to a hand-written
`.cs` beside the components (ambient C# is never policed). Where-constrained generics
are also rejected with a message (move the constrained helper to ambient C#).

Recommended upgrade order: **codemod first, then bump the package** — the codemod runs
from the repository against your project regardless of the installed package version,
so migrate on 0.15.x, verify, then upgrade to 0.16.0 with a clean console. (Upgrading
first also works; every legacy file reports `UITKX2320` until the codemod runs.)

### Escape hatches (unchanged)

`@using` keeps parsing indefinitely (the unified `import "@Ns"` spelling is the
recommended form). An explicit `@namespace` stamp pins a file's namespace — a rare
interop escape hatch, not for normal components. Components still emit `partial class`,
so hand-written `.cs` can extend them.

### Troubleshooting

| Symptom | Meaning → fix |
|---|---|
| `UITKX2320` on `component`/`hook`/`module` | The wrapper grammar was removed in 0.16.0 — run the codemod above |
| `UITKX2320` "cannot be mixed with plain declarations" | One wrapper declaration sits inside an otherwise-migrated file (e.g. a pasted legacy snippet). Mixed files are NOT auto-migrated — the codemod freezes them into its report; rewrite that one declaration by hand |
| `CS0103 'container' does not exist` post-migration | A former companion member is no longer merged — add `import { container } from "./X.style"` |
| `CS0246/CS0234` in a consumer `.cs` | The namespace moved (file-keyed) — append the declaring file's stem to the `using` |
| `CS0229 ambiguity with __Exports.X` | A value export shares a name with a type in scope (e.g. `Button`) — rename the export |
| `UITKX2110` on a hook rename | `import { useX as y }` drops the `use` prefix — hook bindings must stay `use`-prefixed |

(`UITKX2107`/`2108`/`2109` retired with the grammar in 0.16.0. A legacy file fails at
parse with 2320 before 2107/2109 could fire, and the former 2108 mixed-file cases now
report the 2320 mixed-file variant above.)
