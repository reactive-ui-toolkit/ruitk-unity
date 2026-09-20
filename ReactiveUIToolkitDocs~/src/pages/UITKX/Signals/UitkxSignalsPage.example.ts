export const UITKX_SIGNALS_COMPONENT_EXAMPLE = `import "@Ruitk.Signals"

export VirtualNode SignalCounterDemo() {
  var counterSignal = useMemo(() => SignalFactory.Get<int>("demo.counter", 0), Array.Empty<object>());
  var count = useSignal(counterSignal);

  return (
    <VisualElement>
      <Text text="Signal Counter" />
      <Text text={$"Count: {count}"} />
      <VisualElement style={new Style { (StyleKeys.FlexDirection, "row") }}>
        <Button text="Increment" onClick={_ => counterSignal.Dispatch(v => v + 1)} />
        <Button text="Reset" onClick={_ => counterSignal.Dispatch(0)} />
      </VisualElement>
    </VisualElement>
  );
}`

export const UITKX_SIGNALS_RUNTIME_EXAMPLE = `using Ruitk.Signals;

SignalsRuntime.EnsureInitialized();
var counter = SignalFactory.Get<int>("demo.counter", 0);
counter.Dispatch(previous => previous + 1);`

export const UITKX_SIGNALS_OWNED_EXAMPLE = `using Ruitk.Signals;

// Keyed: shared by everything that asks for this key, and kept by the registry
// for the lifetime of the process.
var theme = SignalFactory.Get<string>("app.theme", "dark");

// Owner-scoped: no key, not in the registry, not reachable through TryGet.
// It is collected with whatever holds it.
public sealed class InventoryPresenter
{
    private readonly Signal<int> selected = SignalFactory.Create<int>(-1);

    public Signal<int> Selected => selected;
}`
