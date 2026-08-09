using System;
using System.Collections.Generic;
using Ruitk.Core;
using Ruitk.Core.Diagnostics;
using Ruitk.Core.Fiber;
using Ruitk.Elements;
using Ruitk.Signals;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Ruitk.Ugui
{
    /// <summary>
    /// Mounts a fiber tree under a RectTransform inside any existing Canvas —
    /// the uGUI sibling of <see cref="RootRenderer"/>. Does not own or create
    /// the Canvas or the EventSystem: it slots into the scene structure the
    /// team already has, and validates the EventSystem at first render with a
    /// single actionable warning.
    /// </summary>
    public sealed class UguiRootRenderer : MonoBehaviour
    {
        [SerializeField]
        private RectTransform target;

        private HostContext sharedHostContext;
        private FiberRenderer fiberRenderer;
        private bool eventSystemChecked;


        private void EnsureSetup()
        {
            if (sharedHostContext != null)
            {
                return;
            }
            if (RenderScheduler.Instance == null)
            {
                var go = new GameObject("RenderScheduler");
                go.hideFlags = HideFlags.DontSave;
                go.AddComponent<RenderScheduler>();
            }
            var uguiRegistry = UguiElementRegistryProvider.GetDefaultRegistry();
            sharedHostContext = RuitkBootstrap.CreateHostContext(
                ElementRegistryProvider.GetDefaultRegistry(),
                new UguiHostConfig(uguiRegistry),
                RenderScheduler.Instance,
                isEditor: false
            );
        }

        private void Awake()
        {
            EnsureSetup();
        }

        private void OnDestroy()
        {
            Unmount();
        }

        /// <summary>
        /// Sets the mount rect and optionally seeds environment slots (portal
        /// targets, feature flags) into the <see cref="HostContext"/>. Must be
        /// called before the first <see cref="Render"/> unless the target is
        /// assigned in the Inspector or this component sits on a RectTransform.
        /// </summary>
        public void Initialize(RectTransform mountTarget, Action<HostContext> env = null)
        {
            EnsureSetup();
            target = mountTarget;
            env?.Invoke(sharedHostContext);
        }

        public void Render(VirtualNode rootNode)
        {
            EnsureSetup();
            var mount = ResolveTarget();
            if (mount == null)
            {
                Debug.LogError(
                    "[Ruitk.Ugui] UguiRootRenderer has no mount target. Assign a "
                        + "RectTransform in the Inspector, call Initialize(rectTransform), or "
                        + "place the component on a RectTransform under a Canvas.",
                    this
                );
                return;
            }
            WarnIfNoEventSystem();
            if (fiberRenderer == null)
            {
                fiberRenderer = new FiberRenderer((object)mount.gameObject, sharedHostContext);
            }
            fiberRenderer.Render(rootNode);
        }

        public void Unmount()
        {
            if (fiberRenderer != null)
            {
                fiberRenderer.Clear();
                fiberRenderer = null;
            }
        }

        private RectTransform ResolveTarget()
        {
            if (target != null)
            {
                return target;
            }
            return transform as RectTransform;
        }

        private void WarnIfNoEventSystem()
        {
            if (eventSystemChecked)
            {
                return;
            }
            eventSystemChecked = true;
            if (EventSystem.current == null)
            {
                Debug.LogWarning(
                    "[Ruitk.Ugui] No EventSystem found in the scene — uGUI "
                        + "interaction events (Button.onClick, pointer handlers) will not "
                        + "fire. Add one via GameObject > UI > Event System.",
                    this
                );
            }
        }
    }
}
