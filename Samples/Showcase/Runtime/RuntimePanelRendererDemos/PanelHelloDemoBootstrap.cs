using Ruitk.Core;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Ruitk.Samples.Components.PanelRendererDemos.PanelHelloFunc;
using UnityEngine.UIElements;
#endif

namespace Ruitk.Samples.Showcase.Runtime
{
    /// <summary>
    /// Sample 1 - Hello PanelRenderer (Unity 6.5+). Scene setup: one GameObject
    /// carrying a PanelRenderer (assign its Panel Settings, leave Source Asset
    /// empty), this bootstrap, and a RootRenderer. The panel is built by Unity
    /// a frame after enable, so the Render call below runs before a root
    /// exists - the deferred-mount path holds it and replays automatically.
    /// </summary>
    [RequireComponent(typeof(RootRenderer))]
    public class PanelHelloDemoBootstrap : MonoBehaviour
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
                    "PanelHelloDemoBootstrap: assign a PanelRenderer in the Inspector.",
                    this
                );
                return;
            }
            rootRenderer.Initialize(panelRenderer);
            rootRenderer.Render(V.Func(PanelHelloFunc.Render));
        }
#else
        private void Awake()
        {
            Debug.LogWarning(
                "PanelHelloDemoBootstrap: the PanelRenderer host requires Unity 6000.5 or newer.",
                this
            );
        }
#endif
    }
}
