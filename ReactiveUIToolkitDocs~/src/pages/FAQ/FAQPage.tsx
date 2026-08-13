import type { FC } from 'react'
import { Link as MuiLink } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import { Box, Typography } from '@mui/material'
import Styles from './FAQPage.style'

export const FAQPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Frequently Asked Questions
    </Typography>

    {/* ── General ──────────────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      General
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      What is UITKX?
    </Typography>
    <Typography variant="body2" paragraph>
      UITKX is a markup language for authoring Unity UI Toolkit components using
      a React-like model. You write <code>.uitkx</code> files with JSX-style
      markup, hooks, and control flow. A Roslyn source generator compiles them
      into standard C# that runs on the ReactiveUIToolkit runtime.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      Which Unity versions are supported?
    </Typography>
    <Typography variant="body2" paragraph>
      Unity <strong>6.2</strong> and above. The framework relies on UI Toolkit
      APIs available from Unity 6.2 onward.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      How do I migrate a project that still uses the old wrapper keywords?
    </Typography>
    <Typography variant="body2" paragraph>
      The <code>component</code>/<code>hook</code>/<code>module</code> wrappers were{' '}
      <strong>removed in 0.16.0</strong> — un-migrated files raise the <code>UITKX2320</code>{' '}
      error. The codemod ships in the repository (not the package), so clone it and run once
      against your project:{' '}
      <code>node scripts/migrate-uitkx.mjs &lt;YourProject&gt;/Assets --es-modules</code>{' '}
      (add <code>--check</code> for a dry run). It rewrites the wrappers to plain{' '}
      <code>export</code> declarations, inserts the <code>import</code> lines each file needs,
      and stamps <code>@namespace</code> where needed so nothing changes identity. It is
      idempotent and formatter-stable. Details:{' '}
      <MuiLink component={RouterLink} to="/imports">Imports &amp; Exports → Migrating an existing project</MuiLink>.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      Does UITKX work with existing UI Toolkit code?
    </Typography>
    <Typography variant="body2" paragraph>
      Yes. UITKX components render into the same <code>VisualElement</code> tree
      as hand-written UI Toolkit code. You can mount UITKX components alongside
      existing UI Toolkit panels, mix UITKX components with native elements, and
      interop through standard <code>VisualElement</code> references.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      Does UITKX add runtime overhead?
    </Typography>
    <Typography variant="body2" paragraph>
      The reconciliation scheduler adds a small per-frame cost similar to other
      retained-mode UI frameworks. In practice, the overhead is negligible for
      typical UI workloads. All generated code is standard C# — there is no
      runtime code generation or reflection.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      Can I use UITKX in production builds?
    </Typography>
    <Typography variant="body2" paragraph>
      Yes. The source generator produces plain C# at compile time. The generated
      output is included in your build like any other script. There is no
      interpreter or runtime codegen — UITKX is fully AOT-compatible.
    </Typography>

    {/* ── IDE & Extensions ─────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      IDE & Editor Extensions
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      Which editors are supported?
    </Typography>
    <Typography variant="body2" paragraph>
      <strong>VS Code</strong> and <strong>Visual Studio 2022</strong> have full,
      officially supported extensions with syntax highlighting, completions,
      hover documentation, diagnostics, and formatting. A{' '}
      <strong>JetBrains Rider</strong> plugin is available too — it wires the
      same shared language server and TextMate grammar, so the core editing
      experience (diagnostics, completions, hover, go-to-definition) matches the
      other editors; it is lighter on IDE-specific integrations and sees less
      field testing than VS Code / VS 2022.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      Do I need the VS Code extension to use UITKX?
    </Typography>
    <Typography variant="body2" paragraph>
      No. The source generator runs inside Unity regardless of your editor. The
      extension provides the editing experience — syntax highlighting,
      completions, error squiggles, and formatting. Without it you can still
      write <code>.uitkx</code> files, but you won't have language support.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      VS Code shows wrong colours briefly when I open a file — is that a bug?
    </Typography>
    <Typography variant="body2" paragraph>
      This is expected. VS Code layers TextMate grammar colours first, then
      overrides them with LSP semantic tokens after ~200 ms. The brief flash
      (e.g. PascalCase names appearing green) is inherent to how VS Code works
      and resolves automatically.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      What .NET version does the language server need?
    </Typography>
    <Typography variant="body2" paragraph>
      The LSP server requires <strong>.NET 8</strong> or later. Run{' '}
      <code>dotnet --version</code> to verify. If you have a non-standard
      install location, set <code>uitkx.server.dotnetPath</code> in VS Code
      settings.
    </Typography>

    {/* ── Authoring ────────────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Authoring
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      Can I use standard C# inside UITKX files?
    </Typography>
    <Typography variant="body2" paragraph>
      Yes. The setup code section (before the <code>return</code>) is standard
      C#. You can declare variables, call methods, use LINQ, and access any type
      available via namespace imports (<code>import &quot;@Ns&quot;</code> or the equivalent{' '}
      <code>@using</code>). Attribute values inside
      markup also accept C# expressions via the <code>{'{expr}'}</code> syntax.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      Does HMR (Hot Module Replacement) affect build times?
    </Typography>
    <Typography variant="body2" paragraph>
      No. HMR bypasses Unity's normal compilation pipeline — it compiles only
      the changed <code>.uitkx</code> file using Roslyn directly, loads the
      result via <code>Assembly.Load</code>, and swaps the render delegate.
      Typical save-to-visual-update time is 50–200 ms. When HMR is stopped,
      Unity compiles normally.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      Why do hooks need to be at the top level?
    </Typography>
    <Typography variant="body2" paragraph>
      UITKX follows the same rules as React hooks — they must be called
      unconditionally at the component top level, never inside{' '}
      <code>@if</code>, <code>@foreach</code>, or event handlers. This ensures
      hooks are called in the same order on every render, which is required for
      the reconciler to track state correctly.
    </Typography>

    {/* ── Troubleshooting ──────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Troubleshooting
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      I see "Failed to resolve assembly: Assembly-CSharp-Editor" from Burst —
      what do I do?
    </Typography>
    <Typography variant="body2" paragraph>
      Go to <strong>Edit → Project Settings → Burst AOT Settings</strong> and
      add <code>Assembly-CSharp-Editor</code> to the exclusion list. This
      prevents Burst from trying to AOT-compile editor-only assemblies.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      Completions or hover stopped working — how do I debug?
    </Typography>
    <Typography variant="body2" paragraph>
      Set <code>uitkx.trace.server</code> to <code>"verbose"</code> in VS Code
      settings, then open the Output panel (Ctrl+Shift+U) and select the "UITKX
      Language Server" channel. Check for error messages or missing responses.
      See the <em>Debugging Guide</em> page for more details.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      My component has red squiggles but the code looks correct — what's wrong?
    </Typography>
    <Typography variant="body2" paragraph>
      Make sure the file is saved — the language server works on the last saved
      content. Also check that each declaration is well-formed (e.g.{' '}
      <code>export VirtualNode Name() {'{ … }'}</code>) and that any imports are correct.
    </Typography>

    <Typography variant="body1" sx={Styles.question}>
      I get CS0229 ambiguity errors when using <code>Column</code>, <code>Row</code>, etc.
    </Typography>
    <Typography variant="body2" paragraph>
      Remove <code>@using UnityEngine.UIElements</code> from your <code>.uitkx</code>{' '}
      file. The <code>CssHelpers</code> shortcuts (<code>FlexRow</code>, <code>FlexColumn</code>,{' '}
      <code>SelectNone</code>, etc.) are auto-imported and conflict with the identically named
      UIElements enum members. Use CssHelpers shortcuts instead of qualified enum
      names like <code>FlexDirection.Column</code>.
    </Typography>
  </Box>
)
