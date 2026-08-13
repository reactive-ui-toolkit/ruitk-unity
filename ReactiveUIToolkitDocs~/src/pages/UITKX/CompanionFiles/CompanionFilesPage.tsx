import type { FC } from 'react'
import { Link as MuiLink } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
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
import { CodeBlock } from '../../../components/CodeBlock/CodeBlock'
import Styles from '../../GettingStarted/GettingStartedPage.style'
import {
  EXAMPLE_UITKX,
  EXAMPLE_GENERATED_CLASS,
  EXAMPLE_DIRECTORY,
  EXAMPLE_HOOKS,
  EXAMPLE_STYLES,
  EXAMPLE_TYPES,
  EXAMPLE_UTILS,
  EXAMPLE_STANDALONE,
} from './CompanionFilesPage.example'

export const CompanionFilesPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Companion Files
    </Typography>
    <Typography variant="body1" paragraph>
      The source generator produces a <strong>complete C# class</strong> from every{' '}
      <code>.uitkx</code> file — namespace, partial class, <code>Render()</code> method, and
      everything else. A component can work with just a single <code>.uitkx</code> file.
    </Typography>
    <Typography variant="body1" paragraph>
      Companion files are <strong>optional</strong> <code>.uitkx</code> files that live next to a
      component. Each one is an <strong>ordinary module</strong> — a file of plain{' '}
      <code>export</code> declarations that the component <code>import</code>s. Use them to
      extract reusable state logic, style values, or utility functions.
    </Typography>
    <Box sx={{ my: 2, p: 2, borderLeft: '4px solid', borderColor: 'primary.main', bgcolor: 'action.hover' }}>
      <Typography variant="body2">
        A file IS a module: cross-file references go through explicit <code>import</code>s. The
        legacy behaviour where a same-named companion merged into the component&rsquo;s partial
        class was <strong>removed in 0.16.0</strong> together with the wrapper keywords. The
        file-kind naming rules below (one component per file, <code>.hooks</code>/
        <code>.style</code> suffixes, filename == component) are{' '}
        <strong>documentation conventions</strong> rather than compiler-enforced requirements. A
        single file may declare any mix of components, hooks, and values. See{' '}
        <MuiLink component={RouterLink} to="/imports">Imports &amp; Exports</MuiLink> for the model, the
        recommended conventions, and the migration codemod.
      </Typography>
    </Box>

    <Typography variant="h5" component="h2" gutterBottom>
      The UITKX component
    </Typography>
    <Typography variant="body1" paragraph>
      Here is a component that uses styles, types, and utility functions defined in companion files:
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_UITKX} />

    <Typography variant="h5" component="h2" gutterBottom>
      Generated namespace &amp; class name
    </Typography>
    <Typography variant="body1" paragraph>
      The source generator creates a C# class from the <code>.uitkx</code> file. Three things
      determine its identity:
    </Typography>
    <List>
      <ListItem disablePadding>
        <ListItemText
          primary={
            <>
              <strong>Namespace</strong> — <strong>file-keyed</strong> by default: derived from
              the file&rsquo;s folders relative to its owning <code>.asmdef</code> <em>plus its
              file stem</em> (e.g. <code>Ruitk.Uitkx.UI.PlayerCard.PlayerCard</code>), so
              every file gets its own namespace. The optional <code>@namespace</code> directive
              overrides it — useful interop escape hatch when a hand-written partial{' '}
              <code>.cs</code> must share the generated class&rsquo;s namespace. See{' '}
              <MuiLink component={RouterLink} to="/imports">Imports &amp; Exports</MuiLink>.
            </>
          }
        />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText
          primary={
            <>
              <strong>Class name</strong> — comes from the component declaration&rsquo;s name
              (<code>export VirtualNode PlayerCard(...)</code> emits class{' '}
              <code>PlayerCard</code>). Value/hook/util exports land on a per-file{' '}
              <code>__Exports</code> container instead.
            </>
          }
        />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText
          primary={
            <>
              <strong>Accessibility</strong> — an <code>export</code>ed declaration emits a{' '}
              <code>public partial class</code>; without <code>export</code> it is{' '}
              <code>internal</code> and invisible to imports (file-private).
            </>
          }
        />
      </ListItem>
    </List>
    <Typography variant="body1" paragraph>
      For the example above, the generator produces:
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_GENERATED_CLASS} />

    <Typography variant="h5" component="h2" gutterBottom>
      Directory layout
    </Typography>
    <Typography variant="body1" paragraph>
      Place companion files in the <strong>same directory</strong> as the <code>.uitkx</code>{' '}
      component:
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_DIRECTORY} />

    <Typography variant="h5" component="h2" gutterBottom>
      File types
    </Typography>
    <TableContainer component={Paper} variant="outlined" sx={{ mb: 2 }}>
      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell><strong>File</strong></TableCell>
            <TableCell><strong>Contains</strong></TableCell>
            <TableCell><strong>Purpose</strong></TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          <TableRow>
            <TableCell><code>MyComponent.uitkx</code></TableCell>
            <TableCell><code>export VirtualNode …</code></TableCell>
            <TableCell>UI markup + setup code</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><code>MyComponent.hooks.uitkx</code></TableCell>
            <TableCell><code>export (…) useX(…)</code></TableCell>
            <TableCell>Custom hooks — reusable state logic</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><code>MyComponent.style.uitkx</code></TableCell>
            <TableCell><code>export Style x = …</code></TableCell>
            <TableCell>Style constants, helpers, colours, sizes</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><code>MyComponentTypes.cs</code></TableCell>
            <TableCell>ambient C#</TableCell>
            <TableCell>Enums, structs, DTOs used by the component (nested types have no plain-declaration form)</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><code>MyComponent.utils.uitkx</code></TableCell>
            <TableCell><code>export string F(…) =&gt; …</code></TableCell>
            <TableCell>Pure helper / formatting functions</TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </TableContainer>
    <Typography variant="body2" paragraph>
      All companion files end in <code>.uitkx</code>. The naming conventions (
      <code>.hooks.</code>, <code>.style.</code>, <code>.utils.</code>) are recommendations, not
      enforced rules.
    </Typography>

    <Typography variant="h5" component="h2" gutterBottom>
      Hooks — reusable state logic
    </Typography>
    <Typography variant="body1" paragraph>
      A <code>use</code>-prefixed export is a hook — reusable stateful logic. Hook bodies are
      pure C# and can call <code>useState</code>, <code>useEffect</code>, <code>useMemo</code>,
      and any other built-in hooks. The return type leads the declaration, tuple returns
      included:
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_HOOKS} />

    <Typography variant="h5" component="h2" gutterBottom>
      Style values
    </Typography>
    <Typography variant="body1" paragraph>
      Style constants and helpers are plain value exports — the component imports the names it
      uses.
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_STYLES} />

    <Typography variant="h5" component="h2" gutterBottom>
      Type definitions
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_TYPES} />

    <Typography variant="h5" component="h2" gutterBottom>
      Utility functions
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_UTILS} />

    <Typography variant="h5" component="h2" gutterBottom>
      Standalone modules
    </Typography>
    <Typography variant="body1" paragraph>
      Not everything has to be tied to a component. Any <code>.uitkx</code> file is a module, so
      shared values can live wherever they read best and be imported from anywhere in the asmdef:
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_STANDALONE} />

    <Typography variant="h5" component="h2" gutterBottom>
      HMR support
    </Typography>
    <List>
      <ListItem disablePadding>
        <ListItemText primary="Editing a hook file triggers HMR — the hook delegate is swapped in-place and all components re-render with the new logic." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary="Hook state is preserved across swaps (useState, useRef, useEffect, etc.)." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary="Value and utility changes hot-reload in-place: value initializers are re-evaluated and the fresh values are copied into the live __Exports type, and utility method bodies are swapped via per-method delegate trampolines. No domain reload required." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary="Adding a NEW export to a file mid-session is a CLR rude edit — the project type's metadata cannot grow at runtime. HMR auto-recovers by scheduling a domain reload (configurable via the HMR window's 'Auto-reload on rude edit' toggle, default on; or EditorPref UITKX_HMR_AutoReloadOnRudeEdit). A once-per-session warning is logged either way." />
      </ListItem>
    </List>

    <Typography variant="h5" component="h2" gutterBottom>
      When not to use companion files
    </Typography>
    <List>
      <ListItem disablePadding>
        <ListItemText primary="Simple components — if a component has no shared styles or types, it doesn't need any companion files." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText
          primary={
            <>
              Small helpers — for code that only the component uses, prefer
              setup code before <code>return()</code> inside the <code>.uitkx</code> file itself.
            </>
          }
        />
      </ListItem>
    </List>
  </Box>
)
