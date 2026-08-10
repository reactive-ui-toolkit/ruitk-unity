import type { FC } from 'react'
import { Link as MuiLink } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import { Box, List, ListItem, ListItemText, Typography } from '@mui/material'
import { CodeBlock } from '../../../components/CodeBlock/CodeBlock'
import Styles from '../../GettingStarted/GettingStartedPage.style'
import {
  UITKX_HELLO_WORLD_BOOTSTRAP,
  UITKX_HELLO_WORLD_COMPONENT,
  UITKX_INSTALL_URL,
  UITKX_EDITOR_BOOTSTRAP,
} from './UitkxGettingStartedPage.example'

export const UitkxGettingStartedPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      UITKX Getting Started
    </Typography>
    <Typography variant="body1" paragraph>
      You write function-style <code>.uitkx</code> components and the source generator produces a
      complete C# class automatically — no boilerplate needed. Supported Unity versions:{' '}
      <strong>Unity 6.2+</strong>.
    </Typography>

    <Typography variant="h5" component="h2" gutterBottom>
      Install via Unity Package Manager
    </Typography>
    <List>
      <ListItem disablePadding>
        <ListItemText primary="Open Package Manager in Unity." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary="Add package from Git URL:" />
      </ListItem>
    </List>
    <CodeBlock language="jsx" code={UITKX_INSTALL_URL} />

    <Typography variant="h5" component="h2" gutterBottom>
      1. Create a UITKX component
    </Typography>
    <Typography variant="body1" paragraph>
      A <code>.uitkx</code> file <strong>is a module</strong>: a sequence of plain typed
      declarations, each optionally <code>export</code>-prefixed. A simple file holds one
      component. (A file may declare any mix of components, hooks, and values; matching the
      filename to the primary component is a recommended convention rather than a hard rule —
      see <MuiLink component={RouterLink} to="/imports">Imports &amp; Exports</MuiLink>.)
      Setup code goes at the top of the body; the component returns markup.
    </Typography>
    <CodeBlock language="jsx" code={UITKX_HELLO_WORLD_COMPONENT} />
    <Typography variant="body1" paragraph>
      The source generator automatically discovers all <code>.uitkx</code> files in your{' '}
      <code>Assets/</code> directory — no registration needed. On the next Unity compile it emits a
      complete C# class (<code>HelloWorld.uitkx.g.cs</code>) with <code>namespace</code>, a{' '}
      <code>partial class</code>, and a full <code>Render()</code> method. The namespace is{' '}
      <strong>file-keyed</strong> — derived from the file&rsquo;s folders plus its file stem
      (here <code>Ruitk.Uitkx.UI.HelloWorld</code>) — so no <code>@namespace</code>{' '}
      directive is needed. <code>export</code> makes the class <code>public</code> and importable
      from other files; without it the declaration is <code>internal</code> and file-private. You
      don&rsquo;t need to create any companion file for this to work.
    </Typography>
    <Typography variant="body2" paragraph>
      Upgrading an existing project? The old wrapper keywords were removed in 0.16.0 — the
      codemod (run from a clone of the repository:{' '}
      <code>node scripts/migrate-uitkx.mjs &lt;YourProject&gt;/Assets --es-modules</code>)
      migrates wrapper-keyword files to plain <code>export</code> declarations in one pass — see{' '}
      <MuiLink component={RouterLink} to="/migration-0-16">Migrating to 0.16</MuiLink>.
    </Typography>

    <Typography variant="h5" component="h2" gutterBottom>
      2. Mount it
    </Typography>
    <Typography variant="body1" paragraph>
      <code>V.Func(HelloWorld.Render)</code> wraps the source-generated <code>Render</code> method
      as a <code>VirtualNode</code> — the reconciler's entry point into your component tree.
      Mount the component at runtime with <code>RootRenderer</code>:
    </Typography>
    <CodeBlock language="jsx" code={UITKX_HELLO_WORLD_BOOTSTRAP} />
    <Typography variant="body1" paragraph>
      <code>RootRenderer.Initialize</code> has two overloads. Prefer{' '}
      <code>Initialize(UIDocument)</code>: it polls <code>rootVisualElement</code> once per frame
      via the panel-independent <code>AnimationTicker</code> and migrates the mounted fiber tree
      onto the new root whenever Unity rebuilds the panel — a known Unity 6.3 regression where
      every <code>InspectorWindow</code> redraw silently recreates the root (reported to Unity;
      distinct from UUM-47682). The legacy <code>Initialize(VisualElement)</code> overload remains
      valid for hosts without a <code>UIDocument</code> (custom <code>EditorWindow</code>, tests,
      bespoke panels). Steady-state cost is one <code>ReferenceEquals</code> per frame; no
      allocations on the hot path.
    </Typography>

    <Typography variant="h5" component="h2" gutterBottom>
      Editor windows
    </Typography>
    <Typography variant="body1" paragraph>
      For custom editor windows use <code>EditorRootRendererUtility.Mount</code> instead — it sets up
      the editor scheduler, signals runtime, and diagnostics automatically:
    </Typography>
    <CodeBlock language="jsx" code={UITKX_EDITOR_BOOTSTRAP} />

    <Typography variant="h5" component="h2" gutterBottom>
      Companion files (optional)
    </Typography>
    <Typography variant="body1" paragraph>
      The generator produces everything needed, but you can optionally extract reusable state
      logic, styles, or utilities into sibling <code>.uitkx</code> files — each one is an ordinary
      module whose <code>export</code>s you <code>import</code> from the component. See the{' '}
      <strong>Companion Files</strong> page for details.
    </Typography>
  </Box>
)
