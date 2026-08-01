namespace PFound.ServerOperationFlow.Core
{
    using System.Threading;

    /// <summary>Creates the transport for a flow's request/response pair (game-supplied; unconstrained so the Core never names the wire base types).</summary>
    public interface IServerOperationTransportFactory
    {
        IServerOperationTransport<TRequest, TResponse> CreateTransport<TRequest, TResponse>();
    }

    /// <summary>
    /// The ambient host a flow resolves its context from (like <c>NetworkClient.Current</c>), generic over the
    /// game's single result type so the presenter + sink are held typed with no cast. Assign <see cref="Current"/>
    /// once at boot.
    /// </summary>
    public sealed class ServerOperationHost<TResult> where TResult : IServerOperationResult
    {
        public static ServerOperationHost<TResult> Current;

        readonly IServerOperationTransportFactory _transportFactory;
        readonly IServerOperationFailurePresenter<TResult> _failurePresenter;

        /// <summary>The success notification handed to every resolved context. Defaults to the no-op sink.</summary>
        public IServerOperationSuccessSink<TResult> SuccessSink { get; set; } = new SilentSuccessSink<TResult>();

        /// <summary>The shared run policy (single-flight + loading) handed to every resolved context.</summary>
        public ServerOperationGate Gate { get; set; } = ServerOperationGate.Disabled;

        /// <summary>The telemetry sink handed to every resolved context.</summary>
        public IServerOperationAnalytics Analytics { get; set; } = new SilentAnalytics();

        /// <summary>Ambient cancellation handed to every context — cancel from the gameloop to abort flows passing no explicit token.</summary>
        public CancellationToken Cancellation { get; set; }

        public ServerOperationHost(
            IServerOperationTransportFactory transportFactory,
            IServerOperationFailurePresenter<TResult> failurePresenter)
        {
            _transportFactory = transportFactory;
            _failurePresenter = failurePresenter;
        }

        /// <summary>Assemble the context for one flow from the ambient factory + typed presenter + shared seams.</summary>
        public ServerOperationContext<TRequest, TResponse, TResult> CreateContext<TRequest, TResponse>()
            => new ServerOperationContext<TRequest, TResponse, TResult>(
                    _transportFactory.CreateTransport<TRequest, TResponse>(),
                    _failurePresenter)
            {
                SuccessSink = SuccessSink,
                Analytics = Analytics,
                Gate = Gate,
                Cancellation = Cancellation,
            };
    }
}
