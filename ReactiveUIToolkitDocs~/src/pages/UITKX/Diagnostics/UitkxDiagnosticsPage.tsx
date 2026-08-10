import type { FC } from 'react'
import { Link as MuiLink } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import {
  Box,
  Chip,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import Styles from '../Reference/UitkxReferencePage.style'

export const UitkxDiagnosticsPage: FC = () => (
  <Box sx={Styles.root}>
    <Typography variant="h4" component="h1" gutterBottom>
      Diagnostics Reference
    </Typography>
    <Typography variant="body1" paragraph>
      Every diagnostic code emitted by the UITKX source generator and language
      server, with severity, meaning, and how to fix it.
    </Typography>

    {/* ── Generator Diagnostics ─────────────────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Source Generator Diagnostics
    </Typography>
    <Typography variant="body2" paragraph>
      Emitted at compile time by the Roslyn source generator when processing{' '}
      <code>.uitkx</code> files. Several structural codes (e.g. <code>UITKX0104</code>,{' '}
      <code>UITKX0106</code>, <code>UITKX0108</code>, <code>UITKX0109</code>,{' '}
      <code>UITKX0120</code>, <code>UITKX0121</code>) fire both here and live in the editor —
      they also appear in the language-server table below.
    </Typography>
    <TableContainer>
      <Table size="small" sx={Styles.table}>
        <TableHead>
          <TableRow>
            <TableCell>Code</TableCell>
            <TableCell>Severity</TableCell>
            <TableCell>Title</TableCell>
            <TableCell>How to fix</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          <TableRow>
            <TableCell><Chip label="UITKX0001" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Unknown built-in element</TableCell>
            <TableCell>Check the tag name — built-in elements use PascalCase (e.g. <code>&lt;Button&gt;</code>, <code>&lt;Label&gt;</code>).</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0109" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Unknown attribute on element</TableCell>
            <TableCell>Verify the attribute name matches a property on the element's props type.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0008" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Unknown function component</TableCell>
            <TableCell>Ensure the component type exists and has a public static <code>Render</code> method.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0106" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Loop element missing key</TableCell>
            <TableCell>Add a <code>key</code> attribute with a stable unique identifier. Applies to all loop types: <code>@foreach</code>, <code>@for</code>, <code>@while</code>.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0104" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Duplicate sibling key</TableCell>
            <TableCell>Ensure each sibling element has a unique <code>key</code> value.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0113" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Duplicate component declaration (same file)</TableCell>
            <TableCell>Two <code>component</code>s with the same name in one file — the generator emits only the first and ignores the duplicate. Rename one. (The language server flags the cross-file variant under the same code.)</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0012" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Directive order error</TableCell>
            <TableCell>Move <code>@namespace</code> above <code>@component</code>.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0013" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Hook in conditional</TableCell>
            <TableCell>Move the hook call to the component top level, outside any <code>@if</code> branch.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0014" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Hook in loop</TableCell>
            <TableCell>Move the hook call to the component top level, outside any <code>@foreach</code> loop.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0015" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Hook in switch case</TableCell>
            <TableCell>Move the hook call to the component top level, outside the <code>@switch</code>.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0016" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Hook in event handler</TableCell>
            <TableCell>Move the hook call to the component top level — hooks cannot be called inside attribute expressions.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0108" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Multiple root elements</TableCell>
            <TableCell>Wrap all root elements in a single container element (e.g. <code>&lt;VisualElement&gt;</code>).</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0018" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>UseEffect missing dependency array</TableCell>
            <TableCell>Pass an explicit dependency array as the second argument, or <code>Array.Empty&lt;object&gt;()</code> for run-once.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0019" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Loop variable used as key</TableCell>
            <TableCell>Use a stable unique identifier from the item instead of the loop index.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0020" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>ref on component without Ref&lt;T&gt; param</TableCell>
            <TableCell>Add a <code>Ref&lt;T&gt;?</code> parameter to the component, or remove the <code>ref</code> attribute.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0021" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>ref ambiguous — multiple Ref&lt;T&gt; params</TableCell>
            <TableCell>Use an explicit prop name (e.g. <code>inputRef={'{x}'}</code>) instead of <code>ref</code>.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0120" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Asset file not found</TableCell>
            <TableCell>Check the asset path — it must be relative to the <code>.uitkx</code> file or an absolute project path.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0121" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Asset type mismatch</TableCell>
            <TableCell>The asset at the given path is a different type than the requested <code>Asset&lt;T&gt;</code>. Verify the generic type parameter.</TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </TableContainer>

    {/* ── Structural Diagnostics (UITKX0101–0111) ────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Structural Diagnostics (Language Server)
    </Typography>
    <Typography variant="body2" paragraph>
      Emitted in real time by the language server as you type. These appear as
      squiggly underlines in your editor.
    </Typography>
    <TableContainer>
      <Table size="small" sx={Styles.table}>
        <TableHead>
          <TableRow>
            <TableCell>Code</TableCell>
            <TableCell>Severity</TableCell>
            <TableCell>Message</TableCell>
            <TableCell>How to fix</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          <TableRow>
            <TableCell><Chip label="UITKX0101" size="small" color="default" variant="outlined" /></TableCell>
            <TableCell><Chip label="Reserved" size="small" /></TableCell>
            <TableCell>Missing <code>@namespace</code> — no longer flagged</TableCell>
            <TableCell><code>@namespace</code> is optional: the namespace is path-derived when omitted (see <MuiLink component={RouterLink} to="/imports">Imports &amp; Exports</MuiLink>). The code stays reserved and is never emitted.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0102" size="small" color="default" variant="outlined" /></TableCell>
            <TableCell><Chip label="Reserved" size="small" /></TableCell>
            <TableCell>Missing <code>@component</code> — no longer flagged</TableCell>
            <TableCell>Plain-declaration files need no directive; member-only files are legal. Reserved, never emitted.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0103" size="small" color="default" variant="outlined" /></TableCell>
            <TableCell><Chip label="Removed" size="small" /></TableCell>
            <TableCell>Filename ≠ component name — no longer flagged</TableCell>
            <TableCell>A file may declare several components, so a filename-match rule is meaningless. Matching the filename is now a documentation convention (see <MuiLink component={RouterLink} to="/imports">Imports &amp; Exports</MuiLink>), not a diagnostic.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0104" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Duplicate sibling key</TableCell>
            <TableCell>Ensure each sibling has a unique <code>key</code>.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0105" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Unknown element — no component found</TableCell>
            <TableCell>Check the tag name or add the missing component to your project.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0106" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Loop element missing key</TableCell>
            <TableCell>Add a <code>key</code> attribute for reconciliation. Applies to <code>@foreach</code>, <code>@for</code>, <code>@while</code>.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0107" size="small" color="info" variant="outlined" /></TableCell>
            <TableCell><Chip label="Hint" size="small" color="info" /></TableCell>
            <TableCell>Unreachable code after return / @break / @continue</TableCell>
            <TableCell>Remove the unreachable code, or restructure control flow.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0108" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Multiple root elements</TableCell>
            <TableCell>Wrap all root elements in a single container.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0109" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Unknown attribute on element</TableCell>
            <TableCell>
              For built-in tags: check the attribute against the element's props type. For
              <strong> user components</strong>: only declared parameters and the universal
              <code>key</code> / <code>ref</code> attributes are accepted — intrinsic
              attributes like <code>style</code>, <code>className</code>, <code>onClick</code>,
              <code>extraProps</code> are <em>not</em> auto-forwarded. Either declare the
              attribute as a parameter on the component (and forward it in the body) or
              remove it.
            </TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0111" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Unused component parameter</TableCell>
            <TableCell>Remove the unused parameter or use it in the component body.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0112" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Unused variable in setup code</TableCell>
            <TableCell>Remove the unused variable or use it in the component.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0113" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Duplicate component declaration in the same asmdef</TableCell>
            <TableCell>Two or more <code>.uitkx</code> files in the same asmdef declare a <code>component</code> with the same name. The source generator picks one deterministically (first by path), but consumers across the asmdef may bind to the wrong one. Rename one of the components, or move it to a different asmdef. Cross-asmdef collisions are legal and not flagged.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0120" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Asset path not found (language server)</TableCell>
            <TableCell>Fix the asset path in <code>Asset&lt;T&gt;("path")</code> — the language server validates paths in real time.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0121" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Asset type validation (language server)</TableCell>
            <TableCell>The asset type does not match the <code>Asset&lt;T&gt;</code> generic parameter.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0200" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Attribute requires newer Unity version</TableCell>
            <TableCell>The attribute is only available in a newer Unity version, or was removed in the current version.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0210" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Write to <code>[UitkxHmrSwap]</code> field outside type initializer</TableCell>
            <TableCell>The field is generator-managed for HMR re-initialization. Move the assignment into a <code>static</code> constructor or field initializer. The HMR pipeline will overwrite any external write on the next save. Suppress with <code>#pragma warning disable UITKX0210</code> if intentional.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0211" size="small" color="default" variant="outlined" /></TableCell>
            <TableCell><Chip label="Reserved" size="small" /></TableCell>
            <TableCell><code>const</code> field inside a legacy <code>module</code> body — retired</TableCell>
            <TableCell>Retired in 0.16.0 with the legacy module grammar: plain-declaration member bodies have no field-position <code>const</code>, so the HMR-invisibility hazard this warned about no longer exists. The ID stays reserved.</TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </TableContainer>

    {/* ── Parser Diagnostics (UITKX0300–0306) ────────────────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Parser Diagnostics
    </Typography>
    <Typography variant="body2" paragraph>
      Emitted when the parser encounters malformed syntax.
    </Typography>
    <TableContainer>
      <Table size="small" sx={Styles.table}>
        <TableHead>
          <TableRow>
            <TableCell>Code</TableCell>
            <TableCell>Severity</TableCell>
            <TableCell>Title</TableCell>
            <TableCell>How to fix</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          <TableRow>
            <TableCell><Chip label="UITKX0300" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Unexpected token</TableCell>
            <TableCell>Check for typos or misplaced syntax near the reported line.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0301" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Unclosed tag</TableCell>
            <TableCell>Add a matching closing tag or use self-closing syntax (<code>/&gt;</code>).</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0302" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Mismatched closing tag</TableCell>
            <TableCell>Ensure the closing tag matches the opening tag name.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0303" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Unexpected end of file</TableCell>
            <TableCell>Close any open tags, braces, or expressions.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0304" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Unclosed expression or block</TableCell>
            <TableCell>Close the unclosed brace or parenthesis.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0305" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Unknown markup directive</TableCell>
            <TableCell>Valid directives: <code>@if</code>, <code>@else</code>, <code>@for</code>, <code>@foreach</code>, <code>@while</code>, <code>@switch</code>, <code>@case</code>, <code>@default</code>.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX0306" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell><code>@(expr)</code> markup syntax is no longer supported</TableCell>
            <TableCell>
              The <code>@(expr)</code> markup-child embed has been removed. Use{' '}
              <code>{'{expr}'}</code> instead (matching JSX/Babel/React). Migration
              is mechanical: replace every <code>@(</code> with <code>{'{'}</code>{' '}
              and the matching <code>)</code> with <code>{'}'}</code>. The{' '}
              <code>@</code> prefix still marks <strong>directives</strong>{' '}
              (<code>@if</code>, <code>@for</code>, <code>@foreach</code>,{' '}
              <code>@while</code>, <code>@switch</code>, <code>@case</code>,{' '}
              <code>@default</code>, <code>@using</code>, <code>@namespace</code>,{' '}
              <code>@component</code>, <code>@props</code>, <code>@key</code>,{' '}
              <code>@inject</code>, <code>@uss</code>).
            </TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </TableContainer>

    {/* ── Function-Style Component Diagnostics (UITKX2100–2106) ─────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Function-Style Component Diagnostics
    </Typography>
    <Typography variant="body2" paragraph>
      Emitted by the language server when validating function-style component
      declarations.
    </Typography>
    <TableContainer>
      <Table size="small" sx={Styles.table}>
        <TableHead>
          <TableRow>
            <TableCell>Code</TableCell>
            <TableCell>Severity</TableCell>
            <TableCell>Message</TableCell>
            <TableCell>How to fix</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          <TableRow>
            <TableCell><Chip label="UITKX2100" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Non-PascalCase component name</TableCell>
            <TableCell>Component names must start with an uppercase letter (e.g. <code>MyButton</code>, not <code>myButton</code>).</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2101" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Missing return statement</TableCell>
            <TableCell>Function-style components must end with <code>return (...);</code> containing markup.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2102" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Malformed return statement</TableCell>
            <TableCell>The <code>return</code> must be followed by <code>(...)</code> containing UITKX markup.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2104" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Mixed directive and function-style syntax</TableCell>
            <TableCell>Use either directive headers (<code>@namespace</code> / <code>@component</code>) or declarations (<code>export VirtualNode Name() {'{ }'}</code>), not both.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2105" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Invalid function-style declaration</TableCell>
            <TableCell>Ensure the <code>component</code> keyword is followed by a valid name and optional parameter list.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2106" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Missing parameter name</TableCell>
            <TableCell>Each parameter in the component signature must have a name: <code>export VirtualNode Name(string text)</code>.</TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </TableContainer>

    {/* ── Import / Export Diagnostics (UITKX2300–2317) ──────────────────── */}
    <Typography variant="h5" component="h2" sx={Styles.section}>
      Import / Export Diagnostics
    </Typography>
    <Typography variant="body2" paragraph>
      Emitted when resolving the <code>import</code> / <code>export</code> grammar. Codes
      2300–2309 are shared family-wide (identical across the Unity, Unreal, and Godot ports
      modulo prefix); 2310–2317 are Unity-local (namespaces are a Unity/C# concept). See the{' '}
      <MuiLink component={RouterLink} to="/imports">Imports &amp; Exports</MuiLink> page for the full grammar.
    </Typography>
    <TableContainer>
      <Table size="small" sx={Styles.table}>
        <TableHead>
          <TableRow>
            <TableCell>Code</TableCell>
            <TableCell>Severity</TableCell>
            <TableCell>Message</TableCell>
            <TableCell>How to fix</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          <TableRow>
            <TableCell><Chip label="UITKX2300" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Unknown import specifier</TableCell>
            <TableCell>No <code>.uitkx</code> file at that path. Use a relative (<code>./</code>, <code>../</code>) or <code>~/</code> specifier — engine-native forms (<code>Assets/</code>, <code>Packages/</code>) never resolve.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2301" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Name is not exported</TableCell>
            <TableCell>Add <code>export</code> to the declaration in the target file.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2302" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Name not declared in that file</TableCell>
            <TableCell>The target file resolves but declares no such name. Fix the imported name or the specifier.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2303" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Duplicate import</TableCell>
            <TableCell>The name is already imported (from another specifier). Remove the duplicate.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2304" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Unused import</TableCell>
            <TableCell>The imported binding (named, <code>* as</code>, or default) is never referenced. Remove it. Error since 0.9.1 — the reference scan over-approximates "used", so this only fires when the binding truly appears nowhere.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2305" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error / Warning" size="small" color="warning" /></TableCell>
            <TableCell>Peer-exported name used without an import</TableCell>
            <TableCell>The message names the exact line to add, e.g. <code>import {'{ X }'} from "./X"</code>. <strong>Error</strong> for component tags (<code>&lt;X&gt;</code> is uitkx-only syntax, so the evidence is sound); <strong>Warning</strong> for bare hook-call / module member-access matches — plain C# (a nested enum via <code>@using static</code>, <code>Screen.width</code>, a hand-written hook) can legitimately produce those shapes, and a truly missing import still fails the compile with CS0103.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2306" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Value-import cycle</TableCell>
            <TableCell>Hooks and member values load eagerly, so a cycle among their imports is an error (components are exempt). Break the chain or move to a component reference.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2307" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Used like a hook but nothing exports it</TableCell>
            <TableCell>Declare and <code>export</code> it, or import an existing one. Warning-tier because a hand-written C# hook resolves ambiently and produces the identical call shape — built-in hooks (<code>useState</code>, …) are always exempt.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2308" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Import crosses an asmdef boundary</TableCell>
            <TableCell>Imports are module-scoped in v1 — the target must be in the same assembly definition.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2309" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Import after the first declaration</TableCell>
            <TableCell>Move all <code>import</code> lines into the preamble, before any declaration.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2310" size="small" color="default" variant="outlined" /></TableCell>
            <TableCell><Chip label="Reserved" size="small" /></TableCell>
            <TableCell>Cannot derive a namespace</TableCell>
            <TableCell>Reserved. In practice every file under a project root now derives a namespace (no-asmdef files anchor at the config root), so this no longer fires — add an explicit <code>@namespace</code> if you want to pin one.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2311" size="small" color="warning" variant="outlined" /></TableCell>
            <TableCell><Chip label="Warning" size="small" color="warning" /></TableCell>
            <TableCell>Export accessibility mismatch across merged parts</TableCell>
            <TableCell>A <code>component</code> and a same-named <code>module</code> (or other merged parts) disagree on <code>export</code>. The component&rsquo;s accessibility is used; align the <code>export</code> keywords.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2312" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Hook-container merge conflict</TableCell>
            <TableCell>Same-named hook containers in two files disagree (duplicate hook name or accessibility). Rename or consolidate.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2313" size="small" color="default" variant="outlined" /></TableCell>
            <TableCell><Chip label="Reserved" size="small" /></TableCell>
            <TableCell>Layout conventions — not emitted</TableCell>
            <TableCell>One component per file, hooks in a <code>.hooks</code> file, and filename == component are documentation best-practices (see <MuiLink component={RouterLink} to="/imports">Imports &amp; Exports</MuiLink>), not code-enforced warnings.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2314" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell><code>~/</code> root not configured / escapes the project</TableCell>
            <TableCell>Set <code>"root"</code> in <code>uitkx.config.json</code>, or fix the specifier so it stays inside the project.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2316" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error / Warning" size="small" color="warning" /></TableCell>
            <TableCell>Unknown namespace in a <code>@using</code> / <code>import "@Ns"</code></TableCell>
            <TableCell>The namespace analogue of 2300: the namespace resolves nowhere in the assembly or its references. <strong>Error</strong> in the editor (a located squiggle — the misspelled-<code>@using</code> that used to be silent); <strong>Warning</strong> at build (it never breaks a valid build — the emitted <code>using</code>&rsquo;s CS0246 stays the gate). Fix the spelling, add the missing asmdef reference, or remove the using.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2317" size="small" color="default" variant="outlined" /></TableCell>
            <TableCell><Chip label="Hint" size="small" /></TableCell>
            <TableCell>Redundant using (already in scope)</TableCell>
            <TableCell>The namespace is auto-injected into every generated file (e.g. <code>@using UnityEngine</code>, <code>@using System</code>), so the line is dead weight. Editor Hint (faded); the &ldquo;Remove redundant using&rdquo; quick-fix or the codemod <code>--tidy</code> strips it.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2320" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Removed wrapper keyword (<code>component</code>/<code>hook</code>/<code>module</code>)</TableCell>
            <TableCell>The wrapper keywords were removed in <strong>0.16.0</strong> (deprecated since 0.9.0). Write a plain <code>export</code> declaration instead — the <code>UitkxMigrateImports --es-modules</code> codemod (<code>node scripts/migrate-uitkx.mjs &lt;dir&gt; --es-modules</code>) rewrites whole trees automatically. Family code (identical in all three engines).</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2321" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell><code>use</code>-prefixed declaration returns <code>VirtualNode</code></TableCell>
            <TableCell>Did you mean a component? Components are PascalCase and return <code>VirtualNode</code>; hooks are <code>use</code>-prefixed and return anything else.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2322" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Value export cannot infer its type</TableCell>
            <TableCell>The type-less form is legal only when the initializer names the type: <code>export theme = new Style {'{'} … {'}'}</code>. Otherwise declare it: <code>export Style theme = …</code>. Family divergence: Unity is the only engine that EMITS this code — the C# typed dialect has real inference sugar; Godot and Unreal register the number but never fire it.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2323" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell><code>export default</code> / <code>export {'{ … }'}</code> names an undeclared symbol</TableCell>
            <TableCell>The name must match a top-level declaration in this file.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2324" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Duplicate export</TableCell>
            <TableCell>A name is exported twice — inline <code>export</code> + a list entry, twice in one list, or across lists. Remove the duplicate.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2325" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Import alias collision</TableCell>
            <TableCell>An <code>as</code>-rename, <code>* as X</code>, or default-import binding collides with a declaration in this file or another import. Rename the import.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2326" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Default import of a file with no <code>export default</code></TableCell>
            <TableCell>Use a named import instead (the message suggests one), or add <code>export default Name;</code> to the target.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2327" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Duplicate <code>export default</code></TableCell>
            <TableCell>A file has at most one default export.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2107" size="small" color="default" variant="outlined" /></TableCell>
            <TableCell><Chip label="Reserved" size="small" /></TableCell>
            <TableCell>Companion partial-class merge — retired</TableCell>
            <TableCell>Retired in 0.16.0: the legacy <code>module</code>-into-component merge mechanism was removed with the wrapper grammar, so there is nothing left to warn about. The ID stays reserved.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2108" size="small" color="default" variant="outlined" /></TableCell>
            <TableCell><Chip label="Reserved" size="small" /></TableCell>
            <TableCell>Mixed declaration styles — retired</TableCell>
            <TableCell>Retired in 0.16.0: with the wrapper grammar removed there is only one declaration style, so a file can no longer mix two. The ID stays reserved (recorded family divergence — sibling engines retire it on their own removal waves).</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2109" size="small" color="default" variant="outlined" /></TableCell>
            <TableCell><Chip label="Reserved" size="small" /></TableCell>
            <TableCell>Import form needs a migrated target — retired</TableCell>
            <TableCell>Retired in 0.16.0: a legacy target now fails its own parse with <code>UITKX2320</code>, so the migrate-the-target-first gate has no subject. The ID stays reserved.</TableCell>
          </TableRow>
          <TableRow>
            <TableCell><Chip label="UITKX2110" size="small" color="error" variant="outlined" /></TableCell>
            <TableCell><Chip label="Error" size="small" color="error" /></TableCell>
            <TableCell>Hook rename drops the <code>use</code> prefix (Unity-local)</TableCell>
            <TableCell><code>import {'{ useX as y }'}</code> — hook bindings must stay <code>use</code>-prefixed (the prefix drives Rules-of-Hooks analysis and emission).</TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </TableContainer>
  </Box>
)
