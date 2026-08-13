import type { FC } from 'react'
import { Box, Link as MuiLink, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Typography } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import { CodeBlock } from '../../components/CodeBlock/CodeBlock'
import { VersionBadge } from '../../components/VersionBadge/VersionBadge'
import Styles from '../GettingStarted/GettingStartedPage.style'
import {
  EXAMPLE_UIDOCUMENT,
  EXAMPLE_VISUALELEMENT,
  EXAMPLE_PANELRENDERER,
  EXAMPLE_ENV,
  EXAMPLE_EDITOR,
} from './MountingPage.example'

export const MountingPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Mounting &amp; Roots
    </Typography>
    <Typography variant="body1" paragraph>
      <code>RootRenderer</code> is the <code>MonoBehaviour</code> that hosts a component tree:
      you <code>Initialize</code> it against a UI host, then <code>Render</code> a{' '}
      <code>VirtualNode</code> (usually <code>V.Func(MyComponent.Render)</code>) into it. There
      are three hosts to mount into, plus a dedicated editor-window path and the uGUI backend.
    </Typography>

    <TableContainer>
      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell>Host</TableCell>
            <TableCell>Call</TableCell>
            <TableCell>When</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          <TableRow>
            <TableCell><code>UIDocument</code></TableCell>
            <TableCell><code>Initialize(uiDocument)</code></TableCell>
            <TableCell>Runtime UI Toolkit — the recommended default</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><code>VisualElement</code></TableCell>
            <TableCell><code>Initialize(someElement)</code></TableCell>
            <TableCell>Static root — custom hosts, tests, bespoke panels</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><code>PanelRenderer</code> <VersionBadge feature={{ sinceUnity: '6000.5' }} /></TableCell>
            <TableCell><code>Initialize(panelRenderer)</code></TableCell>
            <TableCell>Unity 6.5&rsquo;s world-space / render-texture panel host</TableCell>
          </TableRow>
          <TableRow>
            <TableCell>Editor window</TableCell>
            <TableCell><code>EditorRootRendererUtility.Mount(element, node)</code></TableCell>
            <TableCell>Custom <code>EditorWindow</code>s and inspectors</TableCell>
          </TableRow>
          <TableRow>
            <TableCell>uGUI canvas</TableCell>
            <TableCell><code>UguiRootRenderer</code></TableCell>
            <TableCell>The <MuiLink component={RouterLink} to="/ugui">uGUI backend</MuiLink> (<code>@backend ugui</code> files)</TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </TableContainer>

    <Typography variant="h5" component="h2" gutterBottom sx={{ mt: 3 }}>
      UIDocument (recommended)
    </Typography>
    <Typography variant="body1" paragraph>
      Prefer <code>Initialize(UIDocument)</code> over passing{' '}
      <code>rootVisualElement</code> directly: the renderer polls the document&rsquo;s root once
      per frame (one <code>ReferenceEquals</code>, no allocations) and migrates the mounted fiber
      tree onto the new root whenever Unity rebuilds the panel — which a known Unity 6.3
      regression does silently on every Inspector redraw.
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_UIDOCUMENT} />

    <Typography variant="h5" component="h2" gutterBottom>
      VisualElement (static root)
    </Typography>
    <Typography variant="body1" paragraph>
      The plain-element overload mounts into exactly the element you pass and never re-targets.
      Use it when there is no <code>UIDocument</code>; if the host is rebuilt you re-call{' '}
      <code>Render</code> yourself from the code that triggered the rebuild.
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_VISUALELEMENT} />

    <Typography variant="h5" component="h2" gutterBottom>
      PanelRenderer (Unity 6.5+) <VersionBadge feature={{ sinceUnity: '6000.5' }} />
    </Typography>
    <Typography variant="body1" paragraph>
      Unity 6.5&rsquo;s <code>PanelRenderer</code> hosts a panel in world space or on a render
      texture. Its root element is internal, so the mount is <strong>callback-driven</strong>: a{' '}
      <code>Render</code> call issued before the panel exists is held and replayed automatically
      when the renderer delivers it. The tree mounts into a library-owned sub-root
      (<code>__ruitk_root</code>) rather than Unity&rsquo;s root — Unity rewrites its own root
      every frame — and <code>V.Host</code> props land on that sub-root. World-space
      configuration (<code>worldSpaceSizeMode</code>, <code>worldSpaceSize</code>, position,
      pivot) stays on the <code>PanelRenderer</code> component itself: Unity owns those, and the
      mounted UI follows.
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_PANELRENDERER} />
    <Typography variant="body1" paragraph>
      Panel rebuilds are handled by the release state of the mounted tree: <em>reuse in place</em>{' '}
      when nothing moved, <em>retarget</em> (state preserved) when the tree was orphaned but not
      released, and a <em>fresh remount</em> (hook state dropped by design) when Unity released
      the subtree — e.g. saving a Source Asset <code>.uxml</code> or reassigning{' '}
      <code>panelSettings</code> at runtime. Known Unity 6000.5.x issues around nested renderers
      are covered by symptom-gated workarounds (<code>mount_watchdog</code>,{' '}
      <code>nested_prevention</code>, <code>nested_repair</code>) — see{' '}
      <MuiLink component={RouterLink} to="/known-issues">Known Issues</MuiLink> for the details
      and opt-outs.
    </Typography>

    <Typography variant="h5" component="h2" gutterBottom>
      Seeding the environment
    </Typography>
    <Typography variant="body1" paragraph>
      Every <code>Initialize</code> overload takes an optional <code>env</code> callback that
      runs once against the renderer&rsquo;s <code>HostContext</code> — the place to seed named{' '}
      <MuiLink component={RouterLink} to="/tooling/portal">portal</MuiLink> target slots or
      environment values before the first render:
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_ENV} />

    <Typography variant="h5" component="h2" gutterBottom>
      Editor windows
    </Typography>
    <Typography variant="body1" paragraph>
      For custom editor windows use <code>EditorRootRendererUtility.Mount</code> — it sets up the
      editor scheduler, signals runtime, and diagnostics automatically, and keeps one renderer
      per host element (repeat calls re-render instead of re-mounting):
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_EDITOR} />
  </Box>
)
