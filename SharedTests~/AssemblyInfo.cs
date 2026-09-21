using Xunit;

// xUnit runs test CLASSES in parallel by default. Several of the things under test here are
// process-global by design -- the signal registry behind SignalFactory, and the
// WhyDidYouRender diagnostics seam, which any render in any class reports into. A capture
// subscribed in one class therefore sees events produced by another, and an assertion about
// "nothing is listening" is decided by whichever class happens to be mid-test.
//
// The whole suite runs in about 50 ms, so sequential execution costs nothing and removes the
// entire class of cross-test interference rather than papering over one instance of it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
