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
import workspaceShot from '../../assets/builder/workspace.png'
import emptyStateShot from '../../assets/builder/empty-state.png'
import Styles from './UiBuilderPage.style'

const Section: FC<{ title: string; children: React.ReactNode }> = ({ title, children }) => (
  <Box>
    <Typography variant="h5" component="h2" gutterBottom>
      {title}
    </Typography>
    {children}
  </Box>
)

export const UiBuilderOverviewPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      RUITK UI Builder
    </Typography>
    <Typography variant="body1" paragraph>
      The UI Builder is an in-Unity visual editor for <code>.uitkx</code> component trees. It is a
      near-zero-code way to build a real React-style UI: you lay out components on a canvas, pick
      elements from a palette, set typed style keys from a list, and watch the thing render live
      while you work. Everything it produces is ordinary <code>.uitkx</code> source that you own,
      read, and can hand-edit at any moment.
    </Typography>

    <Shot
      src={workspaceShot}
      alt="The RUITK UI Builder with the folder tree, library, canvas, live preview and source pane"
      caption="The whole workspace: folders and library on the left, the canvas in the middle, live preview and source on the right."
    />

    <Section title="Near-zero code, not no-code">
      <Typography variant="body1" paragraph>
        The distinction matters, because it is what you get in exchange for learning the tool. A
        no-code builder owns your UI in a format only it understands. This one does not have a
        format of its own at all:
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <strong>The file is the truth.</strong> Every card on the canvas IS a{' '}
                <code>.uitkx</code> file on disk. There is no project database, no scene-embedded
                blob, and nothing to export. The source pane shows you the exact text at all times.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <strong>It round-trips.</strong> Anything you build visually can be edited in your
                IDE, and anything you write by hand shows up as cards. The builder never rewrites a
                file into its own dialect, and it never claims ownership of one.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <strong>You still write logic.</strong> Markup, structure, imports, styling and
                wiring are point-and-click. Hook bodies, event handlers and real behaviour are code
                &mdash; typed into the same cards, with the same completion.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <strong>Nothing is written until you Save.</strong> The whole session is in memory.
                You can restructure a tree, rename half of it, and change your mind with{' '}
                <strong>Abort</strong>, and the project on disk never knew.
              </>
            }
          />
        </ListItem>
      </List>
    </Section>

    <Section title="Opening it">
      <TableContainer component={Paper} sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Route</TableCell>
              <TableCell>What happens</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>
                <strong>Reactive UI Toolkit / UI Builder</strong>
              </TableCell>
              <TableCell>
                Opens the builder empty. From there you create your first component and pick a
                folder for it when you first Save.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                Right-click a <code>.uitkx</code> asset &rarr;{' '}
                <strong>Open in RUITK UI Builder</strong>
              </TableCell>
              <TableCell>
                Resolves the file&apos;s tree root and opens the whole connected tree &mdash; not
                just the file you clicked. Any member of a tree opens the same canvas.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                Right-click a <code>.uxml</code> asset &rarr; <strong>Convert UXML to UITKX</strong>
              </TableCell>
              <TableCell>
                A one-way import that maps inline USS onto typed <code>Style</code> and reports
                every construct it had to drop.
              </TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
      <Shot
        src={emptyStateShot}
        alt="The builder's empty state offering New component, style, hook and util module"
        caption="Opened from the menu with nothing selected, the builder offers the four ways to start. Nothing is written to disk until you Save."
      />
      <Typography variant="body2" paragraph>
        Double-clicking a <code>.uitkx</code> asset keeps opening your external editor. The builder
        never takes that route over.
      </Typography>
    </Section>

    <Section title="What is on this page set">
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <strong>The Workspace</strong> &mdash; every pane, button and badge, and what each
                one is for.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <strong>Folders &amp; Naming</strong> &mdash; the folder structure the builder
                creates, where a new module is born, and the family naming convention.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <strong>Editing</strong> &mdash; the three zoom layers, the context menus, dragging,
                and setting typed style keys.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <strong>Saving &amp; History</strong> &mdash; what Save actually does, Abort, undo,
                deletion, and where your canvas layout is kept.
              </>
            }
          />
        </ListItem>
      </List>
    </Section>

    <Box sx={Styles.callout}>
      <Typography variant="body2">
        <strong>Pair it with HMR Mode.</strong> With Hot Module Replacement active, a Save does not
        even trigger a domain reload &mdash; the saved files are hot-swapped and anything already
        running in Play Mode keeps its state. The builder and HMR are designed to be used together.
      </Typography>
    </Box>
  </Box>
)
