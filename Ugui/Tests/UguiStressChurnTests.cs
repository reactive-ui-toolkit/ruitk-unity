using System.Collections.Generic;
using NUnit.Framework;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using Ruitk.Elements;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_6000_5_OR_NEWER
using StableObjectId = UnityEngine.EntityId;
#else
using StableObjectId = System.Int32;
#endif

namespace Ruitk.Ugui.Tests
{
    /// <summary>
    /// The automated counterpart of Samples/Components/StressTest for the
    /// uGUI backend: many host elements, many re-render cycles with moving
    /// positions and churning membership. Asserts structural correctness
    /// after every cycle — the reconciler and pool must stay coherent under
    /// sustained load, not just in single-step scenarios.
    /// </summary>
    public class UguiStressChurnTests
    {
        private const int BoxCount = 300;
        private const int Cycles = 20;

        private static StableObjectId StableId(GameObject go)
        {
#if UNITY_6000_5_OR_NEWER
            return go.GetEntityId();
#else
            return go.GetInstanceID();
#endif
        }

        private GameObject _canvasGo;
        private RectTransform _mountRect;
        private FiberRenderer _renderer;

        [SetUp]
        public void SetUp()
        {
            _canvasGo = new GameObject("StressCanvas", typeof(Canvas));
            var mount = new GameObject("Mount", typeof(RectTransform));
            _mountRect = (RectTransform)mount.transform;
            _mountRect.SetParent(_canvasGo.transform, false);
        }

        /// <summary>
        /// Builds the renderer under an explicit <c>host_node_pool</c> setting (the knob is
        /// read once in the UguiHostConfig constructor, so it must be seeded BEFORE
        /// construction). Explicit seeding keeps these tests deterministic regardless of any
        /// settings store the host project carries.
        /// </summary>
        private void CreateRenderer(bool hostNodePool)
        {
            Ruitk.Core.Config.RuitkSettings.SetActive(
                Ruitk.Core.Config.RuitkSettings.Parse(
                    "{ \"host_node_pool\": " + (hostNodePool ? "true" : "false") + " }"
                )
            );
            var context = new HostContext(
                ElementRegistryProvider.GetDefaultRegistry(),
                new UguiHostConfig(UguiElementRegistryProvider.GetDefaultRegistry())
            );
            _renderer = new FiberRenderer((object)_mountRect.gameObject, context);
        }

        [TearDown]
        public void TearDown()
        {
            FiberConfig.StrictModeEnabled = false;
            _renderer?.Clear();
            Ruitk.Core.Config.RuitkSettings.SetActive(null);
            Ruitk.Core.Config.RuitkSettings.Invalidate();
            if (_canvasGo != null)
                Object.DestroyImmediate(_canvasGo);
            var staging = GameObject.Find("Ruitk.Ugui.Staging");
            if (staging != null)
                Object.DestroyImmediate(staging);
        }

        private static VirtualNode BoxField(int count, float t)
        {
            var children = new VirtualNode[count + 1];
            var status = UguiBaseProps.__Rent<UguiTextProps>();
            status.Text = $"boxes: {count} t: {t:F2}";
            children[0] = U.Text(status, "status");

            for (int i = 0; i < count; i++)
            {
                var box = UguiBaseProps.__Rent<UguiImageProps>();
                box.Anchors = UguiAnchorPreset.BottomLeft;
                box.SizeDelta = new Vector2(16f, 16f);
                box.AnchoredPosition = new Vector2(
                    Mathf.Abs(Mathf.Sin(t + i * 0.37f)) * 400f,
                    Mathf.Abs(Mathf.Cos(t * 1.3f + i * 0.11f)) * 300f
                );
                box.Color = new Color(0.2f, 0.4f + (i % 5) * 0.1f, 0.9f, 1f);
                children[i + 1] = U.Image(box, $"box-{i}");
            }

            var area = UguiBaseProps.__Rent<UguiPanelProps>();
            return U.Panel(area, "area", children);
        }

        [Test]
        public void StressLoop_MovingBoxes_StructureStaysCoherent()
        {
            CreateRenderer(hostNodePool: true);
            for (int cycle = 0; cycle < Cycles; cycle++)
            {
                float t = cycle * 0.16f;
                _renderer.Render(BoxField(BoxCount, t));

                var area = _mountRect.GetChild(0);
                Assert.AreEqual(BoxCount + 1, area.childCount, $"cycle {cycle}");
                Assert.AreEqual(
                    $"boxes: {BoxCount} t: {t:F2}",
                    area.GetChild(0).GetComponent<TextMeshProUGUI>().text,
                    $"cycle {cycle}"
                );
            }

            var lastArea = _mountRect.GetChild(0);
            var sampleRt = (RectTransform)lastArea.GetChild(1).transform;
            Assert.AreNotEqual(Vector2.zero, sampleRt.anchoredPosition);
        }

