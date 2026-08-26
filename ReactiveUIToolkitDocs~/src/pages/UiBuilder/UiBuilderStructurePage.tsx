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
import { CodeBlock } from '../../components/CodeBlock/CodeBlock'
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

const TREE = `Assets/UI/NewComponent/
  NewComponent.uitkx            <- the tree ROOT
  newComponent.style.uitkx      <- companion: styles for NewComponent
  useNewComponent.hooks.uitkx   <- companion: hooks for NewComponent
  components/
    LeftSide/
      LeftSide.uitkx            <- a child component
      leftSide.style.uitkx      <- its own companion
    MiddleSide/
      MiddleSide.uitkx
    RightSide/
      RightSide.uitkx
      components/
        Badge/
          Badge.uitkx           <- children nest the same way, at any depth`

export const UiBuilderStructurePage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Folders &amp; Naming
    </Typography>
    <Typography variant="body1" paragraph>
      The builder creates a predictable folder structure so that a tree is readable from the Project
      window alone, without opening anything. The rules are small, and worth knowing because they
      explain where a new file appears when you create one.
    </Typography>

    <Section title="The shape of a tree">
      <Typography variant="body1" paragraph>
        A component owns a folder named after it. Its children live in a{' '}
        <code>components/</code> folder inside that folder, each in a folder of their own. Its
        companions &mdash; styles and hooks &mdash; sit beside it, not below it.
      </Typography>
      <CodeBlock language="bash" code={TREE} />
      <Typography variant="body1" paragraph>
        The <strong>tree root</strong> is the outermost component that owns its folder. Opening any
        file in this layout opens the whole tree on one canvas, because the root is derived from the
        structure rather than stored anywhere.
      </Typography>
    </Section>

    <Section title="The two kinds of module">
      <TableContainer component={Paper} sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Kind</TableCell>
              <TableCell>File name</TableCell>
              <TableCell>Where it goes</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>
                <strong>Component</strong>
              </TableCell>
              <TableCell>
                <code>PascalCase.uitkx</code>
              </TableCell>
              <TableCell>
                Into a folder of its own, under its parent&apos;s <code>components/</code>.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                <strong>Style module</strong>
              </TableCell>
              <TableCell>
                <code>camelCase.style.uitkx</code>
              </TableCell>
              <TableCell>Beside the component it belongs to.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                <strong>Hook module</strong>
              </TableCell>
              <TableCell>
                <code>useSomething.hooks.uitkx</code>
              </TableCell>
              <TableCell>Beside the component it belongs to.</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>
                <strong>Util module</strong>
              </TableCell>
              <TableCell>
                <code>camelCase.uitkx</code>
              </TableCell>
              <TableCell>
                Beside, and at the tree root by default &mdash; a util is shared until proven
                otherwise.
              </TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
      <Typography variant="body2" paragraph>
        Components nest. Companions do not. That single distinction is what the placement rules
        below are made of.
      </Typography>
    </Section>

    <Section title="Families">
      <Typography variant="body1" paragraph>
        <code>NewComponent.uitkx</code>, <code>newComponent.style.uitkx</code> and{' '}
        <code>useNewComponent.hooks.uitkx</code> are one <strong>family</strong>: same name, three
        roles, one folder. The builder recognises the pattern and uses it to place new companions
        automatically &mdash; create a style called <code>leftSide</code> and it goes to{' '}
        <code>LeftSide/</code>, wherever that component happens to live.
      </Typography>
      <Typography variant="body1" paragraph>
        Because companions sit beside their component rather than in a shared folder,{' '}
        <code>Card/button.style.uitkx</code> and <code>Panel/button.style.uitkx</code> can coexist
        without colliding.
      </Typography>
    </Section>

    <Section title="Where a new module is born">
      <Typography variant="body1" paragraph>
        Placement follows from <em>where you right-click</em>, not from what is currently focused.
        That is deliberate: an earlier version placed new modules relative to the focus, and because
        creating a module also focuses it, three components created in a row nested three deep. The
        structure recorded the order of your clicks rather than anything about your UI.
      </Typography>

      <Shot
        src="/builder/context-menu.png"
        alt="The card context menu with the New submenu open, showing child and beside placement hints"
        caption="Every entry in the New submenu states its placement on the right: child or beside."
      />

      <TableContainer component={Paper} sx={Styles.table}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Where you right-click</TableCell>
              <TableCell>Component</TableCell>
              <TableCell>Style / hook / util</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            <TableRow>
              <TableCell>Empty canvas</TableCell>
              <TableCell>
                At the tree root: <code>Root/components/Name/Name.uitkx</code>
              </TableCell>
              <TableCell>
                At the tree root: <code>Root/</code> &mdash; unless the name matches a
                component&apos;s family, which sends it to that component&apos;s folder.
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>A component card</TableCell>
              <TableCell>
                A <strong>child</strong>: <code>Parent/components/Name/Name.uitkx</code>
              </TableCell>
              <TableCell>
                A <strong>sibling</strong>: <code>Parent/</code>
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell>A companion card</TableCell>
              <TableCell colSpan={2}>
                No create menu &mdash; a style module has no children.
              </TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </TableContainer>
      <Typography variant="body2" paragraph>
        The name prompt tells you the parent it is about to create under, and the new card is placed
        under its parent on the canvas so the shape you see matches the shape on disk.
      </Typography>
    </Section>

    <Section title="Create states placement. Wiring states usage.">
      <Typography variant="body1" paragraph>
        These are kept strictly apart, and it is the most important rule in the tool.
      </Typography>
      <Typography variant="body1" paragraph>
        Creating a module <strong>never</strong> adds an import. If create added the import it would
        also have to add a <em>usage</em>, because <code>UITKX2304 unused import</code> is
        error-tier &mdash; an unused import stops the whole project compiling the moment the file is
        saved. And to add a usage it would have to guess where a style applies or which element a
        hook belongs to, which is a decision only you can make.
      </Typography>
      <Typography variant="body1" paragraph>
        So the builder puts the file in the right place and stops there. You wire it up by dragging
        it in from the library, which is the gesture that states usage.
      </Typography>
    </Section>

    <Section title="What moves a module">
      <Typography variant="body1" paragraph>
        Nothing, unless a gesture says so. Files do not re-file themselves, and removing an import
        does not move anything.
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText
            primary={
              <>
                <strong>Dragging in the folder tree</strong> re-files by type: a component into{' '}
                <code>Target/components/Name/</code>, a companion into <code>Target/</code>.
              </>
            }
          />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="It rewrites the import specifiers of everything that already imports it, each from its own position, so nothing breaks." />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary="It adds and removes no imports. Dragging X onto Y does not make Y use X, and the old parent keeps its import because its markup still references X." />
        </ListItem>
      </List>
      <Typography variant="body2" paragraph>
        A rule that was considered and rejected: having a shared module climb to the closest common
        parent as more things use it. In a deep tree it moves files out from under you, so the
        builder does not do it.
      </Typography>
    </Section>
  </Box>
)
