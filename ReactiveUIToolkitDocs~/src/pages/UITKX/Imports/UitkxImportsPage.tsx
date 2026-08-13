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
import { CodeBlock } from '../../../components/CodeBlock/CodeBlock'
import Styles from '../../GettingStarted/GettingStartedPage.style'
import {
  EXAMPLE_GRAMMAR,
  EXAMPLE_FULL_SURFACE,
  EXAMPLE_SPECIFIERS,
  EXAMPLE_MIXED,
  EXAMPLE_STRICT,
  EXAMPLE_CODEMOD,
  EXAMPLE_NAMESPACE,
  EXAMPLE_NAMESPACE_IMPORT,
} from './UitkxImportsPage.example'

const DIAGS: [string, string][] = [
  ['UITKX2300', 'unknown import specifier — no file at that path (also engine-native specifiers, which never resolve)'],
  ['UITKX2301', '`X` is not exported by that file — add `export` to its declaration'],
  ['UITKX2302', '`X` is imported from that file, but the file declares no `X`'],
  ['UITKX2303', 'duplicate import of `X` (already imported from another specifier)'],
  ['UITKX2304', 'unused import `X` (warning)'],
  ['UITKX2305', '`X` is defined in a peer file but not imported — the message names the exact import line to add. Error for component tags (<X> is uitkx-only syntax); warning for bare hook-call / module member-access matches (plain C# can legitimately produce those shapes)'],
  ['UITKX2306', 'value-import cycle: hooks/values load eagerly, so a cycle among their imports is an error (components are exempt)'],
  ['UITKX2307', '`X` is used like a hook but no file exports it (warning — a hand-written C# hook resolves ambiently and looks identical)'],
  ['UITKX2308', 'import crosses a module/root boundary — imports are asmdef-scoped in v1'],
  ['UITKX2309', 'import must appear in the preamble, before the first declaration'],
  ['UITKX2311', 'export mismatch across declarations merging into one generated type (e.g. two same-named declarations disagree on `export`) — the component wins; align them (warning)'],
  ['UITKX2312', 'hook-container merge conflict: same-named containers in two files disagree (duplicate hook or accessibility)'],
  ['UITKX2314', '`~/` root is not configured or the path resolves outside the project — set `"root"` in uitkx.config.json'],
  ['UITKX2316', 'unknown namespace — a `@using`/`import "@Ns"` whose namespace resolves nowhere in the assembly or its references (the namespace analogue of 2300). Editor error; build warning (the emitted `using`’s CS0246 stays the real gate)'],
  ['UITKX2317', 'redundant using — the namespace is already in scope by default (e.g. `@using UnityEngine`); editor Hint (faded), safe to delete. `--tidy` strips these'],
]

