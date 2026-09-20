import type { FC } from 'react'
import { Box, Typography } from '@mui/material'
import { CodeBlock } from '../../../components/CodeBlock/CodeBlock'
import Styles from '../Reference/UitkxReferencePage.style'

const GENERATED_FILE_PATH = `// Generated files are at:
// Library/PackageCache/com.reactiveuitoolkit/Analyzers~
//   or under your project's SourceGenerator~ output folder.
// Look for files ending in .uitkx.g.cs`

const WDYR_EXAMPLE = `using Ruitk.Diagnostics;

// Console: one line per component, per render pass.
WhyDidYouRender.Enabled = true;

// Or take the same facts structurally -- count them, filter them, draw them.
WhyDidYouRender.Rendered += (component, reason) =>
{
    if (reason == RenderReason.None) return;          // bailed out, body never ran
    if (reason.HasFlag(RenderReason.PropsChanged))    Tally(component, "props");
    if (reason.HasFlag(RenderReason.ContextChanged))  Tally(component, "context");
};`

const LSP_TRACE_SETTING = `{
  "uitkx.trace.server": "verbose"
}`

export const UitkxDebuggingPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Debugging Guide
    </Typography>
    <Typography variant="body1" paragraph>
      How to diagnose and fix common issues when working with UITKX.
    </Typography>

    {/* ── Inspecting generated code ──────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Inspecting Generated Code
    </Typography>
    <Typography variant="body2" paragraph>
      Every <code>.uitkx</code> file produces a corresponding <code>.uitkx.g.cs</code> file
      via the Roslyn source generator. To inspect it:
    </Typography>
    <Typography component="ol" variant="body2">
      <li>In VS Code, go to <strong>Definition</strong> (F12) on any generated symbol.</li>
      <li>
        Or navigate to the <code>GeneratedFiles</code> folder under your project's
        Analyzers output directory.
      </li>
      <li>
        The generated file contains <code>#line</code> directives that map errors
        back to the original <code>.uitkx</code> file and line number.
      </li>
    </Typography>
    <CodeBlock language="jsx" code={GENERATED_FILE_PATH} />

    {/* ── Reading #line mappings ──────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Understanding #line Directives
    </Typography>
    <Typography variant="body2" paragraph>
      When the C# compiler reports an error in generated code, the{' '}
      <code>#line</code> directive maps it back to your <code>.uitkx</code>{' '}
      source. For example:
    </Typography>
    <Typography variant="body2" component="ul">
      <li>
        <code>#line 42 "MyComponent.uitkx"</code> means the C# code that
        follows was generated from line 42 of your UITKX file.
      </li>
      <li>
        Clicking on the error in VS Code or Visual Studio will jump directly to
        the UITKX source line.
      </li>
    </Typography>

    {/* ── LSP server logs ─────────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      LSP Server Logs
    </Typography>
    <Typography variant="body2" paragraph>
      To see detailed LSP communication, set the trace level in your VS Code
      settings:
    </Typography>
    <CodeBlock language="json" code={LSP_TRACE_SETTING} />
    <Typography variant="body2" paragraph>
      Then open the <strong>Output</strong> panel (Ctrl+Shift+U) and select the{' '}
      <strong>UITKX Language Server</strong> channel. This shows all JSON-RPC
      requests and responses, which is useful for diagnosing:
    </Typography>
    <Typography component="ul" variant="body2">
      <li>Missing completions — check if the completion request/response is present</li>
      <li>Stale diagnostics — look for <code>textDocument/publishDiagnostics</code> messages</li>
      <li>Server crashes — look for error messages in the trace output</li>
    </Typography>

    {/* ── Breakpoints & stack traces ─────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Breakpoints &amp; Stack Traces
    </Typography>
    <Typography variant="body2" paragraph>
      Breakpoints cannot be set directly in <code>.uitkx</code> files. Instead,
      debug in the generated <code>.uitkx.g.cs</code> files:
    </Typography>
    <Typography component="ol" variant="body2">
      <li>Open the <code>.uitkx.g.cs</code> file (F12 on a generated symbol, or find it in the GeneratedFiles folder).</li>
      <li>Set breakpoints in the generated C# code — the debugger will hit them normally.</li>
      <li>
        When an exception occurs, the stack trace uses <code>#line</code>{' '}
        directives to show <strong>your original .uitkx file and line number</strong>,
        not the generated C# line.
      </li>
    </Typography>
    <Typography variant="body2" paragraph>
      You can also use Unity&apos;s built-in <strong>UI Toolkit Debugger</strong>{' '}
      (Window → UI Toolkit → Debugger) to inspect the live VisualElement tree
      that the UITKX reconciler produces.
    </Typography>

    {/* ── Formatter issues ────────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Formatter Issues
    </Typography>
    <Typography variant="body2" paragraph>
      If formatting produces unexpected results:
    </Typography>
    <Typography component="ol" variant="body2">
      <li>
        <strong>Check for syntax errors first</strong> — the formatter requires
        valid UITKX syntax. Fix any red squiggles before formatting.
      </li>
      <li>
        <strong>Ensure format-on-save is using the UITKX formatter</strong> —
        check that <code>editor.defaultFormatter</code> is set to{' '}
        <code>"ReactiveUITK.uitkx"</code> for <code>[uitkx]</code> files.
      </li>
      <li>
        <strong>Try formatting manually</strong> — press Shift+Alt+F to rule out
        format-on-save timing issues.
      </li>
    </Typography>

    {/* ── Why did this render? ────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Why Did This Render?
    </Typography>
    <Typography variant="body2" paragraph>
      <code>FiberReconciler.MetricsEmitted</code> reports per-commit totals with no component
      names: it tells you that forty components rendered, never which one is rendering every
      frame. <code>Ruitk.Diagnostics.WhyDidYouRender</code> answers the other half. The
      reconciler's bailout already decides on exactly four facts — first mount, a queued state
      update, unequal props, changed context, a different children list — and reports them as
      a <code>RenderReason</code> flags value.
    </Typography>
    <Typography variant="body2" paragraph>
      <code>RenderReason.None</code> is the interesting one: it means the bailout fired and the
      component's body did <strong>not</strong> run. That is what you want to see for a
      component you have just memoised.
    </Typography>
    <CodeBlock language="csharp" code={WDYR_EXAMPLE} />
    <Typography variant="body2" paragraph>
      Cost when nothing is subscribed: one static bool test per function-component render.
      Component names are resolved inside the reporting call, so the reflection that resolves
      them never runs while the feature is off, and the result is cached per component function
      rather than per fiber. Measured over 200,000 state-driven renders, the unsubscribed path
      is within run-to-run noise of the same build without the diagnostic.
    </Typography>

    {/* ── Reporting bugs ──────────────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Reporting Bugs
    </Typography>
    <Typography variant="body2" paragraph>
      When reporting an issue, include:
    </Typography>
    <Typography component="ol" variant="body2">
      <li>The minimal <code>.uitkx</code> file that reproduces the problem.</li>
      <li>The exact error message or diagnostic code (if any).</li>
      <li>Your editor (VS Code / Visual Studio / Rider) and extension version.</li>
      <li>LSP trace output if relevant (see above).</li>
    </Typography>
  </Box>
)
