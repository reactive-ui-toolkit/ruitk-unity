using Ruitk.Core;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Ruitk.Samples.Components.PanelRendererDemos.PanelWorldSpaceFunc;
using UnityEngine.UIElements;
#endif

namespace Ruitk.Samples.Showcase.Runtime
{
    /// <summary>
    /// Sample 2 - a diegetic world-space panel (Unity 6.5+). Scene setup: a
    /// GameObject positioned on a surface in the 3D scene with a
    /// PanelRenderer configured for world space (Panel Settings with a
    /// world-space render mode; set worldSpaceSizeMode / worldSpaceSize /
    /// position / pivot / pivotReferenceSize on the component), plus this
    /// bootstrap and a RootRenderer. Unity owns the root transform and
    /// rewrites it every frame - the mounted UI renders into a library-owned
    /// sub-root, so nothing here fights those writes.
    /// </summary>
    [RequireComponent(typeof(RootRenderer))]
    public class PanelWorldSpaceDemoBootstrap : MonoBehaviour
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
                    "PanelWorldSpaceDemoBootstrap: assign a PanelRenderer in the Inspector.",
                    this
                );
                return;
            }
            rootRenderer.Initialize(panelRenderer);
            rootRenderer.Render(V.Func(PanelWorldSpaceFunc.Render));
        }
#else
        private void Awake()
        {
            Debug.LogWarning(
                "PanelWorldSpaceDemoBootstrap: the PanelRenderer host requires Unity 6000.5 or newer.",
                this
            );
        }
#endif
    }
}
