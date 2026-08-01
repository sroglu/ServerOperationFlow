namespace PFound.ServerOperationFlow.Core
{
    using System.Threading;

    /// <summary>How a single <c>RunAsync</c> ended.</summary>
    public enum ServerOperationDisposition
    {
        /// <summary>The lifecycle ran to a terminal outcome; <see cref="ServerOperationRun{TResult}.Result"/> holds it.</summary>
        Completed,

        /// <summary>The run policy refused this run because an identical one was already in flight (single-flight).</summary>
        DuplicateSuppressed,
    }

    /// <summary>
    /// The report of one <c>RunAsync</c>. Read <see cref="Result"/> only when <see cref="Accepted"/> is true;
    /// a duplicate-suppressed run carries no result (it never ran). A cancelled run does not return a report
    /// at all — cancellation surfaces as an <c>OperationCanceledException</c>, the idiomatic .NET signal.
    /// </summary>
    public readonly struct ServerOperationRun<TResult> where TResult : IServerOperationResult
    {
        public ServerOperationDisposition Disposition { get; }
        public TResult Result { get; }

        public bool Accepted => Disposition == ServerOperationDisposition.Completed;

        ServerOperationRun(ServerOperationDisposition disposition, TResult result)
        {
            Disposition = disposition;
            Result = result;
        }

        public static ServerOperationRun<TResult> Completed(TResult result)
            => new ServerOperationRun<TResult>(ServerOperationDisposition.Completed, result);

        public static ServerOperationRun<TResult> Suppressed()
            => new ServerOperationRun<TResult>(ServerOperationDisposition.DuplicateSuppressed, default);
    }

    /// <summary>
    /// The injected seams for an operation, gathered in one object so a concrete task's constructor stays
    /// small. The transport and the failure presenter are required (a task with neither cannot run and would
    /// fail-fast). The success sink and analytics default to no-ops, and the run policy defaults to the
    /// no-gate <see cref="ServerOperationGate.Disabled"/> — a task opts into any of these by setting them.
    /// </summary>
    public sealed class ServerOperationContext<TRequest, TResponse, TResult>
        where TResult : IServerOperationResult
    {
        public IServerOperationTransport<TRequest, TResponse> Transport { get; }
        public IServerOperationFailurePresenter<TResult> FailurePresenter { get; }
        public IServerOperationSuccessSink<TResult> SuccessSink { get; set; } = new SilentSuccessSink<TResult>();
        public IServerOperationAnalytics Analytics { get; set; } = new SilentAnalytics();
        public ServerOperationGate Gate { get; set; } = ServerOperationGate.Disabled;

        /// <summary>Ambient cancellation a RunAsync observes when its caller passes no explicit token (default: never cancels).</summary>
        public CancellationToken Cancellation { get; set; }

        public ServerOperationContext(
            IServerOperationTransport<TRequest, TResponse> transport,
            IServerOperationFailurePresenter<TResult> failurePresenter)
        {
            Transport = transport;
            FailurePresenter = failurePresenter;
        }
    }
}
