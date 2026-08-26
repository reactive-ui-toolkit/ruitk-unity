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
import { Shot } from './Shot'
import Styles from './UiBuilderPage.style'

const Section: FC<{ title: string; children: React.ReactNode }> = ({ title, children }) => (
  <Box>
    <Typography variant="h5" component="h2" gutterBottom>
      {title}
    </Typography>
    {children}
  </Box>
)

export const UiBuilderWorkspacePage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      The Workspace
    </Typography>
    <Typography variant="body1" paragraph>
      The window is five regions: a toolbar across the top, the folder tree and library down the
      left, the canvas in the middle, and the live preview stacked over the source pane on the
      right. The splitters between canvas, preview and source are draggable.
    </Typography>

    <Shot
      src="/builder/workspace.png"
      alt="The builder workspace with all five regions visible"
      caption="All five regions at Layer 3. The header reads the focused file and how many files are open and dirty."
    />

    <Section title="Toolbar">
      <TableContainer component={Paper} sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Control</TableCell>
              <TableCell>What it does</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>Header text</TableCell>
              <TableCell>
                The focused file, then how many files the tree has and how many carry unsaved edits
                &mdash; e.g. <code>NewComponent.uitkx | 5 file(s), 0 dirty</code>. The dirty count is
                the honest answer to &ldquo;do I need to Save?&rdquo;.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Layer select</TableCell>
              <TableCell>
                Jumps the canvas between the three zoom layers. See{' '}
                <strong>Editing &rarr; Zoom layers</strong>.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Import .uxml&hellip;</TableCell>
              <TableCell>
                One-way UXML conversion, the same as the Assets context-menu route.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>History</TableCell>
              <TableCell>
                A panel of every action this session, newest first. Click a row to jump the tree
                back to that point. Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y drive the same stack.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Trace</TableCell>
              <TableCell>
                Turns on a running log of what the preview pipeline decided &mdash; which modules
                were considered for a rebuild, which were rebuilt, and why. Off by default; turn it
                on when the preview is not showing what you expect.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>? How to drive it</TableCell>
              <TableCell>Toggles an in-window cheat sheet of the gestures.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Save / Abort</TableCell>
              <TableCell>
                Commit or discard the entire session. See <strong>Saving &amp; History</strong>.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Legend</TableCell>
              <TableCell>
                The colour key on the right: component, hook module, style module, usage edge.
              </TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
    </Section>

    <Section title="Folders">
      <Typography variant="body1" paragraph>
        The top-left pane is the tree as it will exist on disk &mdash; folders, and the{' '}
        <code>.uitkx</code> files inside them. It is expanded by default and collapsible from the{' '}
        <strong>FOLDERS</strong> header when you want the library to have the height.
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="Click a file to focus it: the canvas selects its card, the preview switches to it, and the source pane shows its buffer." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Drag a file or a folder onto another folder to re-file it. This is the only gesture that moves anything." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                What you see here is <em>pending</em> until you Save &mdash; a move you make in this
                pane is a plan, not a filesystem operation.
              </>
            }
          />
        </ListItem>
      </List>
    </Section>

    <Section title="Library">
      <Typography variant="body1" paragraph>
        The searchable palette underneath, fed by the bundled language server so it always matches
        the elements the runtime can actually render. It has four groups:
      </Typography>
      <TableContainer component={Paper} sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Group</TableCell>
              <TableCell>Contents</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>Native elements</TableCell>
              <TableCell>
                Every UI Toolkit control the toolkit wraps &mdash;{' '}
                <code>&lt;VisualElement&gt;</code>, <code>&lt;Label&gt;</code>,{' '}
                <code>&lt;Button&gt;</code>, <code>&lt;ScrollView&gt;</code> and the rest.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Custom components</TableCell>
              <TableCell>
                The components in this tree. Dropping one in is how you compose &mdash; it inserts
                the tag and, where needed, the import.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>Hooks</TableCell>
              <TableCell>
                Ambient hooks (<code>useState</code>, <code>useEffect</code>, <code>useMemo</code>,{' '}
                <code>useRef</code>, <code>provideContext</code>&hellip;) plus any hook modules in
                the tree.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>+ new</TableCell>
              <TableCell>
                Creates a module at the tree root. See <strong>Folders &amp; Naming</strong> for
                exactly where it lands.
              </TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
      <Typography variant="body2" paragraph>
        Library items are dragged onto the canvas, not just clicked: drop onto the top of a row to
        place before it, the bottom to place after, the middle to nest inside, or onto the{' '}
        <strong>BODY</strong> section to add a hook.
      </Typography>
    </Section>

    <Section title="Canvas">
      <Typography variant="body1" paragraph>
        One card per file, with edges drawn between them. An edge is a usage; the colour of a
        card&apos;s badge is its kind. Cards are laid out by you &mdash; drag them anywhere, and the
        arrangement is remembered per tree.
      </Typography>
      <Typography variant="body1" paragraph>
        A component card has a signature line and three sections: <strong>IMPORTS</strong>,{' '}
        <strong>BODY &mdash; HOOKS &amp; STATE</strong> and{' '}
        <strong>RETURN &mdash; MARKUP</strong>. A style module card shows{' '}
        <strong>EXPORTS</strong> instead, with each style entry and its keys.
      </Typography>
    </Section>

    <Section title="Live preview">
      <Typography variant="body1" paragraph>
        The focused component, mounted through the real fiber reconciler on its own frame-budgeted
        scheduler. This is not a mock renderer: it is the same reconciler, adapters and typed style
        system that will run in your game, which is why what you see is what you ship.
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="Every edit re-renders it, after a short debounce." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Primitive props on the focused component appear as knobs you can turn to try values." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Ctrl+Click any element in the preview to jump to the component that rendered it." />
        </ListItem>
      </List>
    </Section>

    <Section title="Source">
      <Typography variant="body1" paragraph>
        The focused file&apos;s buffer, with semantic colouring and live parse and analyzer
        diagnostics. It is read-only until you press <strong>edit</strong>; applying an edit
        re-parses the file and the cards redraw from the new tree. It is the escape hatch for
        anything the visual gestures do not cover, and it is always showing you the real text.
      </Typography>
      <Typography variant="body2" paragraph>
        Files that live in immutable packages open read-only throughout: they render on the canvas
        and in the preview, but cannot be edited or saved.
      </Typography>
    </Section>
  </Box>
)
