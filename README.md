# Reactive UI Toolkit — Unity

Reactive UI Toolkit — Unity brings a React-like component model to Unity UI Toolkit, with function components, hooks, a virtual node tree, and a typed props model that all run entirely in C# on top of UI Toolkit.

The same components also render to classic **Unity UI (uGUI)**: one fiber reconciler, two render backends. A `.uitkx` file opts in with the `@backend ugui` directive and mounts under any Canvas `RectTransform` — see [uGUI Backend](#ugui-backend) below.

👉 Full documentation, guides, and examples: http://reactiveuitoolkit.info/

💬 Join the community on Discord: https://discord.gg/Knedqu4Wyv

---

## Architecture

### Directory Map

| Directory | Description |
|---|---|
| `Runtime/` | Thin MonoBehaviour adapter — `RootRenderer`, `RenderScheduler` |
| `Shared/` | Core reactive library — `V`, `VNode`, Hooks, Fiber reconciler, Elements, Props |
| `Ugui/` | uGUI render backend — `UguiRootRenderer`, `U.*` factories, element adapters, islands |
| `Editor/` | Unity Editor integration — HMR, change watcher, console navigation |
| `Analyzers/` | Published Roslyn analyzer / source generator DLLs |
| `Samples/` | Demo components (`Components/`), shared demo infra (`Shared/`), full apps (`UIs/`, `Showcase/`), uGUI demos (`UguiDemo/`) |
| `SourceGenerator~/` | Source generator source code + tests |
| `ide-extensions~/` | IDE extension projects (VS Code, VS2022, Rider, shared LSP server) |
| `Plans~/` | Design documents and implementation plans |
| `ReactiveUIToolkitDocs~/` | Documentation website (Vite + React) |
| `scripts/` | Build and publish automation |

### Key Architectural Decisions

- **Shared is the core** — `Runtime/` is a thin adapter that hosts the reconciler inside Unity
- **Host-agnostic fiber** — the same reconciler drives UI Toolkit and classic uGUI (`Ugui/`); a mount owns one backend, islands mix them
- **Source generator** transpiles `.uitkx` → C# partial classes at build time (zero runtime overhead)
- **LSP server** (`language-lib` + `lsp-server`) provides IDE features across all editors
- **HMR** compiles `.uitkx` changes in-editor without domain reload (50–200 ms cycle)
- `~` suffix hides IDE / dev folders from Unity's Asset Database

---

## UITKX — JSX-style markup for the toolkit

UITKX lets you write components as `.uitkx` files with XML-like markup instead
of nested `V.Box(...)` C# calls. A Roslyn source generator compiles each
`.uitkx` file into a C# partial class at build time — zero runtime overhead.

### Quick start (under 10 minutes)

**1. Install the VSCode extension** (optional but recommended)

Open the `ide-extensions~/vscode/` folder and run `npm install && npm run build`,
or install the pre-built `.vsix` from the latest release. The extension provides
syntax highlighting, IntelliSense, and auto-format for `.uitkx` files.

**2. Create a `.uitkx` file**

Add `MyComponent.uitkx` to your project (anywhere Unity picks up scripts):

```uitkx
export VirtualNode MyComponent() {
    var (count, setCount) = useState(0);

    return (
        <Box>
            <Label text={$"Count: {count}"}/>
            <Button text="+" onClick={_ => setCount(count + 1)}/>
        </Box>
    );
}
```

A `.uitkx` file IS a module: it holds plain typed declarations, and its exports are
its public surface. The source generator emits the complete class automatically on
the next Unity compile. The namespace is file-keyed — derived from the file's folders
relative to its owning `.asmdef` plus its file stem (the optional `@namespace`
directive overrides it). `export` makes a declaration `public` and importable from
other `.uitkx` files (`import { X } from "./path"`); without `export` it is `internal`
and file-private.

**Migrating a pre-0.9.0 project?** Run the bundled codemod once — it rewrites legacy
`component`/`hook`/`module` wrappers to plain `export` declarations and inserts the
imports each file needs (idempotent; `--check` for a dry run):

```bash
dotnet run --project SourceGenerator~/Tools/UitkxMigrateImports -- Assets --es-modules --check   # dry run
dotnet run --project SourceGenerator~/Tools/UitkxMigrateImports -- Assets --es-modules           # migrate
```

See the [Imports & Exports docs](http://reactiveuitoolkit.info/#/imports) for the full model.

**3. Use the component**

```csharp
var node = V.Func(MyComponent.Render, props);
```

That's it — no reflection, no codegen at runtime.

**4. Add companion files (optional)**

Extract reusable logic into sibling `.uitkx` files — each one is an ordinary module
whose exports you import from the component:

```uitkx
// MyComponent.hooks.uitkx — custom hooks
export (int, Action) useCounter(int initial = 0) {
    var (count, setCount) = useState(initial);
    var increment = useCallback(() => setCount(count + 1), count);
    return (count, increment);
}
```

```uitkx
// MyComponent.style.uitkx — style values and constants
export Style CardStyle = new Style { (FlexDirection, "row") };
```

```uitkx
// MyComponent.uitkx — the component imports what it uses
import { useCounter } from "./MyComponent.hooks"
import { CardStyle } from "./MyComponent.style"
```

---

## Samples

| File | What it demonstrates |
|---|---|
| [`Samples/Components/UitkxCounterFunc/UitkxCounterFunc.uitkx`](Samples/Components/UitkxCounterFunc/UitkxCounterFunc.uitkx) | Minimal counter — `useState` + markup |
| [`Samples/Components/DirectiveSuccessDemo/DirectiveSuccessDemo.uitkx`](Samples/Components/DirectiveSuccessDemo/DirectiveSuccessDemo.uitkx) | `@if/@else`, `@for`, `@foreach`, `@switch`, `@while`, cross-file imports |
| [`Samples/Components/PropTypesDemoFunc/PropTypesDemoFunc.uitkx`](Samples/Components/PropTypesDemoFunc/PropTypesDemoFunc.uitkx) | Typed props via declared component parameters |
| [`Samples/Components/UguiStressTest/UguiStressTest.uitkx`](Samples/Components/UguiStressTest/UguiStressTest.uitkx) | `@backend ugui` — the stress test rendered through uGUI with the same hook flow as the UI Toolkit `StressTest` |

See [`Samples/README.md`](Samples/README.md) for a full breakdown of each sample category.

---

## Typed Style System

`Style` is a typed dictionary class with **set-only properties** that map to every
Unity UI Toolkit inline style. Values are compile-time checked — passing a `float`
where a `Color` is expected is a build error.

```csharp
// In .uitkx files, CssHelpers is auto-imported — no using needed.
// In .cs files, add: using static Ruitk.Props.Typed.CssHelpers;

var cardStyle = new Style {
    Width = Pct(100),
    Height = Px(200),
    BackgroundColor = Rgba(0.1f, 0.1f, 0.15f, 0.9f),
    FlexDirection = FlexColumn,
    JustifyContent = JustifySpaceBetween,
    AlignItems = AlignCenter,
    Padding = 16f,
    BorderRadius = 8f,
};
```

| Category | Type | Properties |
|---|---|---|
| Layout & spacing | `StyleLength` | Width, Height, Margin*, Padding*, FlexBasis, BorderRadius*, FontSize, LetterSpacing |
| Flex & opacity | `StyleFloat` | FlexGrow, FlexShrink, Opacity, BorderWidth* |
| Colors | `Color` | Color, BackgroundColor, BorderColor*, UnityTextOutlineColor (9 total) |
| Enums | Unity enums | FlexDirection, JustifyContent, AlignItems, Position, Display, Visibility, etc. (15 total) |
| Background | Structs | BackgroundRepeat, BackgroundPositionX/Y, BackgroundSize |
| Transforms | `float` / struct | Rotate, Scale, Translate, TransformOrigin |
| Assets | `Texture2D` / `Font` | BackgroundImage, FontFamily |

**`CssHelpers`** (auto-imported in `.uitkx` files) provides shortcuts: `Pct()`, `Px()`,
`StyleAuto`, `StyleNone`, `FlexRow`, `FlexColumn`, `JustifyCenter`, `AlignCenter`, `Hex("#FF0000")`,
`Rgba(255, 0, 0)`, color presets (`ColorWhite`, `ColorBlack`, `ColorRed`, …), compound struct
factories (`BgRepeatNone`, `BgSizeCover`, `Origin()`, `Xlate()`, `EaseInOut`, …),
and all enum values including element props (`SelectNone`, `SortCustom`, `PickIgnore`, etc.).

The old tuple syntax `(StyleKeys.Key, value)` remains available as an escape hatch.

👉 Full guide: [Styling documentation](http://reactiveuitoolkit.info/styling)

---

## uGUI Backend

The same components, hooks, signals, context, router, and HMR also render
classic **Unity UI (uGUI)** — mounted under a `RectTransform` in an existing
Canvas instead of a `UIDocument`. uGUI keeps its own mental model: positioning
is RectTransform anchors/pivots (with an `anchors` preset prop mirroring the
Inspector widget), styling is sprites, colors, and materials, stacking is
LayoutGroups. There is deliberately no `Style`/USS surface on uGUI elements.

A `.uitkx` file opts into the uGUI element vocabulary with a preamble directive:

```uitkx
@backend ugui
export VirtualNode HelloUgui() {
  var (count, setCount) = useState(0);

  return (
    <VerticalLayoutGroup spacing={8f}>
      <Text text={$"Count: {count}"} fontSize={16f} />
      <Button onClick={() => setCount(count + 1)}>
        <Text text="Increment" />
      </Button>
    </VerticalLayoutGroup>
  );
}
```

Mount it with `UguiRootRenderer` (the uGUI sibling of `RootRenderer`) on a
RectTransform under your Canvas — an EventSystem is required for interaction:

```csharp
GetComponent<UguiRootRenderer>().Render(V.Func(HelloUgui.Render));
```

The element vocabulary covers Canvas, Panel, Image, RawImage, Text (TMP),
Button, the three LayoutGroups, Toggle/ToggleGroup, Slider, Scrollbar,
ScrollRect, Dropdown, InputField, plus `<Prefab>` (mounts existing uGUI
prefabs) and islands for mixing backends: `U.UguiHost` embeds a uGUI subtree
in a UI Toolkit tree, `U.UitkHost` embeds a UI Toolkit panel in a uGUI tree.

👉 Full guide: [uGUI Backend documentation](http://reactiveuitoolkit.info/ugui)

---

## Settings

Every setting lives in one window — **Reactive UI Toolkit ▸ Settings** — in
labeled sections. The **Configuration** section is project-scoped and stored in
a plain JSON file, `Assets/Resources/ReactiveUIToolkit/config.json`, holding the
family-canonical knob set: `environment`, `time_slicing`, `time_slice_ms`,
`frame_budget_ms`, `host_node_pool`, `hook_validation`, `strict_diagnostics`,
`strict_mode`, `trace_level`, `diff_tracing`, and the Unity-only
`diagnostics_output_folder`. The section is read-only until you click **Create
settings file** (nothing is written until you do); under `Resources/` the file
ships into every player build by itself, so the same values apply at runtime —
except that release players resolve the `auto` tri-states to off and force
`strict_mode` off. Per-developer knobs (the HMR toggles and keybind
recorders, verbose console navigation) are EditorPrefs-backed sections of the
same window — per machine, not per project. Projects still
carrying an edited legacy `config.json` keep working — it is honoured whenever no
settings file exists.

---

## Development Setup

### Prerequisites

- Unity 6000.2+
- .NET 8+ SDK
- Node.js 18+ (for VS Code extension and docs site)
- Visual Studio 2022 (for VS2022 extension development)

### Building Components

**Source Generator**
```
dotnet build SourceGenerator~/Ruitk.SourceGenerator.csproj
scripts/build-generator.ps1   # builds + copies DLL to Analyzers/
```

**IDE Extensions**
```bash
# VS Code
cd ide-extensions~/vscode && npm run build

# VS2022
ide-extensions~/visual-studio/build-local.ps1

# LSP Server (shared)
dotnet publish ide-extensions~/lsp-server -c Release
```

**Documentation Site**
```bash
cd ReactiveUIToolkitDocs~ && npm run dev
```

### Running Tests

```
dotnet test SourceGenerator~/Tests
```

## License

**Free for almost everyone.** The toolkit ships under the
[Reactive UI Toolkit Community License 1.1](LICENSE.md): use it, modify it, and ship commercial
games with it at no cost if your company (plus parents/subsidiaries) earned under
**US $250,000** in the last 12 months. Development, evaluation, and education are free
at any company size — the threshold only applies when you *ship*.

Above the threshold, shipping a product takes a commercial license — **$2,000 per
title** (one-time, perpetual) or **$2,500 per studio per year**, your pick; see
[LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md). The same terms and prices exist for
each library in the Reactive UI Toolkit family (Godot, Unity, Unreal).

Two asks of everyone: put **"Made with Reactive UI Toolkit"** in your credits alongside your
other middleware, and don't resell the library itself as a competing product (your
game is never a competing product). Every previously released version keeps the
license it shipped with. Contributions require the one-time [CLA](CLA.md).
Weird case (nonprofit, just-over-the-line, contractor)? Email
<yanivkalfa@gmail.com> — we'd rather you ship with the toolkit than not.

