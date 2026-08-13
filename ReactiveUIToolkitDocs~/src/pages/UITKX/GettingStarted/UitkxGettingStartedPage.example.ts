export const UITKX_INSTALL_URL = 'https://github.com/reactive-ui-toolkit/ruitk-unity.git#dist'

export const UITKX_HELLO_WORLD_COMPONENT = `// Assets/UI/HelloWorld.uitkx
export VirtualNode HelloWorld() {
  var (count, setCount) = useState(0);

  return (
    <VisualElement>
      <Text text="Hello Ruitk" />
      <Text text={$"Count: {count}"} />
      <Button text="Increment" onClick={_ => setCount(count + 1)} />
    </VisualElement>
  );
}`

export const UITKX_HELLO_WORLD_BOOTSTRAP = `using UnityEngine;
using UnityEngine.UIElements;
using Ruitk;
using Ruitk.Core;
// File-keyed namespace: <prefix>.<folders>.<file stem> for Assets/UI/HelloWorld.uitkx
using Ruitk.Uitkx.UI.HelloWorld;

public sealed class HelloRuntime : MonoBehaviour
{
  [SerializeField] private UIDocument uiDocument;

  private RootRenderer rootRenderer;

  private void Awake()
  {
    rootRenderer = FindObjectOfType<RootRenderer>();
    if (rootRenderer == null)
    {
      rootRenderer = new GameObject("ReactiveUIRoot").AddComponent<RootRenderer>();
    }

    // Recommended: pass the UIDocument itself so the renderer survives
    // Unity 6.3's silent rootVisualElement rebuilds on Inspector redraws.
    rootRenderer.Initialize(uiDocument);

    // Legacy overload (still valid if you don't have a UIDocument, e.g.
    // mounting into a custom EditorWindow VisualElement):
    //   rootRenderer.Initialize(uiDocument.rootVisualElement);

    rootRenderer.Render(V.Func(HelloWorld.Render));
  }
}`

export const UITKX_EDITOR_BOOTSTRAP = `using UnityEditor;
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
    EditorRootRendererUtility.Mount(
      rootVisualElement,
      V.Func(HelloWorld.Render)
    );
  }
}`
