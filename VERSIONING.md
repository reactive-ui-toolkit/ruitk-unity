# Versioning & Deprecation Policy

> Applies to ReactiveUIToolkit (Unity package), UITKX language, and IDE
> extensions.

---

## Versioning Strategy (SemVer)

All releases follow [Semantic Versioning 2.0.0](https://semver.org/).

### Before 1.0.0

While the leading digit is `0`, a breaking change bumps the MINOR digit rather
than the major one: `0.20.x` -> `0.21.0`. SemVer leaves `0.y.z` deliberately
unconstrained, and reserving `1.0.0` for the API-stability promise is worth more
than spending it on the first breaking change that comes along.

Everything below describes the post-1.0.0 rules, and applies today with that one
substitution. A breaking change still earns a **Breaking** changelog entry, a
migration note, and - where the edit is mechanical - a codemod. The version digit
is the only thing this clause changes.

### Major (X.0.0)

A major bump signals breaking changes. Users may need to update code.

Examples of breaking changes:
- Removing or renaming a public C# API (`V.Func`, `RootRenderer`, hook
  signatures)
- Removing or changing UITKX syntax semantics (removing a directive, changing
  control flow behaviour)
- Dropping support for a Unity version

### Minor (1.X.0)

A minor bump adds new functionality. Existing code continues to work without
changes.

Examples of minor changes:
- New hooks, new intrinsic elements, new directives
- New diagnostic codes (may produce new warnings, but don't break builds)
- New extension features (completions, hover, formatting improvements)
- New configuration options

### Patch (1.0.X)

A patch bump fixes bugs. No API or syntax changes.

Examples of patch changes:
- Formatter fixes, completion fixes, diagnostic false-positive fixes
- Documentation updates
- Performance improvements with no API change

### IDE Extensions

IDE extensions (VS Code, Visual Studio 2022) are versioned independently from
the Unity package. Extension versions do not need to match the Unity package
version. Each extension follows the same SemVer rules within its own version
sequence.

---

## Deprecation Policy

### Timeline

- **One minor version warning**: Deprecated APIs are marked with
  `[Obsolete("Use X instead. Will be removed in vN.Y+1.")]` for at least one
  minor release before removal.
- **Removal in the next minor**: If version 1.1 deprecates an API, version 1.2
  may remove it.

### Communication

- Every deprecation is documented in `CHANGELOG.md` with the version that
  introduced the deprecation and the planned removal version.
- Every removal is documented in `CHANGELOG.md` when it ships.

### UITKX Syntax

Syntax deprecations follow the same one-minor-version rule. The source
generator will emit a warning diagnostic for one minor version before the
syntax is removed.

### Exceptions

- **Security fixes** may remove or change APIs immediately without a
  deprecation period.
- **Pre-1.0 releases** (if any) make no stability guarantees.

---

## Compatibility

| Dependency | Minimum Version |
|------------|----------------|
| Unity | 6.2 |
| .NET (LSP server) | 8.0 |
| VS Code | 1.85+ |
| Visual Studio | 2022 (17.0+) |

---

## Distribution Channels

| Channel | What | Pricing |
|---------|------|---------|
| Unity Asset Store | ReactiveUIToolkit Unity package | Paid |
| Gumroad | ReactiveUIToolkit Unity package | Paid |
| Itch.io | ReactiveUIToolkit Unity package | Paid |
| VS Code Marketplace | UITKX VS Code extension | Free |
| Visual Studio Marketplace | UITKX Visual Studio extension | Free |
