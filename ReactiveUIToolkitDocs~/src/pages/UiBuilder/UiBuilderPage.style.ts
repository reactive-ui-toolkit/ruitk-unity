import type { SxProps, Theme } from '@mui/material'

const root: SxProps<Theme> = {
  display: 'flex',
  flexDirection: 'column',
  gap: 2,
}

const list: SxProps<Theme> = {
  pl: 2,
}

const table: SxProps<Theme> = {
  my: 1,
}

const figure: SxProps<Theme> = {
  my: 2,
  display: 'flex',
  flexDirection: 'column',
  gap: 0.75,
}

const shot: SxProps<Theme> = {
  // maxWidth, never width: these are 1x screenshots, so stretching a tight crop
  // to the column width just blurs it. Large shots still shrink to fit.
  maxWidth: '100%',
  height: 'auto',
  display: 'block',
  borderRadius: 1,
  border: '1px solid',
  borderColor: 'divider',
}

const caption: SxProps<Theme> = {
  color: 'text.secondary',
  fontStyle: 'italic',
}

const callout: SxProps<Theme> = {
  my: 1,
  p: 2,
  borderRadius: 1,
  borderLeft: '3px solid',
  borderLeftColor: 'primary.main',
  backgroundColor: 'action.hover',
}

const Styles = { root, list, table, figure, shot, caption, callout }

export default Styles
