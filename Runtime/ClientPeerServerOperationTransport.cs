using System.Threading;
using System.Threading.Tasks;
using PFound.NetworkLayer;
using PFound.ServerOperationFlow.Core;

namespace PFound.ServerOperationFlow
{
    /// <summary>
    /// The one place the engine-free lifecycle's transport seam is bound to the realtime request/reply
    /// layer. It forwards a run's request to <see cref="ClientPeer.CallAsync{TReply}(RequestMessage,int)"/>
    /// and hands back the correlated reply. The wire-message base-type constraints
    /// (<see cref="RequestMessage"/> / <see cref="ReplyMessage"/>) live here, on the adapter's own type
    /// parameters — never in the Core seam — so the Core stays free of any messaging or engine type.
    ///
    /// The round-trip deadline is governed by <paramref name="deadlineMs"/> (or the peer's default when -1);
    /// the seam's cancellation token governs the lifecycle's cancellable post-effects, not the network hop
    /// (the underlying call is deadline-bounded, so a stuck request faults on expiry rather than hanging).
    ///
    /// The wire-message base-type constraints (<see cref="RequestMessage"/> / <see cref="ReplyMessage"/>) live
    /// here, on the adapter's own type parameters — never in the Core seam — so the Core stays free of any
    /// messaging or engine type. The concrete <typeparamref name="TResponse"/> is required by
    /// <see cref="ClientPeer.CallAsync{TReply}(RequestMessage,int)"/> (it decodes the reply by that awaited type),
    /// so the engine-free ambient transport factory constructs this reflectively from the concrete envelope types.
    /// </summary>
    public sealed class ClientPeerServerOperationTransport<TRequest, TResponse>
        : IServerOperationTransport<TRequest, TResponse>
        where TRequest : RequestMessage
        where TResponse : ReplyMessage
    {
        readonly ClientPeer _peer;
        readonly int _deadlineMs;

        public ClientPeerServerOperationTransport(ClientPeer peer, int deadlineMs = -1)
        {
            _peer = peer;
            _deadlineMs = deadlineMs;
        }

        public Task<TResponse> SendAsync(TRequest request, CancellationToken cancellation)
            => _peer.CallAsync<TResponse>(request, _deadlineMs);
    }
}
