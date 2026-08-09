using System.Collections.Generic;
using System.Text;
using Ruitk.Core.Fiber;
using Ruitk.Props.Typed;

namespace Ruitk.Shared.Tests.Fiber
{
    // POCO host element. The reconciler only ever sees it as an opaque `object`
    // through the FiberHostConfig seam, which is exactly the contract under test.
    public sealed class MockElement
    {
        public string Type;
        public MockElement Parent;
        public readonly List<MockElement> Children = new List<MockElement>();
        public IReadOnlyDictionary<string, object> LastDictProps;
        public HostPropsBase LastTypedProps;
        public int DictApplyCount;
        public int TypedApplyCount;
        public bool HostRemoved;

        public object Prop(string key)
        {
            if (LastDictProps != null && LastDictProps.TryGetValue(key, out var v))
            {
                return v;
            }
            return null;
        }

        public override string ToString() => Type;
    }

    public sealed class MockHostConfig : FiberHostConfig
    {
        public readonly List<MockElement> CreatedElements = new List<MockElement>();
        public readonly List<MockElement> RemovedElements = new List<MockElement>();

        // Elements marked dead model a backend whose elements can die out from
        // under the tree (Unity 6.5 ReleaseResources poisoning, uGUI destroy).
        public readonly HashSet<MockElement> DeadElements = new HashSet<MockElement>();

        public override bool IsAlive(object element) =>
            element is MockElement e ? !DeadElements.Contains(e) : element != null;

        // Chronological log of every host mutation, for ordering assertions.
        public readonly List<string> Operations = new List<string>();

        public override object CreateElement(string elementType)
        {
            var e = new MockElement { Type = elementType ?? "element" };
            CreatedElements.Add(e);
            Operations.Add("create:" + e.Type);
            return e;
        }

        public override void ApplyProperties(
            object element,
            string elementType,
            IReadOnlyDictionary<string, object> oldProps,
            IReadOnlyDictionary<string, object> newProps
        )
        {
            var e = (MockElement)element;
            e.LastDictProps = newProps;
            e.DictApplyCount++;
            Operations.Add("applyDict:" + e.Type);
        }

        public override void ApplyTypedProperties(
            object element,
            string elementType,
            HostPropsBase oldProps,
            HostPropsBase newProps
        )
        {
            var e = (MockElement)element;
            e.LastTypedProps = newProps;
            e.TypedApplyCount++;
            Operations.Add("applyTyped:" + e.Type);
        }

        public override void AppendChild(object parent, object child)
        {
            var p = (MockElement)parent;
            var c = (MockElement)child;
            c.Parent?.Children.Remove(c);
            c.Parent = p;
            p.Children.Add(c);
            Operations.Add("append:" + c.Type + ">" + p.Type);
        }

        public override void InsertBefore(object parent, object child, object beforeChild)
        {
            var p = (MockElement)parent;
            var c = (MockElement)child;
            var before = (MockElement)beforeChild;
            c.Parent?.Children.Remove(c);
            c.Parent = p;
            int index = p.Children.IndexOf(before);
            if (index < 0)
            {
                p.Children.Add(c);
            }
            else
            {
                p.Children.Insert(index, c);
            }
            Operations.Add("insert:" + c.Type + "<" + (before != null ? before.Type : "null"));
        }

        public override void RemoveChild(object parent, object child)
        {
            var p = (MockElement)parent;
            var c = (MockElement)child;
            if (p.Children.Remove(c))
            {
                c.Parent = null;
            }
            Operations.Add("remove:" + c.Type + "<" + p.Type);
        }

        public override object GetParent(object element) => ((MockElement)element).Parent;

        public override void ClearChildren(object element)
        {
            var e = (MockElement)element;
            foreach (var c in e.Children)
            {
                c.Parent = null;
            }
            e.Children.Clear();
            Operations.Add("clear:" + e.Type);
        }

        public override int GetChildCount(object element) => ((MockElement)element).Children.Count;

        public override object GetChildAt(object element, int index) =>
            ((MockElement)element).Children[index];

        public override void OnHostRemoved(object element)
        {
            var e = (MockElement)element;
            e.HostRemoved = true;
            RemovedElements.Add(e);
            Operations.Add("hostRemoved:" + e.Type);
        }

        public override string GetDebugName(object element) => ((MockElement)element).Type;

        // "root(Box(Label,Label),Button)" - shape assertions in one string compare.
        public static string Dump(MockElement element)
        {
            var sb = new StringBuilder();
            Append(element, sb);
            return sb.ToString();
        }

        private static void Append(MockElement element, StringBuilder sb)
        {
            sb.Append(element.Type);
            if (element.Children.Count == 0)
            {
                return;
            }
            sb.Append('(');
            for (int i = 0; i < element.Children.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                Append(element.Children[i], sb);
            }
            sb.Append(')');
        }
    }
}
