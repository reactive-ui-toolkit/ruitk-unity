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
import saveFolderShot from '../../assets/builder/save-folder-prompt.png'
import appliesOnSaveShot from '../../assets/builder/toast-applies-on-save.png'
import Styles from './UiBuilderPage.style'

const Section: FC<{ title: string; children: React.ReactNode }> = ({ title, children }) => (
  <Box>
    <Typography variant="h5" component="h2" gutterBottom>
      {title}
    </Typography>
    {children}
  </Box>
)

export const UiBuilderSavingPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Saving &amp; History
    </Typography>
    <Typography variant="body1" paragraph>
      The builder edits in memory. Nothing you do reaches disk until you press{' '}
      <strong>Save</strong>, and that is what makes it safe to restructure aggressively &mdash; you
      can rename half a tree, move folders around, delete files, and still walk away with{' '}
      <strong>Abort</strong>.
    </Typography>

    <Section title="What Save does">
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="Formats every dirty buffer, then writes them all in one batch — one script reload for the whole batch instead of one per file." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Performs the moves you planned: renames, folder re-filings, and the import-specifier rewrites that keep them consistent." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                Asks first about anything irreversible. Deleting a file is reversible right up until
                you Save, so Save is where it asks &mdash; it names every file, and they go to the
                trash rather than being erased.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                Refuses to write an empty module. An empty <code>.uitkx</code> is not an empty file,
                it is a broken one &mdash; the language requires a top-level declaration &mdash; so
                clearing a module while you work is fine, but writing it is where the builder stops
                and asks.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="With HMR Mode active, there is no domain reload at all: the saved files are hot-swapped in place." />
        </ListItem>
      </List>
      <Shot
        src={appliesOnSaveShot}
        alt="A toast reading: Created SomethingNew.uitkx in SomethingNew - applies on Save"
        caption="Every structural action says the same thing: it is real in the session, and it reaches disk on Save."
      />
    </Section>

    <Section title="Saving a brand-new tree">
      <Typography variant="body1" paragraph>
        A tree started from the empty state has no folder yet, so the first Save asks for one, once,
        and moves the whole pending tree there before writing. Until then the files live at a
        provisional location that Unity deliberately cannot see, so a half-finished tree can never
        be picked up by a compile.
      </Typography>
      <Shot
        src={saveFolderShot}
        alt="A folder picker titled Where should this UI live, browsing the project Assets folder"
        caption="The first Save asks once, and only accepts a folder inside the project — a .uitkx outside Assets is never compiled."
      />
      <Typography variant="body2" paragraph>
        The move is planned in full before anything happens, so a name collision cancels the whole
        relocation instead of leaving half the tree in the new folder.
      </Typography>
    </Section>

    <Section title="Abort">
      <Typography variant="body1" paragraph>
        Discards every unsaved buffer and puts paths back as well as text: a renamed module returns
        to its old name, and a module that rode along inside a renamed folder returns with it. The
        canvas rebuilds and your layout follows the files home.
      </Typography>
    </Section>

    <Section title="History and undo">
      <Typography variant="body1" paragraph>
        Every action this session is recorded &mdash; edits, creates, deletes, moves, formatting
        &mdash; and the <strong>History</strong> panel lists them newest first. Click any row to
        jump the tree to that point.
      </Typography>
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
                <code>Ctrl+Z</code>
              </TableCell>
              <TableCell>Undo</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                <code>Ctrl+Shift+Z</code> or <code>Ctrl+Y</code>
              </TableCell>
              <TableCell>Redo</TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
      <Typography variant="body2" paragraph>
        Undo is session-scoped and never touches Unity&apos;s global undo stack. One entry covers a
        whole operation, so undoing a delete restores the module and every reference to it together.
      </Typography>
    </Section>

    <Section title="Crash cover">
      <Typography variant="body1" paragraph>
        The tree is journalled while you work, and dumped in full before a domain reload takes the
        in-memory copy away. If the builder ever comes back up empty next to a journal, it offers
        the unsaved work back &mdash; a session that ended cleanly leaves no journal to offer.
      </Typography>
    </Section>

    <Section title="Where your canvas layout lives">
      <Typography variant="body1" paragraph>
        Card positions and the camera are per-user preferences, not project content, so they are
        kept outside your assets in the project&apos;s <code>UserSettings/</code> folder &mdash; one
        file per tree, which survives deleting <code>Library/</code> and is conventionally
        gitignored.
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary="Positions are written as soon as you drag a card, not on Save — a layout is not project content, so it is not part of the save contract." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="A slot is decided once and then remembered, so adding a module never reshuffles the cards you have already placed." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Renaming, moving or re-filing carries the layout with the files." />
        </ListItem>
      </List>
    </Section>

    <Section title="Known limitations">
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <code>@uss</code> and <code>Asset&lt;T&gt;</code> references added since the last
                Save resolve only after saving &mdash; the asset cache is disk-gated. This is the
                one known preview limitation.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="Files in immutable packages are read-only: they render, but cannot be edited or saved." />
        </ListItem>
      </List>
    </Section>
  </Box>
)
