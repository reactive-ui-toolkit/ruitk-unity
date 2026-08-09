import type { FC } from 'react'
import { Alert, Box, List, ListItem, ListItemText, Typography } from '@mui/material'
import { CodeBlock } from '../../components/CodeBlock/CodeBlock'
import Styles from './KnownIssuesPage.style'

export const KnownIssuesPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Known Issues
    </Typography>

    {/* ── Unity 6.5 text generation (ATG) ─────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Unity 6.5: Advanced Text Generator is now the default
    </Typography>
    <Typography variant="body1" paragraph>
      Unity 6.5 makes the Advanced Text Generator (ATG) the default text system for UI Toolkit at
      runtime. Unity states <em>feature</em> parity with the previous generator, but not{' '}
      <em>measurement</em> parity: ATG shapes text through a different stack, so measured text sizes
      and the exact points at which lines wrap can differ from Unity 6.4 and earlier.
    </Typography>
    <Typography variant="body1" paragraph>
      If a layout of yours depends on precise text metrics or on specific wrap points, compare it
      across 6.4 and 6.5 after upgrading. Community reports (English, Chinese and Hebrew) describe
      ATG breaking lines at commas and periods where the previous generator did not.
    </Typography>
    <Typography variant="body1" paragraph>
      The opt-out is a <strong>cascading USS property</strong>, not a project setting, so you can
      switch generators for a single subtree rather than the whole application:
    </Typography>
    <CodeBlock
      language="jsx"
      code={`// Typed Style API - already supported, no library changes needed
new Style {
    UnityTextGenerator = new StyleEnum<TextGeneratorType>(TextGeneratorType.Standard),
}

/* or in USS, inherited by everything below the selector */
:root {
    -unity-text-generator: standard;
}`}
    />
    <List>
      <ListItem disablePadding>
        <ListItemText primary="Static font assets are not supported by ATG - migrate them to dynamic font assets." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary={'Rich text parsing is stricter: <style=blue> must become <style="blue">, and <align=flush> is unsupported.'} />
      </ListItem>
    </List>
    <Alert severity="info" sx={{ mt: 1 }}>
      Unity has said it intends to remove the opt-out in a future release, so treat{' '}
      <code>-unity-text-generator: standard</code> as a migration runway rather than a permanent
      setting.
    </Alert>

    {/* ── Unity 6.5 PanelRenderer (delete this section wholesale when the Unity
           bugs below are fixed and the package's Unity floor is past the fixes) ── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Unity 6.5: PanelRenderer host — known Unity issues and shipped workarounds
    </Typography>
    <Typography variant="body1" paragraph>
      The library hosts Unity 6.5's <code>PanelRenderer</code> via{' '}
      <code>RootRenderer.Initialize(panelRenderer)</code>. Unity 6000.5.x has open issues around
      the component that the library works around automatically. Every workaround is{' '}
      <strong>symptom-gated</strong>: it observes the failure itself (a callback that never
      arrived, a tree Unity marked released) rather than the Unity version, so on a fixed editor
      the code is inert with no action on your side — and each one has an opt-out key in the
      settings file.
    </Typography>
    <List>
      <ListItem disablePadding>
        <ListItemText primary={<><strong>Nested renderer never mounts (Unity case IN-150082, editor-only).</strong> A <code>PanelRenderer</code> whose GameObject sits under another <code>PanelRenderer</code> can silently never receive its UI reload callback in the editor — the UI just never appears. The library's mount watchdog detects the missing callback and forces the attach path (a <code>panelSettings</code> round-trip, escalating if needed). Opt-out: <code>mount_watchdog</code>.</>} />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary={<><strong>Disabled-in-Awake renderer never inserts its root (UUM-147875, fixed in 6000.5.7f1).</strong> The common "disable all screens at startup, enable one on demand" pattern hits this on 6000.5.0–6000.5.6. Same watchdog, same mechanism, same opt-out.</>} />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary={<><strong>Parent rebuild releases nested children (UUM-148452, open — editor AND player).</strong> Rebuilding a parent panel calls <code>ReleaseResources()</code> down into nested children; a released tree throws on any touch. Around rebuilds the library itself triggers, nested child renderers are briefly disabled so the cascade cannot reach them (opt-out: <code>nested_prevention</code>). If Unity releases a nested child's tree anyway and no follow-up callback arrives, the child destroys and re-adds its own <code>PanelRenderer</code> with every setting copied — in the editor through Undo, so the repair is one Ctrl+Z (opt-out: <code>nested_repair</code>). A repair loses serialized references pointing at the old component; if you hold such references, disable the repair and avoid nesting instead. During the repair Unity's own cleanup may log an <code>InvalidOperationException</code> ("Trying to modify a released panel tree") from inside its native boundary — it is Unity's, it is harmless, and the repair completes; judge the repair by the outcome, not the log line.</>} />
      </ListItem>
    </List>
    <Typography variant="body1" paragraph>
      <strong>Remount semantics.</strong> When Unity releases the mounted tree wholesale — saving
      a <code>.uxml</code> that is the renderer's Source Asset, or reassigning{' '}
      <code>panelSettings</code> at runtime — nothing is salvageable, so the library drops the old
      tree without touching it (effect cleanups still run) and remounts fresh: transient state
      (hooks, scroll positions, focus) is lost by design. If the renderer is orphaned{' '}
      <em>without</em> being released (disable/enable, editor panel rebuilds), the mounted tree is
      reused or retargeted and all state survives. For a fully code-driven UI, leave{' '}
      <strong>Source Asset empty</strong> — the frequent editor triggers then never release the
      tree at all.
    </Typography>
    <Typography variant="body1" paragraph>
      The library warns once at mount when <code>visualTreeAsset</code> is set (remount hazard)
      and when <code>parentUI</code> is set (the nested-renderer limitations above). If these
      issues affect you, voting on the Unity tracker entries (IN-150082, UUM-148452) helps get
      them fixed — the workarounds are removed once Unity ships fixes and the package's Unity
      floor moves past them.
    </Typography>

    {/* ── Runtime ─────────────────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Runtime
    </Typography>
    <Typography variant="body1" paragraph>
      There is a known issue where <code>MultiColumnListView</code> can briefly
      jump or snap when scrolling large data sets; this will be addressed in a
      future update.
    </Typography>

    {/* ── Burst AOT ───────────────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Burst AOT &amp; Assembly Resolution
    </Typography>
    <Typography variant="body1" paragraph>
      If you encounter the error:
    </Typography>
    <CodeBlock
      language="jsx"
      code="Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly: Assembly-CSharp-Editor"
    />
    <Typography variant="body1" paragraph>
      Go to <strong>Edit → Project Settings → Burst AOT Settings</strong> and
      add <code>Assembly-CSharp-Editor</code> to the exclusion list. This
      prevents Burst from trying to AOT-compile editor-only assemblies that
      reference UITKX types.
    </Typography>

    {/* ── HMR Limitations ─────────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      HMR Limitations
    </Typography>
    <List>
      <ListItem disablePadding>
        <ListItemText primary="Old assemblies from HMR swaps cannot be unloaded by Mono. Each swap leaks approximately 10–30 KB. This is negligible for normal development sessions but accumulates over very long sessions." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary="The first HMR compile is ~1–1.5 seconds due to Roslyn JIT warmup. Subsequent compiles are 25–100 ms." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary="Brand-new .uitkx files are auto-discovered while HMR runs (the watcher covers Assets/, and unknown components referenced from an edited file are found and compiled automatically) — no restart needed. The new component behaves like any other on subsequent saves." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary="A file declaring MULTIPLE components hot-swaps only its FIRST component during an HMR session; edits to the later components in that file take effect on the next full compile. One component per file (the recommended convention) avoids this entirely." />
      </ListItem>
    </List>

    {/* ── Render Depth ────────────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Component Tree Depth
    </Typography>
    <Typography variant="body1" paragraph>
      The reconciler enforces a maximum render depth of <strong>25</strong>{' '}
      nested re-renders per component. If a component calls{' '}
      <code>setState</code> unconditionally during its setup code (creating an
      infinite render loop), the depth guard stops it and logs an error. This is
      not configurable — restructure your component to move state updates into
      event handlers or effects.
    </Typography>

    {/* ── Hooks ───────────────────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Hook Constraints
    </Typography>
    <List>
      <ListItem disablePadding>
        <ListItemText primary="Hooks must be called unconditionally at the top of the component's setup code. Calling hooks inside @if, @for, or other control blocks breaks hook ordering and causes runtime errors." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary="Thread safety: hooks are NOT thread-safe. All hook calls must happen on the main thread during the render cycle. Signal values can be read/written from any thread, but UseSignal() itself is a hook and follows hook rules." />
      </ListItem>
    </List>

    {/* ── Editor vs Runtime ───────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Editor vs Runtime Differences
    </Typography>
    <List>
      <ListItem disablePadding>
        <ListItemText primary={<>Editor uses <code>EditorRenderScheduler</code> (tied to <code>EditorApplication.update</code>), while runtime uses <code>RenderScheduler</code> (tied to <code>MonoBehaviour.Update</code>). Scheduling timing may differ slightly.</>} />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary={<>Drag events (<code>onDragEnter</code>, <code>onDragLeave</code>, <code>onDragUpdated</code>, <code>onDragPerform</code>, <code>onDragExited</code>) are editor-only and require <code>UNITY_EDITOR</code>.</>} />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary={<>Some components are editor-only: <code>PropertyField</code>, <code>InspectorElement</code>, <code>ObjectField</code>, <code>ColorField</code>, <code>EnumFlagsField</code>, <code>Toolbar</code> and its children, <code>TwoPaneSplitView</code>, <code>HelpBox</code>, <code>IMGUIContainer</code>.</>} />
      </ListItem>
    </List>

    <Alert severity="info" sx={{ mt: 2 }}>
      For troubleshooting build or LSP issues, see the{' '}
      <strong>Debugging Guide</strong>.
    </Alert>
  </Box>
)
