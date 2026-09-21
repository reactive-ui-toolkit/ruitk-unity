using System.Runtime.CompilerServices;

// Hooks.UseSignal reads Signal<T>'s internals -- SubscribeRaw, UntypedValue and the
// SignalBase surface behind them -- to subscribe without knowing T at the call site.
// That is the ONLY code outside this assembly that touches them: every other consumer
// in the package (FiberReconciler, RefreshRuntime, PropsHelper, RuitkBootstrap,
// EditorRootRendererUtility, UguiRootRenderer) uses the public API. One entry, not the
// six a wider reading would suggest -- each one of these is a hole in the boundary this
// assembly exists to create, so they are added only where a build actually demands it.
[assembly: InternalsVisibleTo("Ruitk.Shared")]
