export const EXAMPLE_UIDOCUMENT = `using UnityEngine;
using UnityEngine.UIElements;
using Ruitk;
using Ruitk.Core;
using Ruitk.Uitkx.UI.HelloWorld;

public sealed class GameUi : MonoBehaviour
{
  [SerializeField] private UIDocument uiDocument;

  private void Awake()
  {
    var rootRenderer = new GameObject("ReactiveUIRoot").AddComponent<RootRenderer>();

    // Preferred: pass the UIDocument itself. The renderer polls
    // rootVisualElement once per frame and migrates the mounted tree
    // whenever Unity rebuilds the panel root.
    rootRenderer.Initialize(uiDocument);

    rootRenderer.Render(V.Func(HelloWorld.Render));
  }
}`

export const EXAMPLE_VISUALELEMENT = `// Static root — for hosts without a UIDocument (custom EditorWindow,
// tests, bespoke panels). The element you pass IS the root; nothing is polled.
rootRenderer.Initialize(uiDocument.rootVisualElement);
rootRenderer.Render(V.Func(HelloWorld.Render));`

export const EXAMPLE_PANELRENDERER = `using UnityEngine;
using UnityEngine.UIElements;
using Ruitk;
using Ruitk.Core;
using Ruitk.Uitkx.UI.Hud;

public sealed class WorldSpaceHud : MonoBehaviour
{
  [SerializeField] private PanelRenderer panelRenderer;  // Unity 6.5+

  private void Awake()
  {
    var rootRenderer = new GameObject("ReactiveUIRoot").AddComponent<RootRenderer>();
    rootRenderer.Initialize(panelRenderer);

    // Safe to call immediately: a Render issued before the panel exists is
    // held and replayed automatically when the renderer delivers its panel.
    rootRenderer.Render(V.Func(Hud.Render));
  }
}`

export const EXAMPLE_ENV = `// The optional env callback runs once against the renderer's HostContext —
// use it to seed named portal target slots or environment values:
rootRenderer.Initialize(uiDocument,
    env: ctx => ctx.Environment[PortalContextKeys.ModalRoot] = overlayLayer);`

export const EXAMPLE_EDITOR = `using UnityEditor;
using UnityEngine.UIElements;
using Ruitk;
using Ruitk.Core;
using Ruitk.EditorSupport;
using Ruitk.Uitkx.UI.HelloWorld;

public sealed class HelloEditorWindow : EditorWindow
{
  [MenuItem("Window/Hello UITKX")]
  public static void ShowWindow() => GetWindow<HelloEditorWindow>("Hello UITKX");

  private void CreateGUI()
  {
    // Sets up the editor scheduler, signals runtime, and diagnostics.
    EditorRootRendererUtility.Mount(
      rootVisualElement,
      V.Func(HelloWorld.Render)
    );
  }
}`
