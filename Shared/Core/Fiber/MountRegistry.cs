#if UNITY_EDITOR
using System.Collections.Generic;

namespace Ruitk.Core.Fiber
{
    /// <summary>
    /// Editor-only: the single registry of live fiber mounts.
    /// <see cref="FiberRenderer"/> self-registers on construction and
    /// unregisters on <see cref="FiberRenderer.Clear"/>, so every host - the
    /// runtime <c>RootRenderer</c>, editor windows, uGUI mounts, virtualized
    /// row renderers, and any future host - is visible to HMR without a
    /// hard-coded per-host list. Before this existed the host list was
    /// duplicated in <c>UitkxHmrController</c> and
    /// <c>UitkxHmrDelegateSwapper</c> and every new host was a manual leg in
    /// both.
    ///
    /// Weak references: a renderer abandoned without <c>Clear()</c> must not
    /// be kept alive (it anchors its whole fiber tree); dead entries are
    /// pruned on enumeration.
    /// </summary>
    internal static class MountRegistry
    {
        private static readonly List<System.WeakReference<FiberRenderer>> s_live = new();

        internal static void Register(FiberRenderer renderer)
        {
            if (renderer == null)
            {
                return;
            }
            s_live.Add(new System.WeakReference<FiberRenderer>(renderer));
        }

        internal static void Unregister(FiberRenderer renderer)
        {
            if (renderer == null)
            {
                return;
            }
            for (int i = s_live.Count - 1; i >= 0; i--)
            {
                if (!s_live[i].TryGetTarget(out var target) || ReferenceEquals(target, renderer))
                {
                    s_live.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Every live mount's current root fiber. The single source both HMR
        /// walkers (root-fiber enumeration and global re-render) iterate.
        /// </summary>
        internal static IEnumerable<FiberNode> EnumerateRootFibers()
        {
            for (int i = s_live.Count - 1; i >= 0; i--)
            {
                if (!s_live[i].TryGetTarget(out var renderer))
                {
                    s_live.RemoveAt(i);
                    continue;
                }
                var current = renderer.Root?.Current;
                if (current != null)
                {
                    yield return current;
                }
            }
        }
    }
}
#endif
