#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using Ruitk.Elements;
using Ruitk.Props.Typed;

namespace Ruitk.Samples.UITKX.Editor
{
    /// <summary>
    /// Reproduces UB-179 and proves the guards that fix it, in a form the reader
    /// can run repeatedly and watch.
    ///
    /// THE CLAIM. Time-sliced rendering is on by default, so a render can yield
    /// mid-tree, leaving a slice sitting in the scheduler queue that still points
    /// at the work-in-progress tree. If the root is unmounted before that slice
    /// runs -- a RootRenderer destroyed, or a ListView/TreeView recycling a pooled
    /// row renderer -- the queued slice used to resume against a tree that had
    /// just been deleted, and threw a NullReferenceException from inside the
    /// unmount path itself.
    ///
    /// THE SCENARIO is driven, not raced: TimeSliceMs is set to 0 so a slice
    /// yields after exactly one unit of work, and the scheduler is a manual queue
    /// that only advances when this window says so. Every run therefore produces
    /// the same sequence, and the mid-render precondition is ASSERTED rather than
    /// hoped for -- if the render is not actually in flight when the unmount
    /// lands, the run reports itself vacuous instead of passing.
    /// </summary>
    public sealed class EditorUitkxUnmountMidRenderDemoWindow : EditorWindow
    {
        [MenuItem("Reactive UI Toolkit/Demos/Tests-(Core-Fixes)/Unmount Mid-Render (UB-179)")]
        public static void ShowWindow()
        {
            var window = GetWindow<EditorUitkxUnmountMidRenderDemoWindow>("Unmount Mid-Render");
            window.minSize = new Vector2(720, 520);
            window.Show();
        }

        private ScrollView _log;
        private Label _verdict;

        /// <summary>A scheduler that advances only when this window pumps it, so
        /// "a slice is still queued" is a fact the run establishes rather than a
        /// timing window it hopes to hit.</summary>
        private sealed class ManualScheduler : IScheduler
        {
            private readonly Queue<Action> _queue = new Queue<Action>();
            private readonly List<Action> _effects = new List<Action>();

            public int Pending => _queue.Count;

            public void Enqueue(Action action, IScheduler.Priority priority = IScheduler.Priority.Normal)
            {
                if (action != null)
                    _queue.Enqueue(action);
            }

            public void EnqueueBatchedEffect(Action effect)
            {
                if (effect != null)
                    _effects.Add(effect);
            }

            public void BeginBatch() { }

            public void EndBatch() { }

            public void PumpNow() => DrainAll();

            /// <summary>Runs exactly one queued slice. Anything that slice
            /// schedules lands behind it, which is what makes a partial render
            /// observable.</summary>
            public bool Step()
            {
                if (_queue.Count == 0)
                    return false;
                _queue.Dequeue()?.Invoke();
                return true;
            }

            public void DrainAll()
            {
                int guard = 0;
                while (_queue.Count > 0 && guard++ < 10000)
                    _queue.Dequeue()?.Invoke();
                for (int i = 0; i < _effects.Count; i++)
                    _effects[i]?.Invoke();
                _effects.Clear();
            }
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 12;
            root.style.paddingRight = 12;
            root.style.paddingTop = 10;

            root.Add(Title("Unmount mid-render (UB-179)"));
            root.Add(Body(
                "Renders a tree large enough to yield, unmounts the root while a slice is "
                + "still queued, then lets that slice run. The scheduler is manual and the "
                + "time slice is 0 ms, so the sequence is identical on every run."));

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 10 } };
            row.Add(Button("1.  Run against the core as it ships", () => Run(bypassGuard: false)));
            row.Add(Button("2.  Run the pre-fix code path", () => Run(bypassGuard: true)));
            row.Add(Button("Clear", () => _log.Clear()));
            root.Add(row);

            _verdict = new Label(" ")
            {
                style =
                {
                    marginTop = 8, marginBottom = 4, fontSize = 13,
                    unityFontStyleAndWeight = FontStyle.Bold,
                },
            };
            root.Add(_verdict);

            _log = new ScrollView
            {
                style =
                {
                    flexGrow = 1, marginTop = 4, marginBottom = 10,
                    backgroundColor = new Color(0.13f, 0.13f, 0.15f),
                    paddingLeft = 8, paddingTop = 6, paddingBottom = 6,
                },
            };
            root.Add(_log);

