namespace PFound.ServerOperationFlow.Core
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The send-request / await-typed-response seam the lifecycle drives, kept deliberately free of any
    /// engine or messaging type so the Core builds and tests under a plain mono/csc runner with a stub.
    /// The production binding to the realtime request/reply transport lives in the adapter assembly, which
    /// is the ONLY place that constrains the request/response to concrete wire-message base types.
    /// </summary>
    public interface IServerOperationTransport<TRequest, TResponse>
    {
        /// <summary>Send the request and await the server's authoritative response.</summary>
        Task<TResponse> SendAsync(TRequest request, CancellationToken cancellation);
    }

    /// <summary>
    /// The game-supplied outcome of an operation. The client never decides success on its own — this is
    /// filled from the local pre-check (an instant reject) or from mapping the server's response. It carries
    /// just enough for the uniform lifecycle: a success flag the Core branches on, and a diagnostic message.
    /// The status/result CODE is deliberately absent here — it is game-specific (a game's own enum) and lives
    /// on the concrete result type the game supplies, so the Core never has to name an int or an enum for it.
    /// Turning a code into a localized, user-facing string stays entirely game-side (see the failure presenter
    /// seam), so the Core never references a UI or localization type either.
    /// </summary>
    public interface IServerOperationResult
    {
        bool IsSuccess { get; }

        /// <summary>Diagnostic detail for logs; NOT the user-facing message. Absent on success.</summary>
        string ErrorMessage { get; }
    }

    /// <summary>
    /// Notified once, synchronously, right after a successful apply so the app can react (e.g. raise an
    /// in-process signal). Injected only — the Core stays free of any concrete signal/event-bus dependency.
    /// </summary>
    public interface IServerOperationSuccessSink<in TResult> where TResult : IServerOperationResult
    {
        void OnSuccess(TResult result);
    }

    /// <summary>
    /// The single game-side failure destination. It maps the failed result's code to a user-facing,
    /// localized message and shows it (toast / dialog / inline). Both the local pre-check rejection and the
    /// server-reported failure funnel through here, so error handling is identical everywhere.
    /// </summary>
    public interface IServerOperationFailurePresenter<in TResult> where TResult : IServerOperationResult
    {
        void PresentFailure(TResult result);
    }

    /// <summary>Optional telemetry sink invoked on every terminal outcome (success and failure alike).</summary>
    public interface IServerOperationAnalytics
    {
        void RecordOutcome(string operationName, IServerOperationResult result);
    }

    /// <summary>
    /// The busy-indicator seam the opt-in run policy drives around the in-flight window (shown before the
    /// send, hidden on any terminal). The actual spinner/overlay UI stays game-side.
    /// </summary>
    public interface IServerOperationLoadingIndicator
    {
        void Show();
        void Hide();
    }

    /// <summary>No-op success sink for tasks that do not need a success notification.</summary>
    public sealed class SilentSuccessSink<TResult> : IServerOperationSuccessSink<TResult>
        where TResult : IServerOperationResult
    {
        public void OnSuccess(TResult result) { }
    }

    /// <summary>No-op analytics sink used when a task opts out of telemetry.</summary>
    public sealed class SilentAnalytics : IServerOperationAnalytics
    {
        public void RecordOutcome(string operationName, IServerOperationResult result) { }
    }

    /// <summary>No-op loading indicator for a run policy that guards duplicates but shows no spinner.</summary>
    public sealed class SilentLoadingIndicator : IServerOperationLoadingIndicator
    {
        public void Show() { }
        public void Hide() { }
    }
}