        [Test]
        public void StressLoop_ChurningMembership_ReusesPooledVisuals()
        {
            // This test ASSUMES pooling — it is the host_node_pool=true half of the pair
            // (see StressLoop_ChurningMembership_PoolDisabled_CreatesFreshVisuals).
            CreateRenderer(hostNodePool: true);
            var seenBoxes = new HashSet<StableObjectId>();

            for (int cycle = 0; cycle < Cycles; cycle++)
            {
                // Membership oscillates so unmounted boxes flow through the
                // pool and come back on later cycles. The churn amplitude
                // (100) stays under the per-adapter pool capacity (128) so
                // every unmounted box is poolable — the reuse bound below is
                // then exact, not capacity-diluted.
                int count = cycle % 2 == 0 ? BoxCount : BoxCount - 100;
                _renderer.Render(BoxField(count, cycle * 0.25f));

                var area = _mountRect.GetChild(0);
                Assert.AreEqual(count + 1, area.childCount, $"cycle {cycle}");
                for (int i = 1; i < area.childCount; i++)
                {
                    seenBoxes.Add(StableId(area.GetChild(i).gameObject));
                }
            }

            // Pooling bound: with churn amplitude under the pool capacity,
            // every removed box returns to the pool and comes back — distinct
            // Image instances across the whole run must stay at peak-live
            // plus a small slack (the no-pooling worst case would be
            // Cycles/2 * 100 extra instances).
            Assert.LessOrEqual(
                seenBoxes.Count,
                BoxCount + 50,
                "membership churn must reuse pooled instances instead of leaking new ones"
            );
        }

        [Test]
        public void StressLoop_ChurningMembership_PoolDisabled_CreatesFreshVisuals()
        {
            // host_node_pool=false: unmounted elements are destroyed, never pooled, so every
            // re-add cycle creates fresh instances — the distinct-instance count must EXCEED
            // the pooled bound, proving the knob actually disables reuse. Structure must stay
            // coherent on every cycle regardless.
            CreateRenderer(hostNodePool: false);
            var seenBoxes = new HashSet<StableObjectId>();

            for (int cycle = 0; cycle < Cycles; cycle++)
            {
                int count = cycle % 2 == 0 ? BoxCount : BoxCount - 100;
                _renderer.Render(BoxField(count, cycle * 0.25f));

                var area = _mountRect.GetChild(0);
                Assert.AreEqual(count + 1, area.childCount, $"cycle {cycle}");
                for (int i = 1; i < area.childCount; i++)
                {
                    seenBoxes.Add(StableId(area.GetChild(i).gameObject));
                }
            }

            // 9 grow cycles (2,4,…,18) each re-create their 100 boxes from scratch.
            Assert.GreaterOrEqual(
                seenBoxes.Count,
                BoxCount + 900,
                "with the pool disabled, every re-added box must be a fresh instance"
            );
        }

        // ── strict_mode interaction (M4/U-07) ────────────────────────────────

        /// <summary>Typed props for the strict-mode churn component (structural equality).</summary>
        private sealed class CycleProps : Ruitk.Core.IProps
        {
            public int Count;
            public float T;

            public override bool Equals(object obj) =>
                obj is CycleProps other && other.Count == Count && other.T == T;

            public override int GetHashCode() => Count ^ T.GetHashCode();
        }

        private static VirtualNode BoxFieldComponent(
            Ruitk.Core.IProps props,
            IReadOnlyList<VirtualNode> children
        )
        {
            var p = (CycleProps)props;
            return BoxField(p.Count, p.T);
        }

        [Test]
        public void StressLoop_StrictMode_FunctionComponentChurn_PoolStaysCoherent()
        {
            // U-07 discarded-tree rule under sustained load: with strict_mode on, every
            // pass renders the whole box field TWICE — the first tree (and its rented
            // props) is discarded garbage, the second is reconciled. Structure must stay
            // coherent every cycle and the HOST pool reuse bound must hold exactly as in
            // the strict-off pooled test (the discarded tree owns no host elements).
            CreateRenderer(hostNodePool: true);
            FiberConfig.StrictModeEnabled = true; // restored in TearDown
            var seenBoxes = new HashSet<StableObjectId>();

            for (int cycle = 0; cycle < Cycles; cycle++)
            {
                int count = cycle % 2 == 0 ? BoxCount : BoxCount - 100;
                float t = cycle * 0.25f;
                _renderer.Render(
                    V.Func(BoxFieldComponent, new CycleProps { Count = count, T = t })
                );

                var area = _mountRect.GetChild(0);
                Assert.AreEqual(count + 1, area.childCount, $"cycle {cycle}");
                Assert.AreEqual(
                    $"boxes: {count} t: {t:F2}",
                    area.GetChild(0).GetComponent<TextMeshProUGUI>().text,
                    $"cycle {cycle}"
                );
                for (int i = 1; i < area.childCount; i++)
                {
                    seenBoxes.Add(StableId(area.GetChild(i).gameObject));
                }
            }

            Assert.LessOrEqual(
                seenBoxes.Count,
                BoxCount + 50,
                "strict-mode double-invoke must not disturb pooled host reuse"
            );
        }
    }
}
