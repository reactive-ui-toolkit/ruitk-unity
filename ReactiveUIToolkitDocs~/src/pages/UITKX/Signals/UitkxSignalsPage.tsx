import type { FC } from 'react'
import { Box, List, ListItem, ListItemText, Typography } from '@mui/material'
import { CodeBlock } from '../../../components/CodeBlock/CodeBlock'
import Styles from '../../Signals/SignalsPage.style'
import {
  UITKX_SIGNALS_COMPONENT_EXAMPLE,
  UITKX_SIGNALS_OWNED_EXAMPLE,
  UITKX_SIGNALS_RUNTIME_EXAMPLE,
} from './UitkxSignalsPage.example'

export const UitkxSignalsPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Signals
    </Typography>
    <Typography variant="body1" paragraph>
      Signals are lightweight, named reactive values that live in a process-wide registry. They
      behave like a small observable store and are ideal whenever you want a single source of truth
      for reading and updating state (for example: selection, filters, or global preferences).
    </Typography>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Concepts
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary={<>Signals live in a global registry keyed by <code>string</code>.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Call <code>{'SignalFactory.Get<T>(key, initialValue)'}</code> to create or return a <code>{'Signal<T>'}</code> instance.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Call <code>{'SignalFactory.Create<T>(initialValue, comparer)'}</code> for a signal with no key, owned by whatever holds it.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Call <code>signal.Subscribe(...)</code> to watch changes outside of components; use <code>useSignal(...)</code> inside components.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<>Use <code>{'Dispatch(prev => next)'}</code> or <code>Dispatch(value)</code> to update the value and notify listeners.</>} />
        </ListItem>
      </List>
    </Box>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Runtime access
      </Typography>
      <Typography variant="body1" paragraph>
        Outside of components, work with the signal registry directly. Call{' '}
        <code>SignalsRuntime.EnsureInitialized()</code> at startup if you use signals before any
        component mounts.
      </Typography>
      <CodeBlock language="jsx" code={UITKX_SIGNALS_RUNTIME_EXAMPLE} />
    </Box>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Using signals in components
      </Typography>
      <Typography variant="body1" paragraph>
        Use <code>useSignal(...)</code> to read a signal and re-render when it changes. You can
        also project a slice of a more complex signal value with the selector overload{' '}
        <code>{'useSignal<T, TSlice>(signal, selector, comparer)'}</code> and compare with a custom
        equality comparer for performance.
      </Typography>
      <List sx={Styles.list}>
        <ListItem disablePadding>
          <ListItemText primary={<><code>{'useSignal(Signal<T>)'}</code> — subscribe and re-render on change.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>{'useSignal<T>(key, initialValue)'}</code> — shorthand that resolves from the registry by key.</>} />
        </ListItem>
        <ListItem disablePadding>
          <ListItemText primary={<><code>{'useSignal<T, TSlice>(signal, selector, comparer)'}</code> — project a slice with custom equality.</>} />
        </ListItem>
      </List>
      <CodeBlock language="jsx" code={UITKX_SIGNALS_COMPONENT_EXAMPLE} />
    </Box>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Keyed or owner-scoped
      </Typography>
      <Typography variant="body1" paragraph>
        <code>{'SignalFactory.Get<T>(key, ...)'}</code> puts the signal in the process-wide
        registry, which is what makes it shared: everything that asks for the same key gets the
        same instance. The registry has no remove, so a keyed signal and its last value live for
        as long as the process does.
      </Typography>
      <Typography variant="body1" paragraph>
        That is the right trade for application-wide state and the wrong one for state owned by
        something short-lived — a presenter, a window, one screen. Those had to invent a unique
        key and then leak a registry entry per instance.{' '}
        <code>{'SignalFactory.Create<T>(initialValue, comparer)'}</code> is the alternative: no
        key, never registered, not reachable through <code>TryGet</code>, and collected with
        whatever holds it. It behaves identically in every other respect, <code>useSignal</code>
        {' '}included.
      </Typography>
      <CodeBlock language="csharp" code={UITKX_SIGNALS_OWNED_EXAMPLE} />
    </Box>

    <Box sx={Styles.section}>
      <Typography variant="h5" component="h2" gutterBottom>
        Thread safety
      </Typography>
      <Typography variant="body1" paragraph>
        <code>{'Signal<T>'}</code> uses lock-based synchronization internally — signals can be
        safely read, written, and subscribed to from any thread. This makes them suitable for
        background tasks or async workflows that update shared UI state.
      </Typography>
    </Box>
  </Box>
)
