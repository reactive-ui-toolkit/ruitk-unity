import type { FC } from 'react'
import { Alert, Box, Link as MuiLink, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Typography } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import { CodeBlock } from '../../components/CodeBlock/CodeBlock'
import Styles from './MigrationPage.style'

const BEFORE_AFTER = `// BEFORE (legacy wrappers — UITKX2320 error since 0.16.0)
component ScoreRow {
    return ( <Label text="hi" /> );
}
hook useCounter(int initial) -> (int, Action) {
    var (count, setCount) = useState(initial);
    return (count, () => setCount(count + 1));
}
module Palette {
    public static readonly Style Bar = new Style { };
}

// AFTER (plain typed declarations)
export VirtualNode ScoreRow() {
    return ( <Label text="hi" /> );
}
export (int, Action) useCounter(int initial) {
    var (count, setCount) = useState(initial);
    return (count, () => setCount(count + 1));
}
export Style Bar = new Style { };
// consumers of Palette.Bar switch to:  import * as Palette from "./Palette"`

const CODEMOD_COMMANDS = `# The tool ships in the REPOSITORY, not in the package (Unity never imports
# "~" folders), so clone the repo and run it against YOUR project:
git clone https://github.com/reactive-ui-toolkit/ruitk-unity.git
cd ruitk-unity

# rewrite (idempotent; companion sets migrate atomically)
node scripts/migrate-uitkx.mjs /path/to/YourProject/Assets --es-modules

# dry run / CI gate — exits non-zero if anything would still change
node scripts/migrate-uitkx.mjs /path/to/YourProject/Assets --es-modules --check

# optional: write a machine-readable ledger of every rewrite + namespace move
node scripts/migrate-uitkx.mjs /path/to/YourProject/Assets --es-modules --report report.txt`

export const Migration016Page: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Migrating to 0.16 — wrapper keywords removed
    </Typography>

    <Typography variant="body1" paragraph>
      0.16.0 removes the legacy <code>component</code>/<code>hook</code>/<code>module</code>{' '}
      wrapper keywords (deprecated since 0.9.0). A file that still uses them fails to compile
      with the <code>UITKX2320</code> error, whose message names the fix: run the bundled
      migration codemod. (The far older <code>@component</code>/<code>@props</code>/
      <code>@key</code>/<code>@inject</code> directive-header form has been unsupported since
      0.5 — those files report <code>UITKX2105</code> and are not covered by the codemod;
      rewrite them as plain declarations by hand, per the table below.)
    </Typography>

    <Alert severity="info" sx={{ mb: 2 }}>
      <strong>Recommended order: codemod first, then upgrade.</strong> The codemod runs from the
      repository against your project folder, independent of which package version the project
      has installed — so migrate while still on 0.15.x, confirm the project compiles and renders,
      then bump the package to 0.16.0 with zero red console lines. (Upgrading first also works —
      every legacy file reports <code>UITKX2320</code> until you run the codemod.)
    </Alert>

    <Typography variant="h5" component="h2" sx={Styles.section}>
      What changed
    </Typography>
    <CodeBlock language="jsx" code={BEFORE_AFTER} />
    <TableContainer>
      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell>Legacy form</TableCell>
            <TableCell>Plain-declaration form</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          <TableRow>
            <TableCell><code>component Name {'{ … }'}</code></TableCell>
            <TableCell><code>export VirtualNode Name(...) {'{ … }'}</code></TableCell>
          </TableRow>
          <TableRow>
            <TableCell><code>hook useX(...) -&gt; T {'{ … }'}</code></TableCell>
            <TableCell><code>export T useX(...) {'{ … }'}</code> (tuple returns lead the declaration)</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><code>module M {'{ fields/methods }'}</code></TableCell>
            <TableCell>plain <code>export</code> values/functions at top level; consumers use <code>import * as M</code></TableCell>
          </TableRow>
          <TableRow>
            <TableCell><code>@component / @props / @key / @inject</code> (pre-0.5 header form; manual rewrite)</TableCell>
            <TableCell>typed parameters on the component declaration; <code>key=</code> at the use site</TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </TableContainer>

    <Typography variant="h5" component="h2" sx={Styles.section}>
      Run the codemod
    </Typography>
    <CodeBlock language="bash" code={CODEMOD_COMMANDS} />
    <Typography variant="body1" paragraph>
      The codemod is idempotent and formatter-stable: companion sets ({' '}
      <code>X.uitkx</code> + <code>X.*.uitkx</code>) migrate atomically, generic hooks migrate
      (they are first-class plain declarations since 0.16.0), and files whose generated
      namespace would move get an explicit <code>@namespace</code> stamp so nothing changes
      identity. It reports the one shape the plain dialect cannot express — type definitions
      (enums/classes/structs) — which belong in a hand-written <code>.cs</code> beside the
      components (see{' '}
      <MuiLink component={RouterLink} to="/companion-files">Companion Files</MuiLink>).
    </Typography>

    <Typography variant="h5" component="h2" sx={Styles.section}>
      What did NOT change
    </Typography>
    <Typography variant="body1" paragraph>
      <code>@using</code> keeps working indefinitely (the unified{' '}
      <code>import &quot;@Ns&quot;</code> spelling stays the recommended form), and{' '}
      <code>@namespace</code> remains as a rare interop escape hatch — the namespace is
      file-keyed when omitted. <code>@uss</code> and <code>@backend</code> are unchanged.
      Markup, control flow, expressions, hooks, signals, the router — all untouched: this
      release removes only the wrapper grammar around declarations.
    </Typography>

    <Typography variant="body1" paragraph>
      Full details, including the diagnostics that retired with the grammar (
      <code>UITKX2107</code>/<code>2108</code>/<code>2109</code>/<code>0211</code>), are on the{' '}
      <MuiLink component={RouterLink} to="/diagnostics">Diagnostics</MuiLink> page.
    </Typography>
  </Box>
)
