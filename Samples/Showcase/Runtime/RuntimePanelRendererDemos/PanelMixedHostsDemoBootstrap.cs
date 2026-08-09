using Ruitk.Core;
using UnityEngine;
using UnityEngine.UIElements;
#if UNITY_6000_5_OR_NEWER
using Ruitk.Samples.Components.PanelRendererDemos.PanelMixedHostsFunc;
#endif

namespace Ruitk.Samples.Showcase.Runtime
{
    /// <summary>
    /// Sample 4 - mixed hosts (Unity 6.5+): a UIDocument and a PanelRenderer
    /// in one scene, one RootRenderer per host, sharing state through a
    /// signal. Scene setup: GameObject A with a UIDocument + RootRenderer;
    /// GameObject B with a PanelRenderer + RootRenderer; this bootstrap
    /// anywhere with all four fields assigned. Clicking the counter in either
    /// panel updates both - the signal store is process-wide, host boundaries
    /// included.
    /// </summary>
    public class PanelMixedHostsDemoBootstrap : MonoBehaviour
    {
#if UNITY_6000_5_OR_NEWER
        [SerializeField]
        private UIDocument uiDocument;

        [SerializeField]
        private PanelRenderer panelRenderer;

        [SerializeField]
        private RootRenderer uiDocumentMount;

        [SerializeField]
        private RootRenderer panelRendererMount;

        private void Awake()
        {
            if (
                uiDocument == null
                || panelRenderer == null
                || uiDocumentMount == null
                || panelRendererMount == null
            )
            {
                Debug.LogError(
                    "PanelMixedHostsDemoBootstrap: assign the UIDocument, the PanelRenderer "
                        + "and their RootRenderers in the Inspector.",
                    this
                );
                return;
            }
            uiDocumentMount.Initialize(uiDocument);
            uiDocumentMount.Render(V.Func(PanelMixedHostsUiDocumentFunc.Render));
            panelRendererMount.Initialize(panelRenderer);
            panelRendererMount.Render(V.Func(PanelMixedHostsPanelRendererFunc.Render));
        }
#else
        private void Awake()
        {
            Debug.LogWarning(
                "PanelMixedHostsDemoBootstrap: the PanelRenderer host requires Unity 6000.5 or newer.",
                this
            );
        }
#endif
    }
}
