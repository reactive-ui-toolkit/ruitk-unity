using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using OmniSharp.Extensions.JsonRpc;
using Ruitk.Core;
using Ruitk.Language;

namespace UitkxLanguageServer;

// ─────────────────────────────────────────────────────────────────────────────
//  Custom ruitk/* requests (VE-07)
//
//  The RUITK Builder (the in-Unity visual editor) is an LSP client of this
//  server. Beyond the standard protocol it needs four data surfaces the server
//  already computes: the element/attribute schema, the workspace import graph,
//  a component's typed props, and the hook table. Serving them over the same
//  channel keeps the Builder, the IDE extensions, and the build reading ONE
//  source of truth instead of three parallel tables.
//
//  All four are plain request/response (Direction.ClientToServer), registered
//  in Program.cs via WithHandler<>. Buffer-first semantics come for free: the
//  WorkspaceIndex behind them reads DocumentStore overlays before disk.
// ─────────────────────────────────────────────────────────────────────────────

[Parallel]
[Method("ruitk/schema", Direction.ClientToServer)]
public sealed class RuitkSchemaParams : IRequest<RuitkSchemaResult> { }

public sealed class RuitkSchemaResult
{
    /// <summary>The raw uitkx-schema.json text (same embedded resource UitkxSchema parses).</summary>
    public string Json { get; init; } = "";
}

[Parallel]
[Method("ruitk/hooks", Direction.ClientToServer)]
public sealed class RuitkHooksParams : IRequest<RuitkHooksResult> { }

public sealed class RuitkHookInfo
{
    public string Name { get; init; } = "";
    public string Doc { get; init; } = "";
}

public sealed class RuitkHooksResult
{
    /// <summary>Canonical hook names (both casings) with markdown docs — the Builder's hook palette.</summary>
    public List<RuitkHookInfo> Hooks { get; init; } = new();
}

[Parallel]
[Method("ruitk/componentProps", Direction.ClientToServer)]
public sealed class RuitkComponentPropsParams : IRequest<RuitkComponentPropsResult>
{
    public string Name { get; set; } = "";
}

public sealed class RuitkPropInfo
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string Doc { get; init; } = "";
    public int Line { get; init; }
}

public sealed class RuitkComponentPropsResult
{
    public bool Found { get; init; }
    public string FilePath { get; init; } = "";
    public int FileLine { get; init; }
    public List<RuitkPropInfo> Props { get; init; } = new();
}

[Parallel]
[Method("ruitk/workspaceGraph", Direction.ClientToServer)]
public sealed class RuitkWorkspaceGraphParams : IRequest<RuitkWorkspaceGraphResult> { }

public sealed class RuitkGraphExport
{
    public string Name { get; init; } = "";

    /// <summary>Component | Hook | Module | Util (StrictImportDetector.ExportKind names).</summary>
    public string Kind { get; init; } = "";
}

public sealed class RuitkGraphNode
{
    public string File { get; init; } = "";
    public List<RuitkGraphExport> Exports { get; init; } = new();
}

public sealed class RuitkGraphEdge
{
    public string FromFile { get; init; } = "";

    /// <summary>Resolved absolute target path, empty when the specifier does not resolve.</summary>
    public string ToFile { get; init; } = "";

    public string Specifier { get; init; } = "";
    public List<string> Names { get; init; } = new();
}

public sealed class RuitkWorkspaceGraphResult
{
    public List<RuitkGraphNode> Nodes { get; init; } = new();
    public List<RuitkGraphEdge> Edges { get; init; } = new();
}

// ── Handlers ────────────────────────────────────────────────────────────────

