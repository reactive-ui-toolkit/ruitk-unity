#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace Ruitk.Builder
{
    /// <summary>Node kinds the canvas renders (matches export-kind semantics).</summary>
    public enum BuilderNodeKind
    {
        Component,
        Hook,
        Style,
        Util,
        Unknown,
    }

    /// <summary>Coloring class for one line inside a card section.</summary>
    public enum BuilderCardLineKind
    {
        Plain,
        Import,
        Hook,
        Element,
        Component,
        Directive,
        Export,
    }

    /// <summary>One rendered line inside a card body section.</summary>
    [Serializable]
    public sealed class BuilderCardLine
    {
        public string Text;
        public int Depth;
        public BuilderCardLineKind Kind;

        /// <summary>Attribute display text ("text=\"hi\" style={S.x}"), L2 only.</summary>
        public string AttrsText = "";

        /// <summary>1-based line in the source file (row→source-line sync).</summary>
        public int SourceLine;

        /// <summary>1-based inclusive end line (element rows; 0 = single line).</summary>
        public int EndLine;

        /// <summary>0 none, 1 @if, 2 @foreach, 3 @else, 4 other control flow.</summary>
        public int BadgeKind;
    }

    /// <summary>One file card on the canvas.</summary>
    [Serializable]
    public sealed class BuilderCanvasNode
    {
        public string FilePath;
        public string Title;
        public string Signature;
        public BuilderNodeKind Kind;
        public float X;
        public float Y;
        public bool IsReadOnly;
        public List<string> Exports = new List<string>();
        public List<BuilderCardLine> Imports = new List<BuilderCardLine>();
        public List<BuilderCardLine> Body = new List<BuilderCardLine>();
        public List<BuilderCardLine> Markup = new List<BuilderCardLine>();

        /// <summary>Style/util export detail: header lines (Kind Export), entry
        /// lines (Plain, Depth 1, L2-only), and "+ entry" affordances
        /// (BadgeKind 9, AttrsText = the style export name).</summary>
        public List<BuilderCardLine> ExportDetail = new List<BuilderCardLine>();

        /// <summary>Setup-code lines (non-hook statements) shown as the POC's
        /// code island at L2.</summary>
        public List<string> IslandLines = new List<string>();
    }

    /// <summary>One import edge; indices into the node list (broken edges keep To = -1).</summary>
    [Serializable]
    public sealed class BuilderCanvasEdge
    {
        public int FromIndex;
        public int ToIndex;
        public string Specifier;
        public List<string> Names = new List<string>();
        public BuilderNodeKind TargetKind;
    }

    /// <summary>The canvas model for one open tree.</summary>
    [Serializable]
    public sealed class BuilderGraph
    {
        public string RootPath;
        public List<BuilderCanvasNode> Nodes = new List<BuilderCanvasNode>();
        public List<BuilderCanvasEdge> Edges = new List<BuilderCanvasEdge>();

        public int IndexOf(string filePath)
        {
            for (int i = 0; i < Nodes.Count; i++)
                if (string.Equals(Nodes[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }
    }
}
#endif