export const UitkxImportsPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Imports &amp; Exports
    </Typography>
    <Typography variant="body1" paragraph>
      Cross-file references in <code>.uitkx</code> are <strong>explicit</strong>. A file
      declares what it exposes with <code>export</code> and pulls in what it needs with{' '}
      <code>import</code> — ESM-style, the same grammar shared across the Reactive UI Toolkit family
      (Unity <code>.uitkx</code>, Unreal <code>.uetkx</code>, Godot <code>.guitkx</code>).
    </Typography>

    <Typography variant="h5" component="h2" gutterBottom>
      Grammar
    </Typography>
    <Typography variant="body1" paragraph>
      A file is a preamble of <code>import</code> lines followed by a sequence of plain
      typed declarations, each optionally <code>export</code>-prefixed. Classification is
      read from the signature alone: a <code>VirtualNode</code> return type is a component
      (PascalCase enforced), a <code>use</code>-prefixed name is a hook, <code>= initializer</code>
      is a value, anything else is a util. Any mix may live in one file, in any order.
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_GRAMMAR} />
    <List>
      <ListItem>
        <ListItemText primary="export" secondary="Makes a declaration visible to other files. No export = file-private (internal + strict-invisible)." />
      </ListItem>
      <ListItem>
        <ListItemText primary="import { A, B } from '…'" secondary="Named imports of peer .uitkx exports. Preamble only (before the first declaration). A namespace import uses a different shape — import '@Namespace' — see below." />
      </ListItem>
    </List>

    <Typography variant="h5" component="h2" gutterBottom>
      The full import surface
    </Typography>
    <Typography variant="body1" paragraph>
      0.9.0 completes the ES surface: rename-on-import (<code>as</code>), namespace imports
      (<code>* as X</code> — reach members as <code>X.Gap</code> in C# and{' '}
      <code>&lt;X.Comp /&gt;</code> in markup), default imports bound to a file&rsquo;s{' '}
      <code>export default</code>, and deferred <code>export {'{ … }'}</code> lists.
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_FULL_SURFACE} />

    <Typography variant="h5" component="h2" gutterBottom>
      Specifiers
    </Typography>
    <Typography variant="body1" paragraph>
      Specifiers are relative (<code>./</code>, <code>../</code>) or the root alias{' '}
      <code>~/</code>, which resolves against the UI source root (<code>Assets/</code>). They
      are extensionless; <code>.uitkx</code> is implied.
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_SPECIFIERS} />

    <Typography variant="h5" component="h2" gutterBottom>
      Namespace imports
    </Typography>
    <Typography variant="body1" paragraph>
      There are two preamble shapes, and the distinction is the point.{' '}
      <code>import {'{ X }'} from &quot;./file&quot;</code> (braces + <code>from</code>) pulls a
      name from a peer <code>.uitkx</code> file and is name-checked. A quoted{' '}
      <code>import &quot;@Namespace&quot;</code> brings a <em>C# namespace</em> into scope — it is
      exactly equivalent to <code>@using Namespace</code> and emits the same <code>using</code>.
      The leading <code>@</code> inside the string is what tells the two apart.
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_NAMESPACE_IMPORT} />
    <Typography variant="body1" paragraph>
      <code>@using</code> keeps working indefinitely and is never flagged; the unified{' '}
      <code>import &quot;@Ns&quot;</code> spelling is the recommended form for new code. The
      formatter round-trips whichever you write (it never rewrites one to the other); the codemod{' '}
      <code>--tidy</code> converts <code>@using</code>→<code>import &quot;@Ns&quot;</code> and drops
      redundant baseline usings in bulk. Most files need <strong>no</strong> namespace import at all
      — write one only when the editor red-squiggles a C# name that isn&rsquo;t from another{' '}
      <code>.uitkx</code> file (that squiggle is <code>UITKX2316</code>).
    </Typography>

    <Typography variant="h5" component="h2" gutterBottom>
      Mixed declarations
    </Typography>
    <Typography variant="body1" paragraph>
      A file can declare any mix of components, hooks, and values, in any order. The old
      &ldquo;one component per file&rdquo; and companion-file naming rules are no longer enforced
      by the compiler — they are documentation conventions (see below).
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_MIXED} />

    <Typography variant="h5" component="h2" gutterBottom>
      Conventions &amp; best practices
    </Typography>
    <Typography variant="body1" paragraph>
      Because cross-file references are now explicit, the file-layout rules that used to be
      enforced are <strong>recommendations</strong>, not diagnostics. Nothing below produces a
      warning — they just keep a project easy to navigate:
    </Typography>
    <List>
      <ListItem>
        <ListItemText
          primary="One component per file (with its companions)"
          secondary="A component plus its .hooks/.style/etc. companions per folder stays readable and maps cleanly to HMR. Group several small, tightly-related components in one file when it genuinely reads better."
        />
      </ListItem>
      <ListItem>
        <ListItemText
          primary="Hooks in a .hooks file, values in a .style/.utils file"
          secondary="Keeping reusable hooks and values in their own companion files signals intent and keeps component files focused. Inlining a small local hook/value in the component file is fine."
        />
      </ListItem>
      <ListItem>
        <ListItemText
          primary="Match the filename to the primary component"
          secondary="Naming Foo.uitkx after its main component Foo makes it findable. With multiple components in a file this is naturally looser."
        />
      </ListItem>
    </List>

    <Typography variant="h5" component="h2" gutterBottom>
      Strict resolution
    </Typography>
    <Typography variant="body1" paragraph>
      Referencing a peer-exported name without importing it is an error, and the diagnostic
      names the exact import line to add. Built-in elements (<code>&lt;Box&gt;</code>,{' '}
      <code>&lt;Label&gt;</code>…) and built-in hooks (<code>useState</code>,{' '}
      <code>useEffect</code>…) never need importing, and hand-written C# is ambient (never
      flagged).
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_STRICT} />

    <Typography variant="h6" component="h3" gutterBottom>
      Diagnostics (UITKX2300–2317)
    </Typography>
    <TableContainer component={Paper} sx={{ my: 2 }}>
      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell><strong>Code</strong></TableCell>
            <TableCell><strong>Meaning</strong></TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {DIAGS.map(([code, meaning]) => (
            <TableRow key={code}>
              <TableCell><code>{code}</code></TableCell>
              <TableCell>{meaning}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>

    <Typography variant="h5" component="h2" gutterBottom>
      Namespaces &amp; privacy
    </Typography>
    <Typography variant="body1" paragraph>
      A file&rsquo;s default namespace is derived from its path relative to the owning{' '}
      <code>.asmdef</code>. <code>@namespace</code> is now an optional interop override.
      Privacy is a compile-time fence: non-exported declarations are <code>internal</code> and
      invisible to strict resolution.
    </Typography>
    <CodeBlock language="jsx" code={EXAMPLE_NAMESPACE} />

    <Typography variant="h5" component="h2" gutterBottom>
      Migrating an existing project
    </Typography>
    <Typography variant="body1" paragraph>
      The new grammar is additive — an un-migrated project keeps compiling. When you&rsquo;re
      ready, the bundled <code>UitkxMigrateImports</code> codemod rewrites every{' '}
      <code>.uitkx</code> under a directory in place. It runs per owning <code>.asmdef</code>{' '}
      (imports are asmdef-scoped) and does three things:
    </Typography>
    <List>
      <ListItem disablePadding>
        <ListItemText
          primary={
            <>
              <strong>Exports everything</strong> — prefixes every component/hook/module
              declaration with <code>export</code>, so nothing that used to be cross-file-visible
              becomes private. (Tighten to real privacy afterwards by removing the{' '}
              <code>export</code>s you don&rsquo;t need.)
            </>
          }
        />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText
          primary={
            <>
              <strong>Inserts imports</strong> — scans each file&rsquo;s markup/setup for
              references to names exported elsewhere in the asmdef and adds the matching{' '}
              <code>import {'{ … }'}</code> lines (nearest-folder wins when a name is declared in
              several files).
            </>
          }
        />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText
          primary={
            <>
              <strong>Stamps <code>@namespace</code></strong> — writes each file&rsquo;s{' '}
              <em>current</em> namespace explicitly, so its identity is frozen and unaffected by
              the switch to path-derived namespaces. (This is why migrated files with a companion{' '}
              <code>.cs</code> keep merging — the stamped <code>@namespace</code> still matches.)
            </>
          }
        />
      </ListItem>
    </List>
    <Typography variant="body1" paragraph>
      It is idempotent and formatter-stable — re-running makes no further changes. Run{' '}
      <code>--check</code> first for a dry run (it lists what would change and exits non-zero if
      anything would). A reference whose name is declared in two <em>equally-near</em> files is
      genuinely ambiguous: the codemod skips it and prints a warning so you can add that one import
      by hand.
    </Typography>
    <CodeBlock language="bash" code={EXAMPLE_CODEMOD} />

    <Typography variant="h5" component="h2" gutterBottom>
      Editor support
    </Typography>
    <Typography variant="body1" paragraph>
      The language server understands the import graph, so the VS Code, Visual Studio, and Rider
      extensions provide:
    </Typography>
    <List>
      <ListItem disablePadding>
        <ListItemText primary="Go-to-definition on an import — jump from a specifier to the target file, and from an imported name to its declaration." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary="Completion inside import { … } — suggests the target file's exported names (also in the combined form: import Def, { … } from, where the default binding and the default export are excluded); completion inside the specifier string suggests peer file paths." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary="Kind-accurate coloring of imported names — components color like elements, hooks and utils like functions, values like variables, so an import line reads the same as the code that uses it." />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText
          primary={
            <>
              A <strong>quick-fix for UITKX2305</strong> — one click adds the missing{' '}
              <code>import</code> line for a peer name you referenced but didn&rsquo;t import.
            </>
          }
        />
      </ListItem>
      <ListItem disablePadding>
        <ListItemText primary="Semantic highlighting for the import/export/from keywords and specifier strings, plus the live strict diagnostics (2300–2314) as you type." />
      </ListItem>
    </List>
  </Box>
)
