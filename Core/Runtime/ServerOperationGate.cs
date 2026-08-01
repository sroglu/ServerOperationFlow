namespace PFound.ServerOperationFlow.Core
{
    using System.Collections.Generic;

    /// <summary>
    /// The opt-in run policy. A task that supplies a gate gets two behaviours; a task that supplies none
    /// (<see cref="Disabled"/>) runs with no gate at all, so the plain path stays clean.
    ///
    /// 1. Single-flight / duplicate-guard — while one run holding a given key is in flight, a second run of
    ///    the same key is refused (its <see cref="TryEnter"/> returns false), so a double-tap fires one
    ///    request, not two. The key is released on EVERY terminal outcome (success, failure, cancellation),
    ///    so the operation can run again afterwards. The key defaults to the operation type; a task may
    ///    override it to fold in request parameters for per-target guarding (e.g. per invitation id).
    /// 2. Loading indicator — <see cref="Busy"/> is called just before the send and <see cref="Idle"/> on
    ///    the terminal of the in-flight window, driving the game-side spinner seam.
    ///
    /// One gate instance is shared across the operations that should guard against each other (the in-flight
    /// key set lives here), so a game typically creates a small number of gates and injects them.
    /// </summary>
    public class ServerOperationGate
    {
        readonly HashSet<string> _inFlight = new HashSet<string>();
        readonly IServerOperationLoadingIndicator _loading;

        public ServerOperationGate(IServerOperationLoadingIndicator loading) => _loading = loading;

        /// <summary>A gate that guards duplicates but shows no spinner.</summary>
        public static ServerOperationGate DedupOnly() => new ServerOperationGate(new SilentLoadingIndicator());

        /// <summary>A gate that guards duplicates and drives the given loading indicator.</summary>
        public static ServerOperationGate WithLoading(IServerOperationLoadingIndicator loading)
            => new ServerOperationGate(loading);

        /// <summary>The no-gate policy: every run is admitted, nothing is shown. This is the opt-out.</summary>
        public static ServerOperationGate Disabled { get; } = new DisabledServerOperationGate();

        /// <summary>Reserve the key; returns false when a run already holds it (duplicate suppressed).</summary>
        public virtual bool TryEnter(string key) => _inFlight.Add(key);

        /// <summary>Release the key so the operation can run again.</summary>
        public virtual void Exit(string key) => _inFlight.Remove(key);

        /// <summary>Show the loading indicator (called just before the send).</summary>
        public virtual void Busy() => _loading.Show();

        /// <summary>Hide the loading indicator (called on the in-flight terminal, always).</summary>
        public virtual void Idle() => _loading.Hide();

        sealed class DisabledServerOperationGate : ServerOperationGate
        {
            public DisabledServerOperationGate() : base(new SilentLoadingIndicator()) { }
            public override bool TryEnter(string key) => true;
            public override void Exit(string key) { }
            public override void Busy() { }
            public override void Idle() { }
        }
    }
}
