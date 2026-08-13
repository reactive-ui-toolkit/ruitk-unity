import type { FC } from 'react'
import {
  Box,
  List,
  ListItem,
  ListItemText,
  Typography,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Paper,
} from '@mui/material'
import Styles from './HmrPage.style'

const Section: FC<{ title: string; children: React.ReactNode }> = ({ title, children }) => (
  <Box>
    <Typography variant="h5" component="h2" gutterBottom>
      {title}
    </Typography>
    {children}
  </Box>
)

export const HmrPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Hot Module Replacement
    </Typography>
    <Typography variant="body1" paragraph>
      Hot Module Replacement lets you edit <code>.uitkx</code> files and see changes instantly in
      the Unity Editor — without domain reload, without losing component state.
    </Typography>

    <Section title="Quick Start">
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary={<>Open <strong>Ruitk → HMR Mode</strong> from the Unity menu bar.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Click <strong>Start HMR</strong>.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Edit and save any <code>.uitkx</code> file.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="The component updates in-place — hook state (counters, refs, effects) is preserved." />
        </ListItem>
      </List>
    </Section>

    <Section title="How It Works">
      <Typography variant="body1" paragraph>
        When HMR is active:
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="Assembly reloads are locked — no domain reload occurs on file saves." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>A <code>FileSystemWatcher</code> detects <code>.uitkx</code> changes under <code>Assets/</code>.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>The file is parsed and emitted to C# using <code>Ruitk.Language.dll</code>.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>C# is compiled in-process via Roslyn (<code>Microsoft.CodeAnalysis.CSharp</code> 4.3.1), with automatic fallback to external <code>csc.dll</code> if Roslyn DLLs aren't available.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>The compiled assembly is loaded via <code>Assembly.Load(byte[])</code>.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>The new <code>Render</code> delegate is swapped into all active <code>RootRenderer</code> instances.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="A re-render is triggered — hooks run against preserved state." />
        </ListItem>
      </List>
      <Typography variant="body1" paragraph>
        Total time: typically <strong>25–100 ms</strong> compile + emit from save to visual update
        (first compile per session is ~1–1.5s due to Roslyn JIT warmup).
      </Typography>
    </Section>

    <Section title="State Preservation">
      <Typography variant="body1" paragraph>
        HMR preserves all hook state across swaps. The table below details behaviour per hook:
      </Typography>
      <TableContainer component={Paper} variant="outlined" sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell><strong>Hook</strong></TableCell>
              <TableCell><strong>HMR Behaviour</strong></TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell><code>useState</code></TableCell>
              <TableCell>Current values retained across swaps.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell><code>useRef</code></TableCell>
              <TableCell>Ref objects preserved; <code>.Current</code> unchanged.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell><code>useEffect</code></TableCell>
              <TableCell>Cleanup runs, then the effect re-runs with the new closure.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell><code>useMemo</code> / <code>useCallback</code></TableCell>
              <TableCell>Recomputed with the new function body. <code>useCallback</code> returns <code>{'Func<T>'}</code>, not <code>Action</code>.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell><code>useContext</code></TableCell>
              <TableCell>Stateless — reads the current provider value without occupying a hook slot. Always reflects the latest value.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell><code>useImperativeHandle</code></TableCell>
              <TableCell>Handle recreated by calling the new factory. Parent refs receive the updated handle.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell><code>useStableFunc</code> / <code>useStableAction</code> / <code>useStableCallback</code></TableCell>
              <TableCell>Wrapper identity preserved; inner delegate silently replaced with the new closure.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell><code>useAnimate</code> / <code>useTweenFloat</code></TableCell>
              <TableCell>Animation state resets and tracks are re-evaluated from the new definition.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell><code>useDeferredValue</code></TableCell>
              <TableCell>Deferred value is recalculated from the new upstream value on the next render.</TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
      <Typography variant="body1" paragraph sx={{ mt: 1 }}>
        If the number, order, or types of hooks change between edits (an incompatible signature
        change), HMR resets state for every live instance of the component and logs{' '}
        <code>[HMR] Hook signature changed in &lt;Component&gt; — resetting state on all instances.</code>{' '}
        State resets in-place without a domain reload, matching React Fast Refresh semantics; you
        do not need to recompile, restart Play mode, or close any windows.
      </Typography>
    </Section>

    <Section title="Companion Files">
      <Typography variant="body1" paragraph>
        Companion <code>.uitkx</code> files using <code>hook</code> and <code>module</code>{' '}
        keywords are fully supported by HMR:
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary={<><strong>Hook files</strong> (e.g. <code>MyComponent.hooks.uitkx</code>) — the hook delegate is swapped in-place. All components that use the hook re-render with the new logic. Hook state is preserved.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><strong>Style modules</strong> (e.g. <code>MyComponent.style.uitkx</code>) — <code>static readonly</code> field initializers are re-evaluated and the new values are copied into the live module type. Editing a <code>Style</code>, <code>Color</code>, or any other module-scope <code>static readonly</code> value takes effect on the next render without a domain reload.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><strong>Utility modules</strong> (e.g. <code>MyComponent.utils.uitkx</code>) — <code>static</code> method bodies are hot-swapped via per-method delegate trampolines; <code>static readonly</code> fields are re-initialized like style modules; mutable <code>static</code> fields keep their runtime value across cycles.</>} />
        </ListItem>
      </List>
      <Typography variant="body1" paragraph sx={{ mt: 1 }}>
        Module-scope <code>static readonly</code> fields are transparently emitted as
        <code>[UitkxHmrSwap] static</code> by the source generator so the runtime slot stays
        writable and the Mono JIT cannot inline a stale reference. Writing to these fields from
        non-cctor code triggers analyzer warning <code>UITKX0210</code> — the HMR pipeline will
        overwrite the value on the next save.
      </Typography>
      <Typography variant="body1" paragraph>
        Value exports (<code>export Style Root = …</code>) always emit as swappable fields — the
        generated code carries an HMR-swap marker, so every save re-evaluates the initializer and
        copies the fresh value into the live type. Types you hand-write in ambient <code>.cs</code>{' '}
        files are compiled by Unity, not the uitkx pipeline, and follow Unity&rsquo;s normal
        domain-reload cycle instead.
      </Typography>
      <Typography variant="body1" paragraph>
        Generic hooks (e.g. <code>{'export (T, Action<T>) useSel<T>(...)'}</code>) use a cached
        delegate strategy — first call per type parameter after HMR pays ~1-2µs, subsequent calls
        are direct invocations.
      </Typography>
      <Typography variant="body1" paragraph>
        <strong>Hook identity is path-qualified.</strong> Hot-swap matches an edited hook to the
        components that use it by the key{' '}
        <code>{'{Namespace}.__Exports::{hookName}'}</code> (e.g.{' '}
        <code>MyGame.UI.Counter.__Exports::useCounter</code>), not by the bare hook name — so two
        identically-named hooks in different files (say, two <code>useData</code>s) never
        cross-swap: editing one refreshes only its own consumers. This follows the same file-keyed
        namespace derivation as the generated code, including the path-derived default namespace
        when a file has no <code>@namespace</code>.
      </Typography>
    </Section>

    <Section title="New Component Support">
      <Typography variant="body1" paragraph>
        HMR can compile and load <strong>new</strong> <code>.uitkx</code> files that don't exist in
        any pre-compiled assembly:
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="When a parent component references an unknown child, CS0103 errors are caught." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>HMR scans the project for matching <code>.uitkx</code> files and compiles them first.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="The parent is automatically retried after the dependency resolves." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Cross-component references are managed via an assembly registry." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>New plain <code>.cs</code> helper files referenced from a <code>.uitkx</code> are picked up too, even before Unity has recompiled the project DLL — HMR adds them as additional Roslyn syntax trees, asmdef-scoped and AppDomain-deduped against types Unity already loaded.</>} />
        </ListItem>
      </List>
    </Section>

    <Section title="Save Cascade">
      <Typography variant="body1" paragraph>
        Saving a single <code>.uitkx</code> file now enqueues every transitive consumer in the
        same asmdef and recompiles them in topological order. The drain runs through
        <code> EditorApplication.delayCall</code> so the editor stays responsive between compiles.
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="Editing a child component's body now updates parent renderers without requiring a manual second save." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Module value edits (e.g. <code>Theme.Accent</code>) propagate through every derived module field (<code>StatsPanel.Container.BorderColor</code>) automatically.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>When a cascade pulls in two or more files within one asmdef, HMR compiles them in a single union assembly so a refactored <code>ChildProps</code> shape resolves to a single authoritative type across parent + child. Falls back to per-file compile on failure so the user-facing error surface (CS0117 / CS0246 / CS0433) is preserved.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Telemetry: <code>[HMR] union: N files, M ms</code> on a successful batch compile.</>} />
        </ListItem>
      </List>
    </Section>

    <Section title="HMR Window">
      <Typography variant="body1" paragraph>
        The HMR window (<strong>Ruitk → HMR Mode</strong>) shows:
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="Start / Stop button with status indicator." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Stats: swap count, error count, last component name and timing." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Timing breakdown: Parse, Emit, Compile, and Swap durations per step." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Settings: auto-stop on play mode, swap notifications." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Keyboard Shortcuts: configurable bindings (see below)." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Recent Errors: last 10 compilation errors (scrollable, copyable)." />
        </ListItem>
      </List>
    </Section>

    <Section title="Keyboard Shortcuts">
      <Typography variant="body1" paragraph>
        Shortcuts are not bound by default — configure them in the HMR window to avoid conflicting
        with your existing keybindings.
      </Typography>
      <TableContainer component={Paper} variant="outlined" sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell><strong>Action</strong></TableCell>
              <TableCell><strong>Description</strong></TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>Toggle HMR</TableCell>
              <TableCell>Start or stop the HMR session</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Open / Close Window</TableCell>
              <TableCell>Show or hide the HMR window</TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
      <Typography variant="body2" sx={{ mt: 1 }}>
        Requirements: at least one modifier key (Ctrl, Alt, or Shift) plus one regular key.
      </Typography>
    </Section>

    <Section title="Lifecycle">
      <TableContainer component={Paper} variant="outlined" sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell><strong>Event</strong></TableCell>
              <TableCell><strong>Behavior</strong></TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>Start HMR</TableCell>
              <TableCell>Assembly reload locked, file watcher started</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Stop HMR</TableCell>
              <TableCell>Assembly reload unlocked, pending changes compile normally</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Enter / Exit Play Mode</TableCell>
              <TableCell>Auto-stops HMR (configurable)</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Build (Player)</TableCell>
              <TableCell>Auto-stops HMR</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Editor quit</TableCell>
              <TableCell>Auto-stops HMR</TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
      <Typography variant="body1" paragraph sx={{ mt: 1 }}>
        While HMR is active, <strong>all compilation is deferred</strong> — not just{' '}
        <code>.uitkx</code> changes. Any <code>.cs</code> edits accumulate and compile in one batch
        when HMR is stopped.
      </Typography>
    </Section>

    <Section title="Limitations">
      <TableContainer component={Paper} variant="outlined" sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell><strong>Limitation</strong></TableCell>
              <TableCell><strong>Details</strong></TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>Old assemblies stay in memory</TableCell>
              <TableCell>
                Mono cannot unload assemblies. ~10–30 KB per swap, cleared on domain reload.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>All compilation deferred</TableCell>
              <TableCell>
                Non-UITKX <code>.cs</code> changes don't take effect until HMR stops. UX warning
                shown.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>First compile is slow</TableCell>
              <TableCell>
                ~1–1.5s on first HMR compile per session (Roslyn JIT warmup). Subsequent compiles
                are 25–100ms.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Requires NuGet cache</TableCell>
              <TableCell>
                In-process Roslyn loads DLLs from <code>~/.nuget/packages/</code>. Falls back to
                external <code>csc.dll</code> if unavailable.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Static field changes ignored</TableCell>
              <TableCell>Statics live on the old assembly's type.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Cross-assembly props</TableCell>
              <TableCell>
                Props are read via reflection to handle type mismatches across assemblies.
              </TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
    </Section>

    <Section title="Troubleshooting">
      <Typography variant="h6" component="h3" gutterBottom>
        HMR doesn't start
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="Check the Console for initialization errors." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Ensure <code>Ruitk.Language.dll</code> exists in the <code>Analyzers/</code> folder.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Verify Unity's Roslyn compiler is present at <code>{'${EditorPath}'}/Data/DotNetSdkRoslyn/csc.dll</code>.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Check for <code>[HMR] In-process Roslyn compiler loaded successfully</code> in Console.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>If Roslyn fails to load, verify that <code>~/.nuget/packages/microsoft.codeanalysis.csharp/4.3.1/</code> exists.</>} />
        </ListItem>
      </List>

      <Typography variant="h6" component="h3" gutterBottom sx={{ mt: 2 }}>
        Changes don't appear
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="Confirm the file is saved (not just modified)." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Check the HMR window for compilation errors." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Verify the file is under <code>Assets/</code> (the watched directory).</>} />
        </ListItem>
      </List>

      <Typography variant="h6" component="h3" gutterBottom sx={{ mt: 2 }}>
        State is lost after edit
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="Hook order or count may have changed — this triggers automatic state reset." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Check Console for "<code>[HMR] Hook signature changed</code>" messages.</>} />
        </ListItem>
      </List>
    </Section>
  </Box>
)
