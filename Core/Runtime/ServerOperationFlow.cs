namespace PFound.ServerOperationFlow.Core
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The sealed lifecycle for one server-authoritative operation. A concrete operation subclasses this,
    /// parameterised by its request type, its response type, and the game's result type, and fills the
    /// abstract hooks. The ORDER of the run is owned here and cannot be reordered or skipped by a subclass —
    /// that is the whole point: every operation fills the same ordered, half-sealed shape, which deletes the
    /// recurring "forgot to validate before sending / applied an unverified result / missed cancellation /
    /// handled errors inconsistently" bug class.
    ///
    /// The ordered lifecycle a single <see cref="RunAsync"/> drives:
    /// 1. Entry — admitted or refused by the opt-in run policy.
    /// 2. Local pre-check (client prediction) — an instant local reject; on failure NO request is sent.
    /// 3. Build the typed request.
    /// 4. Send + await the response over the injected transport (the server is authoritative).
    /// 5. Interpret the response into the game result (success or failure).
    /// 6. On success — a SYNCHRONOUS state apply (no awaits), then the success notification + analytics.
    /// 7. Optional async, cancellable post-effects (navigation, animation, hand-chaining other operations).
    /// 8. Uniform failure handling — one path for both the pre-check reject and the server failure.
    /// </summary>
    public abstract class ServerOperationFlow<TRequest, TResponse, TResult>
        where TResult : IServerOperationResult
    {
        readonly ServerOperationContext<TRequest, TResponse, TResult> _context;

        /// <summary>
        /// The ambient ctor a game flow uses: it resolves the context from
        /// <see cref="ServerOperationHost{TResult}.Current"/> so the concrete flow's own ctor takes ONLY the game's
        /// parameters and the call site stays clean (<c>await new SpendCoinsOperationFlow(amount).RunAsync()</c>).
        /// The host is configured once at boot.
        /// </summary>
        protected ServerOperationFlow()
            : this(ServerOperationHost<TResult>.Current.CreateContext<TRequest, TResponse>())
        {
        }

        /// <summary>
        /// The explicit-context ctor: hand the flow a context you built yourself. Tests use this to inject a stub
        /// transport and recording seams without configuring an ambient host.
        /// </summary>
        protected ServerOperationFlow(ServerOperationContext<TRequest, TResponse, TResult> context)
            => _context = context;

        /// <summary>
        /// The single-flight key for this run. Defaults to the operation type, so any two runs of the same
        /// operation guard against each other. Override to fold in request parameters when guarding should
        /// be per target (e.g. per invitation id) rather than per operation.
        /// </summary>
        protected virtual string DuplicateKey => GetType().FullName;

        /// <summary>The label reported to analytics. Defaults to the operation type name.</summary>
        protected virtual string OperationName => GetType().Name;

        /// <summary>
        /// The reply envelope captured from the most recent send in <see cref="RunAsync"/>. Its <c>Content</c> is
        /// the reply DTO — the generated flow-running <c>Execute</c> returns <c>Reply.Content</c>, so a call that
        /// runs through a flow returns the very same reply DTO a flowless op would. It stays at its default until a
        /// send completes (a pre-check reject sends nothing and leaves this unset).
        /// </summary>
        public TResponse Reply { get; private set; }

        // ---- subclass hooks (the only parts a concrete operation fills) ----

        /// <summary>Step 2 — validate locally before any network I/O; a failing result rejects instantly.</summary>
        protected abstract TResult PreCheck();

        /// <summary>Step 3 — build the typed request for this run.</summary>
        protected abstract TRequest BuildRequest();

        /// <summary>Step 5 — map the server's response to the game result (success or failure).</summary>
        protected abstract TResult Interpret(TResponse response);

        /// <summary>Step 6 — apply the authoritative result to local state synchronously (no awaits).</summary>
        protected abstract void ApplySuccess(TResult result);

        /// <summary>
        /// Step 7 — optional async follow-up: navigation, animation, or hand-chaining another operation via
        /// <c>await Other.RunAsync(cancellation)</c>. Observe <paramref name="cancellation"/> between steps
        /// (call <c>cancellation.ThrowIfCancellationRequested()</c>) so a cancel stops the remaining work.
        /// </summary>
        protected virtual Task RunPostEffectsAsync(TResult result, CancellationToken cancellation)
            => Task.CompletedTask;

        /// <summary>Run the whole lifecycle to a terminal outcome.</summary>
        public async Task<ServerOperationRun<TResult>> RunAsync(CancellationToken cancellation = default)
        {
            // Fall back to the ambient (gameloop) cancellation when the caller passes none; a cancel here never enters the gate.
            CancellationToken token = cancellation.CanBeCanceled ? cancellation : _context.Cancellation;
            token.ThrowIfCancellationRequested();

            // Step 1 — entry. The run policy refuses a duplicate that is already in flight.
            string key = DuplicateKey;
            if (!_context.Gate.TryEnter(key))
                return ServerOperationRun<TResult>.Suppressed();

            try
            {
                // Step 2 — local pre-check. A failing pre-check sends nothing and takes the failure path.
                TResult precheck = PreCheck();
                if (!precheck.IsSuccess)
                {
                    Fail(precheck);
                    return ServerOperationRun<TResult>.Completed(precheck);
                }

                // Step 3 — build the request.
                TRequest request = BuildRequest();

                // The loading window covers only the network round-trip and what follows it.
                _context.Gate.Busy();
                try
                {
                    // Step 4 — send + await. The client does not decide success; the server does.
                    TResponse response = await _context.Transport.SendAsync(request, token);
                    Reply = response;

                    // Step 5 — interpret the response.
                    TResult outcome = Interpret(response);
                    if (!outcome.IsSuccess)
                    {
                        Fail(outcome);
                        return ServerOperationRun<TResult>.Completed(outcome);
                    }

                    // Step 6 — synchronous success apply, then notify + analytics.
                    ApplySuccess(outcome);
                    _context.SuccessSink.OnSuccess(outcome);
                    _context.Analytics.RecordOutcome(OperationName, outcome);

                    // Step 7 — optional cancellable post-effects.
                    token.ThrowIfCancellationRequested();
                    await RunPostEffectsAsync(outcome, token);

                    return ServerOperationRun<TResult>.Completed(outcome);
                }
                finally
                {
                    // Hide the loading indicator on EVERY terminal of the in-flight window
                    // (success, server failure, or cancellation).
                    _context.Gate.Idle();
                }
            }
            finally
            {
                // Release the single-flight key on EVERY terminal so the operation can run again.
                _context.Gate.Exit(key);
            }
        }

        // Step 8 — the one failure path, serving both the pre-check reject and the server failure.
        void Fail(TResult result)
        {
            _context.FailurePresenter.PresentFailure(result);
            _context.Analytics.RecordOutcome(OperationName, result);
        }
    }
}
