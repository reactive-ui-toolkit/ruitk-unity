import type { FC } from 'react'
import { Box, Link as MuiLink, List, ListItem, ListItemText, Typography } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import Styles from '../../Concepts/ConceptsPage.style'

export const UitkxConceptsPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Concepts & Environment
    </Typography>
    <Typography variant="body1" paragraph>
      ReactiveUIToolkit brings a React-like component model to Unity UI Toolkit. You write
      components, use hooks to manage state, and the reconciler diffs and updates the{' '}
      <code>VisualElement</code> hierarchy for you.
    </Typography>
    <Typography variant="body1" paragraph>
      Where Unity or UI Toolkit impose different constraints (layout system, event model, or platform
      concerns), the library deliberately diverges from React to provide a more idiomatic Unity
      experience. Routing, signals, and safe-area helpers are examples of features that don't exist
      in core React but are important here.
    </Typography>
    <Typography variant="body1" paragraph>
      The package ships with a demo set under <code>Assets/ReactiveUIToolkit/Samples</code> (editor
      windows and runtime scenes). Import them into your project to see real-world usage of
      components, hooks, routing, signals, and more.
    </Typography>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Core authoring rules
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="Intrinsic UITKX/native tag names are reserved; custom components should use distinct names." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Function-style components are the default form: setup code first, then a single returned markup tree." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="State setters are called directly like functions, for example setCount(count + 1)." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                Cross-file references are explicit: a file exposes declarations with{' '}
                <code>export</code> and pulls in what it needs with{' '}
                <code>import {'{ X }'} from "./path"</code>. Exported declarations emit{' '}
                <code>public</code> classes; non-exported ones are <code>internal</code>. See{' '}
                <MuiLink component={RouterLink} to="/imports">Imports &amp; Exports</MuiLink>.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                Declarations are plain and typed, classified from the signature alone: a{' '}
                <code>VirtualNode</code> return type is a component, a <code>use</code>-prefixed
                name is a hook, an initializer is a value, anything else is a util. A file may
                declare any mix; keeping one component per file with{' '}
                <code>.hooks</code>/<code>.style</code>/<code>.utils</code> companions is the
                recommended convention, not a compiler rule.
              </>
            }
          />
        </ListItem>
      </List>
    </Box>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Common props (BaseProps)
      </Typography>
      <Typography variant="body1" paragraph>
        Every element inherits these properties from <code>BaseProps</code>. They
        are available on <code>{'<Button>'}</code>, <code>{'<Label>'}</code>,{' '}
        <code>{'<VisualElement>'}</code>, and all other elements:
      </Typography>
      <Typography component="ul" variant="body2">
        <li><code>name</code>, <code>className</code>, <code>style</code> — identity and styling</li>
        <li><code>ref</code>, <code>contentContainer</code> — element references</li>
        <li><code>visible</code>, <code>enabled</code> — visibility and interactivity</li>
        <li><code>pickingMode</code>, <code>focusable</code>, <code>tabIndex</code>, <code>delegatesFocus</code> — input focus</li>
        <li><code>tooltip</code>, <code>viewDataKey</code>, <code>languageDirection</code> — miscellaneous</li>
        <li><code>extraProps</code> — escape hatch for additional properties</li>
        <li>Event handlers: <code>onClick</code>, <code>onPointerDown</code>, <code>onPointerUp</code>, <code>onPointerMove</code>, <code>onPointerEnter</code>, <code>onPointerLeave</code>, <code>onKeyDown</code>, <code>onKeyUp</code>, <code>onFocusIn</code>, <code>onFocusOut</code>, <code>onWheel</code>, <code>onGeometryChanged</code>, <code>onAttachToPanel</code>, <code>onDetachFromPanel</code>, and more</li>
      </Typography>
    </Box>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Settings
      </Typography>
      <Typography variant="body2" paragraph>
        Settings live in one window: <strong>Reactive UI Toolkit ▸ Settings</strong>,
        divided into labeled sections. The <strong>Configuration</strong> section is
        project-scoped and stored in a plain JSON file,{' '}
        <code>Assets/Resources/ReactiveUIToolkit/config.json</code>. Until the file exists
        the section is read-only and shows the values in effect; click{' '}
        <strong>Create settings file</strong> to write it — nothing is ever written to your
        project until you click. Because it lives under <code>Resources/</code>, the file
        ships into every player build by itself — no build hooks, no Preloaded Assets — and
        loads synchronously on every platform. A missing file or key falls back to the
        compiled default; unknown keys are ignored; enum values are lowercase strings,
        parsed case-insensitively. No scripting define symbols are involved. The knob set
        is family-canonical — the same semantics and defaults on every Reactive UI
        Toolkit leg (each leg spells the key names engine-natively), keys marked{' '}
        <em>(Unity-only)</em> excepted — and every default reproduces the untouched
        behavior:
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary={<><code>environment</code> — <code>auto</code> (default: <code>development</code> in the editor and development builds, <code>production</code> in release builds), <code>development</code>, or <code>production</code>. Exposed at runtime as <code>HostContext.Environment["env"]</code>.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>time_slicing</code> — default <code>true</code>. <code>false</code> is an explicit scheduler bypass: every render runs the synchronous work loop even when a scheduler is installed.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>time_slice_ms</code> — default <code>2.0</code>. Maximum milliseconds the render phase runs per slice before yielding back to the scheduler; applies wherever a scheduler slices, the editor included.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>frame_budget_ms</code> — default <code>4.0</code>. Per-frame budget of the play-mode/player <code>RenderScheduler</code> queues. Unity note: editor mounts use the editor scheduler, which is unbudgeted by design (it drains fully every editor update), so this value does not apply there.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>host_node_pool</code> — default <code>true</code>. Gates the uGUI host-element pool (only adapters that provably reset an element ever pool it); off destroys instead of pooling. The UI Toolkit path is never pooled.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>hook_validation</code> — <code>auto</code> (default), <code>on</code>, or <code>off</code>. Validates hook order and count on every render (catches conditional hook calls). <code>auto</code> = on in the editor and development builds, off in release players.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>strict_diagnostics</code> — <code>auto</code> (default), <code>on</code>, or <code>off</code>. Development warnings for suspect hook usage (state updates during render, hooks invoked without a dependency array), deduplicated per component and prefixed <code>[Hooks][Strict]</code>. <code>auto</code> = on in the editor and development builds, off in release players.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>strict_mode</code> — default <code>false</code>. Double-invokes render functions so impure render bodies surface: the first result is discarded, the second is reconciled; effects, layout effects, memo/callback factories, and cleanups still run once, and the committed UI is identical to strict-off. Forced off in release builds — a shipped player can never activate it.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>trace_level</code> — <code>none</code> (default), <code>basic</code>, or <code>verbose</code>. <code>basic</code> logs structural reconciler events (placements, deletions, replacements, a per-commit summary); <code>verbose</code> adds per-element/per-hook detail. Note: under <code>strict_mode</code> the <code>verbose</code> per-hook capture logs fire on both render invokes — twice per render — which is truthful: two captures happened.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>diff_tracing</code> — default <code>false</code>. Detailed Fiber diff logs (props application, update dumps), independent of <code>trace_level</code>: diff tracing alone lights them, and <code>verbose</code> alone does too.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>diagnostics_output_folder</code> <em>(Unity-only)</em> — where benchmark results and log captures are written. Empty = <code>&lt;project&gt;/Logs/ReactiveUIToolkit</code> in the editor and <code>&lt;persistentDataPath&gt;/ReactiveUIToolkit</code> in players; absolute paths are used as-is, relative paths resolve against the project root (editor) or <code>persistentDataPath</code> (player). Diagnostics never write into the package folder.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>mount_watchdog</code> <em>(Unity-only)</em> — default <code>true</code>. Unity 6.5 PanelRenderer workaround (case IN-150082 + UUM-147875): forces the attach path when an enabled, configured renderer never delivers its UI reload callback. Symptom-gated — inert on fixed editors. See <MuiLink component={RouterLink} to="/known-issues">Known Issues</MuiLink>.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>nested_prevention</code> <em>(Unity-only)</em> — default <code>true</code>. Unity 6.5 workaround (UUM-148452): disables nested child PanelRenderers around rebuilds the library itself triggers so the release cascade cannot poison them.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>nested_repair</code> <em>(Unity-only)</em> — default <code>true</code>. Unity 6.5 workaround (UUM-148452): a nested PanelRenderer whose tree was released with no follow-up callback destroys and re-adds itself with all settings copied. Symptom-gated.</>} />
        </ListItem>
      </List>
      <Typography variant="body2" paragraph>
        <strong>Per-developer preferences</strong> live in the same window: the{' '}
        <strong>Hot Reload (HMR)</strong> section (the HMR toggles and the two keybind
        recorders) and the <strong>Console navigation</strong> section (verbose
        console-navigation logging). They are EditorPrefs, per machine, not per project. The
        HMR window itself keeps only Start/Stop, status, and stats, and links to the
        settings window.
      </Typography>
      <Typography variant="body2" paragraph>
        <strong>Legacy fallback:</strong> resolution order for every setting is the JSON
        settings file → legacy <code>Assets/ReactiveUIToolkit/config.json</code>{' '}
        (<code>envVariables</code> block, read via <code>RuitkConfig.Current</code>) →
        compiled defaults, so projects that edited the old file keep working whenever no
        settings file exists. The legacy file only ever carried{' '}
        <code>env</code>/<code>traceLevel</code>/<code>diffTracing</code> — the newer knobs
        have no legacy spelling and resolve settings file → compiled default. The shipped{' '}
        <code>config.json</code> no longer contains an <code>envVariables</code> block.
      </Typography>
    </Box>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Rendering pipeline
      </Typography>
      <Typography variant="body1" paragraph>
        Understanding the pipeline helps you read error messages and diagnose performance:
      </Typography>
      <Typography component="ol" variant="body2">
        <li>
          <strong>Author</strong> — You write <code>.uitkx</code> markup with setup code and a{' '}
          <code>return (...)</code> statement.
        </li>
        <li>
          <strong>Generate</strong> — The Roslyn source generator compiles each{' '}
          <code>.uitkx</code> file into a C# class with a{' '}
          <code>Render(IProps, children)</code> method that returns a{' '}
          <code>VirtualNode</code> tree.
        </li>
        <li>
          <strong>Mount</strong> — <code>V.Func(Component.Render)</code> wraps the generated method
          as a <code>VirtualNode</code>. The <code>RootRenderer</code> (or{' '}
          <code>EditorRootRendererUtility</code>) mounts it into a{' '}
          <code>VisualElement</code> root.
        </li>
        <li>
          <strong>Reconcile</strong> — On each render, the Fiber reconciler diffs the old and new{' '}
          <code>VirtualNode</code> trees, computing the minimal set of patches.
        </li>
        <li>
          <strong>Commit</strong> — Patches are applied to the live{' '}
          <code>VisualElement</code> tree: elements are created, removed, or updated.
        </li>
        <li>
          <strong>Effects</strong> — After commit, cleanup functions from the previous render run,
          then new <code>UseEffect</code> / <code>UseLayoutEffect</code> callbacks fire.
        </li>
      </Typography>
    </Box>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Component lifecycle
      </Typography>
      <Typography component="ul" variant="body2">
        <li>
          <strong>Mount</strong> — construct <code>VirtualNode</code> → create{' '}
          <code>VisualElement</code> → run effects.
        </li>
        <li>
          <strong>Update</strong> — re-render → diff → patch → run cleanup → run new effects.
        </li>
        <li>
          <strong>Unmount</strong> — run all cleanup functions → remove{' '}
          <code>VisualElement</code> from the tree.
        </li>
      </Typography>
    </Box>
  </Box>
)
