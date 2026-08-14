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
import Styles from './UiBuilderPage.style'

const Section: FC<{ title: string; children: React.ReactNode }> = ({ title, children }) => (
  <Box>
    <Typography variant="h5" component="h2" gutterBottom>
      {title}
    </Typography>
    {children}
  </Box>
)

export const UiBuilderPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      RUITK UI Builder
    </Typography>
    <Typography variant="body1" paragraph>
      The UI Builder is an in-Unity visual editor for <code>.uitkx</code> component trees: a
      pannable canvas of every file in the tree, a live preview rendered through the real
      reconciler, and a colored code buffer — all editing in memory until you Save.
    </Typography>

    <Section title="Opening a tree">
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                Right-click any <code>.uitkx</code> asset → <strong>Open in RUITK UI Builder</strong>.
                The builder resolves the file&apos;s tree root by walking its importers and opens the
                whole connected tree.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                Or right-click a <code>.uxml</code> asset → <strong>Convert UXML to UITKX</strong> —
                a one-way import that maps inline USS to typed <code>Style</code> and reports
                anything it had to drop.
              </>
            }
          />
        </ListItem>
      </List>
    </Section>

    <Section title="The panes">
      <TableContainer component={Paper} sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Pane</TableCell>
              <TableCell>What it does</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>Canvas</TableCell>
              <TableCell>
                One card per file (component / hooks / style / utils, kind-colored), import edges
                drawn between them, pan with drag, zoom to the cursor with the wheel. Card
                positions and the camera persist per tree under the project&apos;s{' '}
                <code>UserSettings/</code>. Double-click a card to focus its file.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Preview</TableCell>
              <TableCell>
                The focused component mounted through the real fiber reconciler on its own
                frame-budgeted scheduler. Primitive props appear as knobs. Ctrl+Click any element
                to jump to the component that rendered it.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Code</TableCell>
              <TableCell>
                The file&apos;s buffer with semantic coloring and live parse/analyzer diagnostics.
                Edits recompile the preview via the hot-swap pipeline after a short debounce —
                disk stays untouched until Save.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Library</TableCell>
              <TableCell>
                Searchable palette of every element and ambient hook, fed by the bundled language
                server. Click to insert at the code caret.
              </TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
    </Section>

    <Section title="Save and abort">
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <strong>Save</strong> (or Ctrl+S) writes every dirty buffer in one batch — one
                script reload for the whole batch instead of one per file. With HMR Mode active
                there is no reload at all: the watcher hot-swaps the saved files.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <strong>Abort</strong> discards every unsaved buffer. Undo/redo (Ctrl+Z / Ctrl+Y)
                is per-file and session-scoped — it never touches Unity&apos;s global undo.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Files that live in immutable packages open read-only: they render on the canvas and in the preview but cannot be edited or saved." />
        </ListItem>
      </List>
    </Section>

    <Section title="Requirements and notes">
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                The language intelligence runs on the bundled LSP server (a .NET 8 process). The
                runtime is resolved from <code>RUITK_DOTNET</code>, then{' '}
                <code>.ruitk-local.json</code>, then Unity&apos;s bundled runtime (Unity 6000.4+),
                then the system install.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <code>@uss</code> and <code>Asset&lt;T&gt;</code> references added since the last
                Save resolve only after saving — the asset cache is disk-gated. This is the one
                known preview limitation.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Double-clicking a .uitkx asset keeps opening your external editor — the builder never takes over that route." />
        </ListItem>
      </List>
    </Section>
  </Box>
)
