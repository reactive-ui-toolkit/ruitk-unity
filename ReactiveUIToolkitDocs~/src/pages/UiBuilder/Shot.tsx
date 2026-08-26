import type { FC } from 'react'
import { Box, Typography } from '@mui/material'
import Styles from './UiBuilderPage.style'

type Props = {
  src: string
  alt: string
  caption: string
}

export const Shot: FC<Props> = ({ src, alt, caption }) => (
  <Box component="figure" sx={Styles.figure}>
    <Box component="img" src={src} alt={alt} sx={Styles.shot} loading="lazy" />
    <Typography component="figcaption" variant="caption" sx={Styles.caption}>
      {caption}
    </Typography>
  </Box>
)