            root.Add(Body(
                "Button 2 reaches past ProcessWorkUntilDeadline and calls PerformUnitOfWork "
                + "directly -- the loop the queued slice used to run before the guard existed. "
                + "It is the same core methods with the guard skipped, not an older binary."));
        }

        private static Label Title(string t) => new Label(t)
        {
            style = { fontSize = 15, unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4 },
        };

        private static Label Body(string t)
        {
            var l = new Label(t)
            {
                style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, marginBottom = 2 },
            };
            l.style.color = new Color(0.62f, 0.62f, 0.68f);
            return l;
        }

        private static Button Button(string text, Action onClick) => new Button(onClick)
        {
            text = text,
            style = { height = 26, marginRight = 6, paddingLeft = 10, paddingRight = 10 },
        };

        private void Say(string text, Color color)
        {
            var l = new Label(text)
            {
                style = { whiteSpace = WhiteSpace.Normal, fontSize = 12, marginBottom = 1 },
            };
            l.style.color = color;
            _log.Add(l);
        }

        private static readonly Color Ink = new Color(0.82f, 0.82f, 0.88f);
        private static readonly Color Good = new Color(0.45f, 0.82f, 0.5f);
        private static readonly Color Bad = new Color(0.94f, 0.45f, 0.42f);
        private static readonly Color Note = new Color(0.55f, 0.72f, 0.95f);

        private void Step(string s) => Say("   " + s, Ink);
        private void Pass(string s) => Say("   PASS  " + s, Good);
        private void Fail(string s) => Say("   FAIL  " + s, Bad);

        // ── the component under test ────────────────────────────────────────

        /// <summary>Wide enough that a 0 ms slice cannot finish it in one go.</summary>
        private static VirtualNode BigTree(IProps props, IReadOnlyList<VirtualNode> children)
        {
            var rows = new VirtualNode[NodeCount];
            for (int i = 0; i < NodeCount; i++)
                rows[i] = V.Label(new LabelProps { Text = "row " + i });
            return V.Box(null, null, rows);
        }

        private const int NodeCount = 400;

        // ── reflection helpers ──────────────────────────────────────────────

        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static object FindReconciler(object graph)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<object>();
            stack.Push(graph);
            while (stack.Count > 0)
            {
                object cur = stack.Pop();
                if (cur == null || !seen.Add(cur))
                    continue;
                if (cur is FiberReconciler)
                    return cur;
                foreach (var f in cur.GetType().GetFields(Any))
                {
                    if (f.FieldType.IsPrimitive || f.FieldType == typeof(string))
                        continue;
                    object v;
                    try { v = f.GetValue(cur); } catch { continue; }
                    if (v != null && v.GetType().Assembly == typeof(FiberReconciler).Assembly)
                        stack.Push(v);
                }
            }
            return null;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        private static object Field(object o, string name) =>
            o.GetType().GetField(name, Any)?.GetValue(o);

        private static void SetField(object o, string name, object value) =>
            o.GetType().GetField(name, Any)?.SetValue(o, value);

        // ── the run ─────────────────────────────────────────────────────────

        private void Run(bool bypassGuard)
        {
            _log.Clear();
            Say(bypassGuard
                ? "PRE-FIX PATH  — the guard is skipped on purpose"
                : "AS SHIPPED  — the guard is in place", Note);
            Say("", Ink);

            float savedSlice = FiberConfig.TimeSliceMs;
            bool savedSlicing = FiberConfig.TimeSlicingEnabled;
            var host = new VisualElement();
            VNodeHostRenderer renderer = null;

            try
            {
                var scheduler = new ManualScheduler();
                var ctx = RuitkBootstrap.CreateHostContext(
                    ElementRegistryProvider.GetDefaultRegistry(), null, scheduler, true);

                // AFTER CreateHostContext, never before: it ends in
                // ApplyGlobalConfig(), which overwrites both knobs from the
                // project settings. Setting them first is why the first cut of
                // this demo rendered synchronously and reported itself vacuous.
                FiberConfig.TimeSlicingEnabled = true;
                FiberConfig.TimeSliceMs = 0f;
                Step($"forced time slicing on, slice budget {FiberConfig.TimeSliceMs} ms "
                     + $"(project default was restored by CreateHostContext, then overridden)");

                renderer = new VNodeHostRenderer(ctx, host);
                renderer.Render(V.Func(BigTree));

                object rec = FindReconciler(renderer);
                if (rec == null)
                {
                    Fail("could not reach the FiberReconciler — the demo needs updating");
                    Verdict("INCONCLUSIVE", Bad);
                    return;
                }

                bool hasScheduler = Field(rec, "_scheduler") != null;
                Step($"rendered a {NodeCount}-node tree; slices queued: {scheduler.Pending}; "
                     + $"reconciler holds a scheduler: {hasScheduler}");

                if (scheduler.Pending == 0)
                {
                    Fail(hasScheduler
                        ? "nothing was queued even though the reconciler has a scheduler — "
                          + "the render took the synchronous WorkLoop path"
                        : "the reconciler has NO scheduler, so it rendered synchronously — "
                          + "the host context did not carry it through");
                    Verdict("VACUOUS — the async path was never taken", Bad);
                    return;
                }

                scheduler.Step();
                Step("ran one slice, then stopped");

                object inFlight = Field(rec, "_nextUnitOfWork");
                object wipRoot = Field(rec, "_workInProgressRoot");
                if (inFlight == null)
                {
                    Fail($"the render finished inside one slice at {FiberConfig.TimeSliceMs} ms "
                         + $"over {NodeCount} nodes — nothing was in flight, so this run proves "
                         + "nothing. Raise NodeCount and try again.");
                    Verdict("VACUOUS — precondition not met", Bad);
                    return;
                }
                Pass("a render IS in flight: _nextUnitOfWork is set and a slice is queued");

                renderer.Unmount();
                Step("unmounted the root — this is the ListView-recycle / teardown moment");

                if (!bypassGuard)
                {
                    object afterUnmount = Field(rec, "_nextUnitOfWork");
                    if (afterUnmount == null)
                        Pass("guard 1 (UnmountRoot): in-flight work was dropped with the tree");
                    else
                        Fail("guard 1 did NOT clear _nextUnitOfWork");

                    try
                    {
                        scheduler.DrainAll();
                        Pass("guard 2 (ProcessWorkUntilDeadline): the queued slice ran and "
                             + "returned without touching the deleted tree");
                        Say("", Ink);
                        Verdict("FIXED — the queued slice is harmless after teardown", Good);
                    }
                    catch (Exception ex)
                    {
                        Fail("the queued slice threw: " + ex.GetType().Name + " — " + ex.Message);
                        Say("", Ink);
                        Verdict("STILL BROKEN — the guards did not hold", Bad);
                    }
                    return;
                }

                // Pre-fix reconstruction. The fix now clears these on unmount, so
                // put the reconciler back into the exact state the old code left
                // it in, then run the loop the old slice ran.
                SetField(rec, "_nextUnitOfWork", inFlight);
                SetField(rec, "_workInProgressRoot", wipRoot);
                Step("restored the stale in-flight pointers the old UnmountRoot left behind");

                var perform = rec.GetType().GetMethod("PerformUnitOfWork", Any);
                if (perform == null)
                {
                    Fail("PerformUnitOfWork not found — the demo needs updating");
                    Verdict("INCONCLUSIVE", Bad);
                    return;
                }

                try
                {
                    object next = inFlight;
                    int units = 0;
                    while (next != null && units++ < NodeCount * 4)
                        next = perform.Invoke(rec, new[] { next });

                    Fail("the old loop completed without throwing after " + units + " units");
                    Say("", Ink);
                    Verdict(
                        "NOT REPRODUCED — the guards may be defending against something else",
                        Bad);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    Pass("the pre-fix loop threw, walking the deleted tree:");
                    Say("           " + inner.GetType().Name + ": " + inner.Message, Bad);
                    string frame = FirstCoreFrame(inner);
                    if (frame != null)
                        Say("           at " + frame, Bad);
                    Say("", Ink);
                    Verdict("REPRODUCED — this is what the guards prevent", Good);
                }
            }
            catch (Exception ex)
            {
                Fail("the harness itself failed: " + ex.GetType().Name + " — " + ex.Message);
                Verdict("INCONCLUSIVE", Bad);
            }
            finally
            {
                FiberConfig.TimeSliceMs = savedSlice;
                FiberConfig.TimeSlicingEnabled = savedSlicing;
                try { renderer?.Unmount(); } catch { }
            }
        }

        private static string FirstCoreFrame(Exception ex)
        {
            string trace = ex.StackTrace ?? "";
            foreach (string line in trace.Split('\n'))
            {
                string t = line.Trim();
                if (t.Contains("Ruitk.Core"))
                    return t.Length > 120 ? t.Substring(0, 120) : t;
            }
            return null;
        }

        private void Verdict(string text, Color color)
        {
            _verdict.text = text;
            _verdict.style.color = color;
        }
    }
}
#endif
