# HookRegistry golden snapshots

These files are **immutable test fixtures** captured from the codebase state
at 0.6.0 (the commit immediately before the unified `HookRegistry` refactor
landed in 0.5.23). They lock the byte-exact output of the 8 sites that the
registry consolidates.

| Golden file | Source (pre-refactor) |
|---|---|
| `sg_alias_table.golden.txt`      | `SourceGenerator~/Emitter/CSharpEmitter.cs` `s_hookAliases` |
| `signature_regex.golden.txt`     | `SourceGenerator~/Emitter/CSharpEmitter.cs` `s_hookSignatureRe.ToString()` |
| `generic_alias_regex.golden.txt` | `SourceGenerator~/Emitter/CSharpEmitter.cs` `s_genericHookAliasRe.ToString()` |
| `validation_patterns.golden.txt` | `SourceGenerator~/Emitter/HooksValidator.cs` `s_hookPatterns` — one pattern per line |
| `hover_docs.golden.json`         | `ide-extensions~/lsp-server/HoverHandler.cs` `s_hookDocs` serialized as ordered JSON `{key: markdown}` |
| `vdg_static_stubs.golden.txt`    | `ide-extensions~/language-lib/Roslyn/VirtualDocumentGenerator.cs` static stub block (containerClass form, ~L342-367) |
| `vdg_instance_stubs.golden.txt`  | same file, instance stub block (function-style form, ~L515-540) |

## Coverage gaps that the refactor closes

The registry surfaces three current consumer drift bugs as side-benefits:

1. **Validator missing `useLayoutEffect`** — `s_hookPatterns` has 20 names,
   alias table has 21. Adding the 21st (`useLayoutEffect`) via the registry
   means `UITKX0013` (rules-of-hooks) will now also catch conditional
   `useLayoutEffect` calls. Pure expansion — no legitimate code breaks.
2. **Hover docs missing `useLayoutEffect` entries** — same root cause.
   Registry-driven hover map adds the missing 2 entries (camel + qualified).
3. **`useLayoutEffect` was the only known coverage gap** as of 0.6.0; all
   other hook names appear consistently across all 8 sites.

The golden files captured here REPRESENT THE PRE-REFACTOR STATE — they
include the gaps. Post-refactor consumer outputs are compared against
**augmented** versions of these goldens (see `HookRegistryTests.cs`).

## Regeneration policy

These files must not be edited by hand. To regenerate after an intentional
hook addition, run the `Golden_RegenerateAllSnapshots` test fixture
(currently marked `[Fact(Skip = ...)]`) and commit the result alongside the
hook addition.

## How these are maintained (updated 2026-08-29, RTR-1)

**They are not regenerated.** The word "immutable" above is meant literally: each
file is the 0.6.0 capture and stays that way. What changes is the TEST, which
compares by DIFF rather than by byte-equality:

- `Golden_ValidationPatterns_…` asserts nothing was **removed** and that the
  additions are exactly an explicit list.
- `Golden_SignatureRegex_…` asserts every alternative in the fixture is still
  present, still in its original **relative order**, and that the additions are
  exactly an explicit list.

Adding a hook therefore means extending the *expected-additions* list in the
test, naming what you added and why — not overwriting the fixture. A byte-compare
would have to be regenerated on every change, which turns a guard into a record
of the last edit and quietly permits a removal.

The remaining goldens (alias table, generic regex, both stub blocks) are still
byte-compared, because router hooks deliberately do not appear in them.

### Additions since 0.6.0

| Added | Why |
|---|---|
| `useLayoutEffect` (3 validation forms, 2 hover docs) | Pre-refactor coverage bug, closed by the registry itself. |
| 16 router hooks (3 validation forms each, 1 hover doc each, 1 call-site row each) | RTR-1. They are hooks — `UseBlocker` composes `Hooks.UseEffect`, so a conditional call breaks effect ordering — and nothing reported it before. Their qualified form names `RouterHooks`, not `Hooks`. |
