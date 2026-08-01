namespace PFound.ServerOperationFlow.Core
{
    /// <summary>
    /// Builds the transport seam for a flow's request/response pair. The game supplies one implementation at
    /// boot (it binds to the realtime request/reply layer); the ambient host uses it to fill a
    /// <see cref="ServerOperationContext{TRequest,TResponse,TResult}.Transport"/> without the call site
    /// threading a transport through. The method is unconstrained because the Core cannot see the wire-message
    /// base types — the game's implementation carries whatever constraint its transport needs.
    /// </summary>
    public interface IServerOperationTransportFactory
    {
        IServerOperationTransport<TRequest, TResponse> CreateTransport<TRequest, TResponse>();
    }

    /// <summary>
    /// Supplies the game-side outcome seams (failure presentation and the success notification) for a flow's
    /// result type. The game supplies one implementation at boot; the ambient host uses it to fill a context's
    /// <see cref="ServerOperationContext{TRequest,TResponse,TResult}.FailurePresenter"/> and
    /// <see cref="ServerOperationContext{TRequest,TResponse,TResult}.SuccessSink"/>.
    /// </summary>
    public interface IServerOperationResultChannels
    {
        IServerOperationFailurePresenter<TResult> FailurePresenter<TResult>() where TResult : IServerOperationResult;
        IServerOperationSuccessSink<TResult> SuccessSink<TResult>() where TResult : IServerOperationResult;
    }

    /// <summary>
    /// The ambient host a flow resolves its <see cref="ServerOperationContext{TRequest,TResponse,TResult}"/>
    /// from, mirroring how the generated <c>Execute</c> resolves <c>NetworkClient.Current</c>. The game builds
    /// one host at boot (transport factory + outcome channels, plus the shared run policy and analytics) and
    /// assigns it to <see cref="Current"/> ONCE. Thereafter a flow is constructed with only the game's own
    /// parameters — <c>new SpendCoinsOperationFlow(amount, wallet)</c> — and its base ctor pulls the context
    /// from here. Tests that want an explicit, hand-built context keep using the flow's context ctor and never
    /// touch this. It is a plain settable field on purpose: an unconfigured flow faults fast with a
    /// <see cref="System.NullReferenceException"/> (nothing here should be null at runtime; no defensive guard).
    /// </summary>
    public sealed class ServerOperationHost
    {
        /// <summary>The host every ambient-constructed flow resolves its context from. Assign once at boot.</summary>
        public static ServerOperationHost Current;

        readonly IServerOperationTransportFactory _transportFactory;
        readonly IServerOperationResultChannels _resultChannels;

        /// <summary>The shared run policy (single-flight + loading) handed to every resolved context.</summary>
        public ServerOperationGate Gate { get; set; } = ServerOperationGate.Disabled;

        /// <summary>The telemetry sink handed to every resolved context.</summary>
        public IServerOperationAnalytics Analytics { get; set; } = new SilentAnalytics();

        public ServerOperationHost(
            IServerOperationTransportFactory transportFactory,
            IServerOperationResultChannels resultChannels)
        {
            _transportFactory = transportFactory;
            _resultChannels = resultChannels;
        }

        /// <summary>Assemble the context for one flow from the ambient factory + channels + shared seams.</summary>
        public ServerOperationContext<TRequest, TResponse, TResult> CreateContext<TRequest, TResponse, TResult>()
            where TResult : IServerOperationResult
            => new ServerOperationContext<TRequest, TResponse, TResult>(
                    _transportFactory.CreateTransport<TRequest, TResponse>(),
                    _resultChannels.FailurePresenter<TResult>())
            {
                SuccessSink = _resultChannels.SuccessSink<TResult>(),
                Analytics = Analytics,
                Gate = Gate,
            };
    }
}
