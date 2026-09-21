import type { FC } from 'react'
import { Alert, Box, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Typography } from '@mui/material'
import { CodeBlock } from '../../components/CodeBlock/CodeBlock'
import Styles from './MigrationPage.style'

const THE_ERROR = `Assets/Game/UI/Inventory.cs(12,9): error CS0246:
  The type or namespace name 'Signal<>' could not be found
  (are you missing a using directive or an assembly reference?)`

const THE_FIX = `// Game.UI.asmdef  —  add one entry to "references"
{
  "name": "Game.UI",
  "references": [
    "Ruitk.Signals",     // <- add this
    "Ruitk.Shared",
    "Ruitk.Runtime"
  ]
}`

const CODEMOD = `# The tool ships in the REPOSITORY, not in the package (Unity never imports a
# ~ folder), so run it from a clone of ruitk-unity and point it at YOUR project.

# Look first — writes nothing, lists what it would change:
dotnet run --project SourceGenerator~/Tools/RuitkMigrateSignalsAsmdef -- <path-to-your-unity-project> --dry-run

# Apply:
dotnet run --project SourceGenerator~/Tools/RuitkMigrateSignalsAsmdef -- <path-to-your-unity-project>`

const THIN_VIEW = `// Game.Presentation.asmdef — references Ruitk.Signals and NOTHING else of ours.
// It can publish state. It cannot name VirtualNode, V.*, Style or any hook:
// that is a compile error, not a code-review note.
{
  "name": "Game.Presentation",
  "references": [ "Ruitk.Signals" ]
}

// Game.UI.asmdef — the renderer half.
{
  "name": "Game.UI",
  "references": [ "Ruitk.Signals", "Ruitk.Shared", "Ruitk.Runtime" ]
}`

export const Migration021Page: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Migrating to 0.21
    </Typography>

    <Typography variant="body1" paragraph>
      <code>{'Signal<T>'}</code>, <code>SignalFactory</code> and <code>SignalsRuntime</code> moved
      out of <code>Ruitk.Shared</code> into their own assembly, <code>Ruitk.Signals</code>. Unity
      assembly references are not transitive, so an assembly definition that uses signals now needs
      to say so.
    </Typography>

    <Alert severity="info" sx={{ mb: 2 }}>
      <strong>Most projects need to do nothing.</strong> <code>Ruitk.Signals</code> is{' '}
      <code>autoReferenced</code>, so any code that is <em>not</em> inside an assembly definition —
      which is most game code — already sees it. Only projects that define their own{' '}
      <code>.asmdef</code> files are affected, and among those, only the ones that actually use
      signals.
    </Alert>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        What you would see
      </Typography>
      <CodeBlock language="text" code={THE_ERROR} />
      <Typography variant="body1" paragraph>
        Or the same error pointing at a generated <code>.uitkx</code> file you never wrote — that
        happens when a <code>.uitkx</code> calls <code>useSignal</code>, because the generated
        wrapper for it takes a <code>{'Signal<T>'}</code>.
      </Typography>
    </Box>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        The fix
      </Typography>
      <CodeBlock language="json" code={THE_FIX} />
      <Typography variant="body1" paragraph>
        Three ways to apply it, in increasing order of effort:
      </Typography>
      <TableContainer>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Route</TableCell>
              <TableCell>Use when</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell><strong>The editor offers it</strong></TableCell>
              <TableCell>
                A check runs once per domain reload and names every assembly definition that needs
                the reference. <code>Assets &gt; Reactive UI Toolkit &gt; Fix Signals Assembly
                References</code> applies it. Easiest, and it works even while your own code will
                not compile — the check lives in our assembly, not yours.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell><strong>The codemod</strong></TableCell>
              <TableCell>
                A whole project at once, or a CI step. Idempotent — run it twice and the second run
                reports nothing to do.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell><strong>By hand</strong></TableCell>
              <TableCell>One or two assembly definitions; it is a single line each.</TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
      <CodeBlock language="bash" code={CODEMOD} />
      <Typography variant="body1" paragraph>
        The codemod adds the reference to the assembly definitions that need it and no others: the
        ones that reference <code>Ruitk.Shared</code> (or Runtime / Ugui / Editor) <em>and</em>{' '}
        whose own sources name a signal type, or whose <code>.uitkx</code> files call{' '}
        <code>useSignal</code>. It never edits the package&apos;s own assembly definitions, and it
        preserves your formatting rather than reserialising the JSON.
      </Typography>
      <Typography variant="body1" paragraph>
        One case it cannot decide for you: an assembly definition may reference others by GUID
        instead of by name, and a GUID cannot be resolved without Unity. Those are listed as{' '}
        <code>CHECK</code> and left alone — the in-editor fix handles them, because Unity is right
        there to resolve them.
      </Typography>
    </Box>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Why the split is worth the one-line edit
      </Typography>
      <Typography variant="body1" paragraph>
        Before, reaching for a signal handed you the whole toolkit — the virtual DOM, every hook,
        the reconciler. There was no way to build an assembly that owns state and cannot touch the
        renderer, because the type you needed lived in the same place as the renderer.
      </Typography>
      <CodeBlock language="json" code={THIN_VIEW} />
      <Typography variant="body1" paragraph>
        The presentation assembly computes what is true; the UI assembly decides how it looks; the
        signal is the only wire between them. When someone later reaches for a{' '}
        <code>VisualElement</code> from inside the game logic, the build breaks — which is the
        point. It also makes that logic testable without Unity&apos;s UI stack, and reusable behind
        a different backend or none at all.
      </Typography>
    </Box>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Why it could not be made transparent
      </Typography>
      <Typography variant="body1" paragraph>
        In ordinary .NET a moved type can leave a forwarder behind, so callers never notice. That
        does not help here: a forwarder fixes binding at <em>runtime</em>, while this break is at{' '}
        <em>compile</em> time, and Unity passes the compiler exactly the references an assembly
        definition declares. <code>autoReferenced</code> covers the predefined assemblies and
        nothing else. So the edit is unavoidable — which is why the tooling exists instead.
      </Typography>
    </Box>
  </Box>
)
