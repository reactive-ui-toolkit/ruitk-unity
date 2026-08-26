// Minimal stand-ins so the REAL BuilderModule.cs and BuilderTree.cs compile and
// run outside Unity. Only the shapes the model touches: an attribute and an
// interface, both of which are pure managed declarations in UnityEngine too.
// This lets the tree's logic - indexing, orphan computation, subtree moves,
// validation - be tested in the ordinary loop. It does NOT test Unity's own
// serializer, which is why the plan keeps a real domain reload as stage 1's
// exit gate.
using System;

namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    public interface ISerializationCallbackReceiver
    {
        void OnBeforeSerialize();
        void OnAfterDeserialize();
    }
}

namespace Ruitk.Builder
{
    public enum BuilderNodeKind
    {
        Component,
        Hook,
        Style,
        Util,
        Unknown,
    }
}
