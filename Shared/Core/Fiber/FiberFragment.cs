using System.Collections.Generic;
using Ruitk.Core;

namespace Ruitk.Core.Fiber
{
    /// <summary>
    /// Fragment support - render multiple children without wrapper
    /// </summary>
    public static class FiberFragment
    {
        /// <summary>
        /// Update a fragment fiber - just reconcile children
        /// </summary>
        public static FiberNode UpdateFragment(FiberNode wipFiber)
        {
            // Fragments have no host element, just reconcile children.
            //
            // Null means "no information, keep what is there"; EMPTY is a render
            // result and must delete. The guard used to reject both, so N -> N-1
            // unmounted and N -> 0 did not (a loop over an emptied collection left
            // its last items on screen). UpdateHostComponent has always branched on
            // null alone, which is why host elements never had the bug.
            //
            // Safe only while the VNode pool stays dormant: V.Fragment() hands out a
            // shared empty list, but VirtualNode.__Reset also sets _children to one,
            // so a recycled node reaching a live fiber would read as "delete my
            // children". Nothing returns nodes to the pool today - see the note on
            // VirtualNode.__ScheduleReturn, which points back here.
            if (wipFiber.Children != null)
            {
                var currentFirstChild = wipFiber.Alternate?.Child;
                FiberChildReconciliation.ReconcileChildren(
                    wipFiber,
                    currentFirstChild,
                    wipFiber.Children
                );
            }
            else if (wipFiber.Alternate?.Child != null)
            {
                FiberFactory.CloneChildrenForBailout(wipFiber);
            }

            return wipFiber.Child;
        }

        /// <summary>
        /// Complete work for fragment - no element to create
        /// </summary>
        public static void CompleteFragment(FiberNode fiber)
        {
            // Fragments have no host element, nothing to do
            // Effects are still collected normally
        }
    }
}
