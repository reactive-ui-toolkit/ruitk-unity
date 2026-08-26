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

export const UiBuilderEditingPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Editing
    </Typography>
    <Typography variant="body1" paragraph>
      Building a UI in the builder is four gestures: zoom to the altitude you need, drag things in
      from the library, right-click for anything typed, and click a value to change it.
    </Typography>

    <Section title="Zoom layers">
      <Typography variant="body1" paragraph>
        The canvas has three levels of detail. The wheel zooms toward the cursor and the layer
        changes with it; the toolbar dropdown jumps straight to one. They exist because the same
        canvas has to answer three different questions.
      </Typography>
      <TableContainer component={Paper} sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Layer</TableCell>
              <TableCell>Question it answers</TableCell>
              <TableCell>What cards show</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>
                <strong>1 &mdash; Architecture</strong>
              </TableCell>
              <TableCell>How does this UI fit together?</TableCell>
              <TableCell>
                Name and kind only, with the edges between them. Reading the shape of a large tree.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                <strong>2 &mdash; Cards</strong>
              </TableCell>
              <TableCell>What is in each file?</TableCell>
              <TableCell>
                Full card contents at a glance &mdash; imports, hooks, markup &mdash; small but
                legible. Good for moving cards around and seeing several at once.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                <strong>3 &mdash; Edit</strong>
              </TableCell>
              <TableCell>What am I changing?</TableCell>
              <TableCell>
                Everything at working size, and the layer where rows, attributes and style entries
                are clickable.
              </TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>

      <Shot
        src="/builder/layer-cards.png"
        alt="The canvas at Layer 2, showing five compact cards and their import edges"
        caption="Layer 2 — Cards. The whole tree at once, still readable, with the usage edges visible."
      />

      <Typography variant="body2" paragraph>
        Over a section that scrolls on its own, hold <strong>Ctrl</strong> while using the wheel to
        zoom the canvas instead of scrolling that section.
      </Typography>
    </Section>

    <Section title="Dragging from the library">
      <Typography variant="body1" paragraph>
        This is how markup gets built. Drag an item from the library onto a card and the drop target
        decides what happens:
      </Typography>
      <TableContainer component={Paper} sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Drop on</TableCell>
              <TableCell>Result</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>The top edge of a row</TableCell>
              <TableCell>Inserted before that element</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>The bottom edge of a row</TableCell>
              <TableCell>Inserted after that element</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>The middle of a row</TableCell>
              <TableCell>Nested inside that element as a child</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                The <strong>BODY</strong> section
              </TableCell>
              <TableCell>Added as a hook call</TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
      <Typography variant="body2" paragraph>
        Rows can also be dragged among themselves to reorder markup, and a blocked drop says why
        rather than silently doing nothing.
      </Typography>
    </Section>

    <Section title="Context menus">
      <Typography variant="body1" paragraph>
        Right-click is where the typed operations live &mdash; the ones with a fixed set of correct
        answers. Rows, cards, imports and the empty canvas each have their own menu, and menus with
        more than a handful of entries group them into submenus.
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="On a markup row: typed attributes for that element, directives, delete." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="On a card: create a module under or beside it, rename it, delete it." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="On an import row: copy the alias, remove the import, jump to the target." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="On empty canvas: create a module at the tree root." />
        </ListItem>
      </List>
      <Typography variant="body2" paragraph>
        The menus are keyboard-drivable: up/down to move, right or Enter to open a submenu, left or
        Escape to back out one level, Escape again to close. Menus with a long list &mdash; style
        keys, elements &mdash; open with a search field instead.
      </Typography>
    </Section>

    <Section title="Setting typed style keys">
      <Typography variant="body1" paragraph>
        A style module card lists its exported styles and, under each, the keys it sets. Adding a
        key opens a searchable list of every UI Toolkit style property with its type &mdash;{' '}
        <code>FlexGrow : number</code>, <code>Margin : length</code>, <code>Color : color</code>{' '}
        &mdash; so you are picking from what exists rather than remembering a name.
      </Typography>

      <Shot
        src="/builder/style-keys.png"
        alt="The style key picker open over a style module card, with the History panel visible"
        caption="Adding a style key. Every entry carries its type, and the list is searchable."
      />

      <Typography variant="body1" paragraph>
        The values are compile-time checked, because <code>Style</code> is a typed set rather than a
        string map. A key you set here is the same key you would write by hand, and helpers like{' '}
        <code>Pct()</code>, <code>Px()</code>, <code>FlexRow</code> and <code>Rgba()</code> are
        available on both routes.
      </Typography>
      <Typography variant="body2" paragraph>
        At Layer 3, clicking an attribute, a badge or a style entry edits it in place.
      </Typography>
    </Section>

    <Section title="Keyboard">
      <TableContainer component={Paper} sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Keys</TableCell>
              <TableCell>Action</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>
                <code>Ctrl+S</code>
              </TableCell>
              <TableCell>Save every dirty buffer</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                <code>Ctrl+Z</code> / <code>Ctrl+Shift+Z</code> / <code>Ctrl+Y</code>
              </TableCell>
              <TableCell>Undo / redo, session-scoped &mdash; never Unity&apos;s global undo</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                <code>Delete</code>
              </TableCell>
              <TableCell>Delete the selected row or card</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                <code>Escape</code>
              </TableCell>
              <TableCell>Cancel the active inline edit, or close the open menu</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                <code>Enter</code>
              </TableCell>
              <TableCell>Commit an inline edit, or activate the highlighted menu row</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                <code>Ctrl+Click</code> (preview)
              </TableCell>
              <TableCell>Jump to the component that rendered that element</TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
    </Section>
  </Box>
)
