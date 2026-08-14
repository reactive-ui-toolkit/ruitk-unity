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

    /// <summary>One file card on the canvas.</summary>
    [Serializable]
    public sealed class BuilderCanvasNode
    {
        public string FilePath;
        public string Title;
        public BuilderNodeKind Kind;
        public float X;
        public float Y;
        public bool IsReadOnly;
        public List<string> Exports = new List<string>();
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