public sealed class RuitkSchemaHandler : IJsonRpcRequestHandler<RuitkSchemaParams, RuitkSchemaResult>
{
    public Task<RuitkSchemaResult> Handle(RuitkSchemaParams request, CancellationToken cancellationToken)
    {
        // Same embedded resource SchemaLoader parses; served raw so the Builder
        // and the LSP deserialize identical bytes.
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("uitkx-schema.json");
        if (stream == null)
            return Task.FromResult(new RuitkSchemaResult { Json = "" });
        using var reader = new StreamReader(stream);
        return Task.FromResult(new RuitkSchemaResult { Json = reader.ReadToEnd() });
    }
}

public sealed class RuitkHooksHandler : IJsonRpcRequestHandler<RuitkHooksParams, RuitkHooksResult>
{
    public Task<RuitkHooksResult> Handle(RuitkHooksParams request, CancellationToken cancellationToken)
    {
        var docs = HookRegistry.GetDocMap();
        var result = new RuitkHooksResult();
        foreach (var name in HookRegistry.AmbientHookNames)
        {
            docs.TryGetValue(name, out var doc);
            result.Hooks.Add(new RuitkHookInfo { Name = name, Doc = doc ?? "" });
        }
        return Task.FromResult(result);
    }
}

public sealed class RuitkComponentPropsHandler
    : IJsonRpcRequestHandler<RuitkComponentPropsParams, RuitkComponentPropsResult>
{
    private readonly WorkspaceIndex _index;

    public RuitkComponentPropsHandler(WorkspaceIndex index) => _index = index;

    public Task<RuitkComponentPropsResult> Handle(
        RuitkComponentPropsParams request,
        CancellationToken cancellationToken
    )
    {
        var info = _index.TryGetElementInfo(request.Name);
        if (info == null)
            return Task.FromResult(new RuitkComponentPropsResult { Found = false });

        var result = new RuitkComponentPropsResult
        {
            Found = true,
            FilePath = info.FilePath,
            FileLine = info.FileLine,
        };
        foreach (var p in info.Props)
            result.Props.Add(new RuitkPropInfo
            {
                Name = p.Name,
                Type = p.Type,
                Doc = p.XmlDoc,
                Line = p.Line,
            });
        return Task.FromResult(result);
    }
}

public sealed class RuitkWorkspaceGraphHandler
    : IJsonRpcRequestHandler<RuitkWorkspaceGraphParams, RuitkWorkspaceGraphResult>
{
    private readonly WorkspaceIndex _index;

    public RuitkWorkspaceGraphHandler(WorkspaceIndex index) => _index = index;

    public Task<RuitkWorkspaceGraphResult> Handle(
        RuitkWorkspaceGraphParams request,
        CancellationToken cancellationToken
    )
    {
        var result = new RuitkWorkspaceGraphResult();

        var byFile = new Dictionary<string, RuitkGraphNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, name, kind) in _index.GetExportsSnapshot())
        {
            if (!byFile.TryGetValue(file, out var node))
            {
                node = new RuitkGraphNode { File = file };
                byFile[file] = node;
                result.Nodes.Add(node);
            }
            node.Exports.Add(new RuitkGraphExport { Name = name, Kind = kind.ToString() });
        }

        foreach (var (file, specifier, names) in _index.GetImportEdges(null))
        {
            // Raw specifiers are what the index stores; resolution to a target
            // file uses the same ImportResolver rule as the build. Unresolvable
            // specifiers still ship as edges (ToFile empty) so the Builder can
            // render a broken-edge marker instead of silently dropping it.
            string? target = null;
            try { target = LspHelpers.ResolveImportTarget(file, specifier); }
            catch { }

            var edge = new RuitkGraphEdge
            {
                FromFile = file,
                ToFile = target ?? "",
                Specifier = specifier,
            };
            foreach (var n in names)
                edge.Names.Add(n);
            result.Edges.Add(edge);

            if (!string.IsNullOrEmpty(edge.ToFile) && !byFile.ContainsKey(edge.ToFile))
            {
                var node = new RuitkGraphNode { File = edge.ToFile };
                byFile[edge.ToFile] = node;
                result.Nodes.Add(node);
            }
        }

        return Task.FromResult(result);
    }
}
