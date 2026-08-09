using System;
using System.Collections.Generic;
using Ruitk;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using Ruitk.Elements;
using Ruitk.Shared.Tests.Fiber;
using Ruitk.Signals;
using Xunit;

namespace Ruitk.Shared.Tests
{
    // The signals runtime: Signal<T> semantics, the registry, and the UseSignal
    // fiber integration. The registry behind SignalFactory is a process-global
    // static, so every test uses a key unique to itself.
    public class SignalTests
    {
        private static string Key(string name) => "SignalTests." + name;

        // ── Registry ────────────────────────────────────────────────────────

        [Fact]
        public void GetOrCreateReturnsTheSameInstanceForTheSameKey()
        {
            var a = SignalFactory.Get(Key("same-instance"), 1);
            var b = SignalFactory.Get(Key("same-instance"), 999);

            Assert.Same(a, b);
            Assert.Equal(1, b.Value);
        }

        [Fact]
        public void GetOrCreateThrowsWhenTheKeyExistsWithADifferentType()
        {
            SignalFactory.Get(Key("type-conflict"), 1);

            Assert.Throws<InvalidOperationException>(
                () => SignalFactory.Get(Key("type-conflict"), "text")
            );
        }

        [Fact]
        public void GetOrCreateRejectsAnEmptyKey()
        {
            Assert.Throws<ArgumentException>(() => SignalFactory.Get<int>(""));
            Assert.Throws<ArgumentException>(() => SignalFactory.Get<int>(null));
        }

        [Fact]
        public void TryGetFindsAnExistingSignalAndRejectsAWrongType()
        {
            SignalFactory.Get(Key("try-get"), 5);

            Assert.True(SignalFactory.TryGet(Key("try-get"), out Signal<int> found));
            Assert.Equal(5, found.Value);
            Assert.False(SignalFactory.TryGet(Key("try-get"), out Signal<string> _));
            Assert.False(SignalFactory.TryGet(Key("try-get-missing"), out Signal<int> _));
        }

        // ── Signal semantics ────────────────────────────────────────────────

        [Fact]
        public void SetNotifiesSubscribersWithTheNewValue()
        {
            var signal = SignalFactory.Get(Key("set-notifies"), 0);
            var seen = new List<int>();
            signal.Subscribe(seen.Add);

            signal.Set(1);
            signal.Set(2);

            Assert.Equal(new[] { 1, 2 }, seen);
            Assert.Equal(2, signal.Value);
        }

        [Fact]
        public void SettingAnEqualValueDoesNotNotify()
        {
            var signal = SignalFactory.Get(Key("equal-bailout"), 7);
            int notifications = 0;
            signal.Subscribe(_ => notifications++);

            signal.Set(7);

            Assert.Equal(0, notifications);
        }

        [Fact]
        public void DisposingTheSubscriptionStopsNotifications()
        {
            var signal = SignalFactory.Get(Key("dispose-stops"), 0);
            int notifications = 0;
            var subscription = signal.Subscribe(_ => notifications++);

            signal.Set(1);
            subscription.Dispose();
            signal.Set(2);

            Assert.Equal(1, notifications);
        }

        [Fact]
        public void DispatchAppliesTheUpdaterToTheCurrentValue()
        {
            var signal = SignalFactory.Get(Key("dispatch-updater"), 10);

            signal.Dispatch(v => v + 5);

            Assert.Equal(15, signal.Value);
        }

        [Fact]
        public void DispatchAcceptsBothSignalUpdateForms()
        {
            var signal = SignalFactory.Get(Key("dispatch-forms"), 0);

            signal.Dispatch((SignalUpdate<int>)42);
            Assert.Equal(42, signal.Value);

            signal.Dispatch((SignalUpdate<int>)(Func<int, int>)(v => v * 2));
            Assert.Equal(84, signal.Value);
        }

        [Fact]
        public void AThrowingSubscriberIsLoggedAndDoesNotBreakOtherSubscribers()
        {
            var signal = SignalFactory.Get(Key("throwing-subscriber"), 0);
            int delivered = 0;
            signal.Subscribe(_ => throw new InvalidOperationException("listener boom"));
            signal.Subscribe(_ => delivered++);
            UnityEngine.Debug.Clear();

            signal.Set(1);

            Assert.Equal(1, delivered);
            Assert.Contains(
                UnityEngine.Debug.Entries,
                e => e.Kind == UnityEngine.LogKind.Error && e.Message.Contains("listener boom")
            );
        }

        // ── UseSignal fiber integration ─────────────────────────────────────

        private static VirtualNode El(string type, Dictionary<string, object> props = null) =>
            new VirtualNode(VirtualNodeType.Element, type, null, null, props, null);

        private static (MockHostConfig host, MockElement container, FiberReconciler reconciler) Rig()
        {
            var host = new MockHostConfig();
            var container = new MockElement { Type = "root" };
            var context = new HostContext(new ElementRegistry(), host);
            return (host, container, new FiberReconciler(context));
        }

        [Fact]
        public void UseSignalRerendersTheComponentWhenTheSignalChanges()
        {
            var (host, container, reconciler) = Rig();
            var signal = SignalFactory.Get(Key("rerender"), 1);
            var node = V.Func(
                (props, children) =>
                {
                    int current = Hooks.UseSignal(signal);
                    return El(
                        "Label",
                        new Dictionary<string, object> { ["text"] = "v=" + current }
                    );
                }
            );

            reconciler.CreateRoot(container, node);
            var label = container.Children[0];
            Assert.Equal("v=1", label.Prop("text"));

            signal.Set(2);

            Assert.Equal("v=2", label.Prop("text"));
            Assert.Same(label, container.Children[0]);
        }

        [Fact]
        public void UseSignalWithASelectorOnlyRerendersWhenTheSliceChanges()
        {
            var (host, container, reconciler) = Rig();
            var signal = SignalFactory.Get(Key("selector"), (count: 1, label: "a"));
            int renders = 0;
            var node = V.Func(
                (props, children) =>
                {
                    renders++;
                    string slice = Hooks.UseSignal(signal, v => v.label);
                    return El("Label", new Dictionary<string, object> { ["text"] = slice });
                }
            );

            reconciler.CreateRoot(container, node);
            Assert.Equal(1, renders);

            signal.Set((count: 2, label: "a"));
            Assert.Equal(1, renders);

            signal.Set((count: 2, label: "b"));
            Assert.Equal(2, renders);
            Assert.Equal("b", container.Children[0].Prop("text"));
        }

        [Fact]
        public void UnmountingTheComponentUnsubscribesItFromTheSignal()
        {
            var (host, container, reconciler) = Rig();
            var signal = SignalFactory.Get(Key("unmount-unsubscribes"), 0);
            int renders = 0;
            var node = V.Func(
                (props, children) =>
                {
                    renders++;
                    Hooks.UseSignal(signal);
                    return El("Label");
                }
            );

            reconciler.CreateRoot(container, node);
            Assert.Equal(1, renders);

            reconciler.UnmountRoot();
            signal.Set(99);

            Assert.Equal(1, renders);
        }
    }
}
