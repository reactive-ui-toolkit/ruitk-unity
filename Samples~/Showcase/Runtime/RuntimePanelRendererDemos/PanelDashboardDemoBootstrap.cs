using Ruitk.Core;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Ruitk.Samples.Components.PanelRendererDemos.PanelDashboardFunc;
using UnityEngine.UIElements;
#endif

namespace Ruitk.Samples.Showcase.Runtime
{
    /// <summary>
    /// Sample 5 - a real screen on the new host (Unity 6.5+): router
    /// navigation, a signal-driven readout, and a virtualized ListView, all
    /// mounted through RootRenderer.Initialize(PanelRenderer). Works both as
    /// a screen overlay and on a world-space panel - the component is
    /// identical; only the PanelRenderer's own configuration differs.
    /// </summary>
    [RequireComponent(typeof(RootRenderer))]
    public class PanelDashboardDemoBootstrap : MonoBehaviour
    {
#if UNITY_6000_5_OR_NEWER
        [SerializeField]
        private PanelRenderer panelRenderer;

        private void Awake()
        {
            var rootRenderer = GetComponent<RootRenderer>();
            if (panelRenderer == null)
            {
                panelRenderer = GetComponent<PanelRenderer>();
            }
            if (panelRenderer == null)
            {
                Debug.LogError(
                    "PanelDashboardDemoBootstrap: assign a PanelRenderer in the Inspector.",
                    this
                );
                return;
            }
            rootRenderer.Initialize(panelRenderer);
            rootRenderer.Render(V.Func(PanelDashboardFunc.Render));
        }
#else
        private void Awake()
        {
            Debug.LogWarning(
                "PanelDashboardDemoBootstrap: the PanelRenderer host requires Unity 6000.5 or newer.",
                this
            );
        }
#endif
    }
}
