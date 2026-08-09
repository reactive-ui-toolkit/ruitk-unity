# Samples~ (development placeholder)

In the development repo the demos live in `Samples/` (no tilde), where Unity
compiles them directly and the SourceGenerator test corpus gates them. The
release pipeline (`publish.yml` deploy-dist and the store dist builder in
`CICD/Editor/PublishUtility.cs`) renames `Samples/` to `Samples~/` so shipped
packages get the standard import-on-demand UPM samples layout that
`package.json`'s `samples` entry points at.

This placeholder exists ONLY so that path resolves in a development or
embedded checkout too: Unity 6.5's Package Manager computes sample sizes on
every package change and throws an endless `DirectoryNotFoundException` when
the declared path is missing. Both release paths delete this folder before
moving the real demos in, so it never ships.
