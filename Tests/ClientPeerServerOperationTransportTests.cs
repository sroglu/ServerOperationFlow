using System;
using System.Collections;
using System.Threading.Tasks;
using MessagePack;
using NUnit.Framework;
using UnityEngine.TestTools;
using PFound.NetworkLayer;
using PFound.ServerOperationFlow.Core;

namespace PFound.ServerOperationFlow.Tests
{
    // ---------------------------------------------------------------------------------------------
    // Wire contract discipline (MODULE.md "### 0. Wire contract discipline"; SPEC §4a "Model Y").
    // This test file is the compiled, Unity-verified example of that discipline: opcodes use the banded
    // two-level scheme — a central NetDomain byte + a per-domain op byte, folded via Opcode.Of (W3); each content payload is an immutable
    // [MessagePackObject] readonly struct with explicit [Key]s + IEquatable value-equality (W2);
    // and the poolable RequestMessage/ReplyMessage ENVELOPES stay plain classes (NOT
    // [MessagePackObject]) that just carry the content DTO (W1). The MessagePackBodyCodec below
    // genuinely packs/unpacks the [Key]-attributed DTOs over the loopback, so the keys are exercised.
    // ---------------------------------------------------------------------------------------------

    // (W3) Banded two-level opcodes: a tiny central NetDomain enum (high byte, one entry per subsystem)
    // plus a per-domain op enum (low byte) with LOCAL values. opcode = (domain << 8) | op, composed by
    // Opcode.Of. Different domain ⇒ different high byte ⇒ cross-subsystem collisions are impossible. This
    // test needs only one domain. A reply is decoded by CORRELATION (the caller's known reply type), not
    // by its own opcode, so replies are NOT enrolled and cost no op value — only the request does.
    public enum NetDomain : byte
    {
        Reward = 1,          // 0x01 — becomes the opcode HIGH byte
    }

    public enum RewardOp : byte
    {
        GrantRequest = 1,    // 0x01 -> opcode 0x0101  ((domain << 8) | op)
    }

    // (W2) Request content = an immutable readonly struct DTO with explicit [Key]s + IEquatable
    // value-equality. This is the shape that actually crosses the wire (never contractless
    // member-order serialization). Its value-equality also makes it a natural single-flight key.
    [MessagePackObject]
    public readonly struct GrantReward : IEquatable<GrantReward>
    {
        [Key(0)] public readonly int Amount;

        [SerializationConstructor]
        public GrantReward(int amount) => Amount = amount;

        public bool Equals(GrantReward other) => Amount == other.Amount;
        public override bool Equals(object obj) => obj is GrantReward other && Equals(other);
        public override int GetHashCode() => Amount;
        public override string ToString() => "GrantReward(" + Amount + ")";
    }

    // (W2) Reply content = an immutable readonly struct DTO carrying the authoritative server value.
    [MessagePackObject]
    public readonly struct GrantOutcome : IEquatable<GrantOutcome>
    {
        [Key(0)] public readonly int NewBalance;

        [SerializationConstructor]
        public GrantOutcome(int newBalance) => NewBalance = newBalance;

        public bool Equals(GrantOutcome other) => NewBalance == other.NewBalance;
        public override bool Equals(object obj) => obj is GrantOutcome other && Equals(other);
        public override int GetHashCode() => NewBalance;
        public override string ToString() => "GrantOutcome(" + NewBalance + ")";
    }

    // (W1) The poolable envelopes stay PLAIN classes (NOT [MessagePackObject]); they just carry the
    // stable content DTO as a public field. Wire stability lives in the DTO, which owns the game data
    // that actually evolves — the envelope is a thin, framework-owned shape.
    public sealed class GrantRewardRequest : RequestMessage, IMessagePayload
    {
        public GrantReward Content;
        Type IMessagePayload.PayloadType => typeof(GrantReward);
        object IMessagePayload.Payload { get => Content; set => Content = (GrantReward)value; }
        public override void Clear() { Content = default; }
    }

    public sealed class GrantRewardReply : ReplyMessage, IMessagePayload
    {
        public GrantOutcome Content;
        Type IMessagePayload.PayloadType => typeof(GrantOutcome);
        object IMessagePayload.Payload { get => Content; set => Content = (GrantOutcome)value; }
        public override void Clear() { base.Clear(); Content = default; }
    }

    // The MessagePack source-generated resolver for this test assembly's content DTOs. Pushed into the codec
    // below so the [Key]-attributed DTOs pack/unpack through AOT-safe generated formatters (no dynamic resolver).
    [GeneratedMessagePackResolver]
    public partial class RewardTestResolver { }

    // A minimal result-code enum so this test's flow reports through the generic ready-made result
    // (ServerOperationResult<TransportProbeResult>). The adapter round-trip only checks IsSuccess, so two
    // values — an Ok sentinel and a single Failed code — are all the coverage here needs.
    public enum TransportProbeResult
    {
        Ok = 0,
        Failed = 1,
    }

    /// <summary>
    /// Glue coverage: the NetworkLayer adapter maps the engine-free transport seam to
    /// <see cref="ClientPeer.CallAsync{TReply}(RequestMessage,int)"/>, driving a real operation lifecycle
    /// over the in-memory loopback transport. No server peer is needed — a manual wire endpoint reads the
    /// client's request frame, correlates it, and returns a crafted reply, so the whole
    /// send → await → interpret → apply path runs end-to-end through the adapter, with the
    /// [Key]-attributed content DTOs genuinely MessagePack-packed over the loopback.
    /// </summary>
    [TestFixture]
    public sealed class ClientPeerServerOperationTransportTests
    {
        static MessageCatalog NewCatalog()
        {
            // MessagePackBodyCodec so the [Key]-attributed DTOs are really packed/unpacked over the wire —
            // through this assembly's AOT source-generated resolver (pushed explicitly, no reflection discovery).
            var catalog = new MessageCatalog(new MessagePackBodyCodec(RewardTestResolver.Instance));
            // (W3) banded request/reply PAIR in one call: the request takes the op; the reply is decoded by
            // correlation (the caller's known reply type), so it is un-enrolled and merely registered for
            // pooling by type — the two-type Enroll does both.
            catalog.ForDomain(NetDomain.Reward)
                .Enroll<GrantRewardRequest, GrantRewardReply>(RewardOp.GrantRequest);
            return catalog;
        }

        sealed class RecordingFailurePresenter : IServerOperationFailurePresenter<ServerOperationResult<TransportProbeResult>>
        {
            public int Count;
            public void PresentFailure(ServerOperationResult<TransportProbeResult> result) => Count++;
        }

        // A concrete operation that grants a reward: build the request DTO from its amount, and on a
        // successful reply record the server-authoritative balance into local state (synchronously,
        // per the lifecycle).
        sealed class GrantRewardOperation : ServerOperationFlow<GrantRewardRequest, GrantRewardReply, ServerOperationResult<TransportProbeResult>>
        {
            readonly GrantReward _content;   // the immutable request DTO; also this run's dedup identity
            int _serverBalance;
            public int AppliedBalance = -1;

            public GrantRewardOperation(
                ServerOperationContext<GrantRewardRequest, GrantRewardReply, ServerOperationResult<TransportProbeResult>> context,
                int amount) : base(context)
                => _content = new GrantReward(amount);

            // (D1) The IEquatable content DTO doubles as the single-flight key: two runs whose request
            // content is value-equal produce the same key, so while one is in flight the other is
            // suppressed. Value-equality on GrantReward is what makes the folded key correct for free.
            protected override string DuplicateKey => "GrantReward:" + _content;

            protected override ServerOperationResult<TransportProbeResult> PreCheck()
                => _content.Amount > 0
                    ? ServerOperationResult<TransportProbeResult>.Success()
                    : ServerOperationResult<TransportProbeResult>.Failure(TransportProbeResult.Failed, "non-positive amount");

            protected override GrantRewardRequest BuildRequest() => new GrantRewardRequest { Content = _content };

            protected override ServerOperationResult<TransportProbeResult> Interpret(GrantRewardReply response)
            {
                _serverBalance = response.Content.NewBalance; // capture the authoritative value for the apply step
                return response.Status == ReplyStatus.Ok
                    ? ServerOperationResult<TransportProbeResult>.Success()
                    : ServerOperationResult<TransportProbeResult>.Failure(TransportProbeResult.Failed, "server rejected");
            }

            protected override void ApplySuccess(ServerOperationResult<TransportProbeResult> result) => AppliedBalance = _serverBalance;
        }

        [UnityTest]
        public IEnumerator AdapterMapsSeamToCallAsync_RoundTripAppliesServerResult()
        {
            var catalog = NewCatalog();
            var hub = new LoopbackHub();
            var clientLink = new LoopbackClientLink(hub);
            var serverLink = new LoopbackServerLink(hub);

            var client = new ClientPeer(clientLink, catalog, ClientLinkOptions.Default);
            client.Connect("loopback", 0);
            serverLink.Listen(0);
            serverLink.Pump(8); // observe the connect edge so the manual endpoint may reply

            // Manual wire endpoint: read the client's request frame, correlate by token, reply with a
            // balance packed as the GrantOutcome content DTO.
            const int serverBalance = 250;
            serverLink.Received += (peer, frame) =>
            {
                Envelope env = FrameCodec.Read(frame);
                if (env.Kind != MessageKind.Request) return;
                var reply = new GrantRewardReply { Content = new GrantOutcome(serverBalance) };
                byte[] body = catalog.PackBody(reply);
                // Echo the REQUEST opcode: the reply has no opcode of its own (correlation model);
                // the client ignores the reply opcode and decodes by the awaited reply type.
                byte[] replyFrame = FrameCodec.WriteReply(Opcode.Of(NetDomain.Reward, RewardOp.GrantRequest), env.CallToken, ReplyStatus.Ok,
                    new ArraySegment<byte>(body));
                serverLink.Deliver(peer, new ArraySegment<byte>(replyFrame));
            };

            var transport = new ClientPeerServerOperationTransport<GrantRewardRequest, GrantRewardReply>(client);
            var presenter = new RecordingFailurePresenter();
            var context = new ServerOperationContext<GrantRewardRequest, GrantRewardReply, ServerOperationResult<TransportProbeResult>>(transport, presenter);
            var op = new GrantRewardOperation(context, amount: 10);

            Task<ServerOperationRun<ServerOperationResult<TransportProbeResult>>> run = op.RunAsync();

            // Pump both ends until the lifecycle task settles (CallAsync completes on a ThreadPool bounce).
            for (int i = 0; i < 600 && !run.IsCompleted; i++)
            {
                serverLink.Pump(8);
                client.Update();
                yield return null;
            }

            Assert.IsTrue(run.IsCompleted, "lifecycle did not settle through the adapter");
            Assert.IsFalse(run.IsFaulted, "lifecycle faulted: " + (run.Exception == null ? "" : run.Exception.ToString()));
            Assert.IsTrue(run.Result.Accepted, "run should be accepted");
            Assert.IsTrue(run.Result.Result.IsSuccess, "adapter round-trip should map to success");
            Assert.AreEqual(serverBalance, op.AppliedBalance, "the server-authoritative balance flowed through the adapter into local state");
            Assert.AreEqual(0, presenter.Count, "no failure presentation on the happy path");
        }

        [UnityTest]
        public IEnumerator PreCheckFail_ShortCircuitsBeforeAdapter_NoFrameSent()
        {
            var catalog = NewCatalog();
            var hub = new LoopbackHub();
            var clientLink = new LoopbackClientLink(hub);
            var serverLink = new LoopbackServerLink(hub);

            var client = new ClientPeer(clientLink, catalog, ClientLinkOptions.Default);
            client.Connect("loopback", 0);
            serverLink.Listen(0);
            serverLink.Pump(8);

            int framesSeenByServer = 0;
            serverLink.Received += (peer, frame) => framesSeenByServer++;

            var transport = new ClientPeerServerOperationTransport<GrantRewardRequest, GrantRewardReply>(client);
            var presenter = new RecordingFailurePresenter();
            var context = new ServerOperationContext<GrantRewardRequest, GrantRewardReply, ServerOperationResult<TransportProbeResult>>(transport, presenter);
            var op = new GrantRewardOperation(context, amount: 0); // fails the local pre-check

            Task<ServerOperationRun<ServerOperationResult<TransportProbeResult>>> run = op.RunAsync();

            for (int i = 0; i < 30 && !run.IsCompleted; i++)
            {
                serverLink.Pump(8);
                client.Update();
                yield return null;
            }

            Assert.IsTrue(run.IsCompleted, "pre-check failure should complete synchronously");
            Assert.IsFalse(run.Result.Result.IsSuccess, "pre-check should reject");
            Assert.AreEqual(0, framesSeenByServer, "a rejected pre-check must send no frame through the adapter");
            Assert.AreEqual(1, presenter.Count, "failure presenter should fire for the pre-check rejection");
        }

        [UnityTest]
        public IEnumerator DuplicateContentKey_OverlappingRuns_SendOneRequest_SecondSuppressed()
        {
            // (D1) The IEquatable content DTO as the operation's single-flight DuplicateKey: two overlapping
            // runs whose request content is value-equal must send ONE request; the second is suppressed.
            var catalog = NewCatalog();
            var hub = new LoopbackHub();
            var clientLink = new LoopbackClientLink(hub);
            var serverLink = new LoopbackServerLink(hub);

            var client = new ClientPeer(clientLink, catalog, ClientLinkOptions.Default);
            client.Connect("loopback", 0);
            serverLink.Listen(0);
            serverLink.Pump(8);

            const int serverBalance = 400;
            int requestFramesSeenByServer = 0;
            serverLink.Received += (peer, frame) =>
            {
                Envelope env = FrameCodec.Read(frame);
                if (env.Kind != MessageKind.Request) return;
                requestFramesSeenByServer++;
                var reply = new GrantRewardReply { Content = new GrantOutcome(serverBalance) };
                byte[] body = catalog.PackBody(reply);
                // Echo the REQUEST opcode: the reply has no opcode of its own (correlation model);
                // the client ignores the reply opcode and decodes by the awaited reply type.
                byte[] replyFrame = FrameCodec.WriteReply(Opcode.Of(NetDomain.Reward, RewardOp.GrantRequest), env.CallToken, ReplyStatus.Ok,
                    new ArraySegment<byte>(body));
                serverLink.Deliver(peer, new ArraySegment<byte>(replyFrame));
            };

            var transport = new ClientPeerServerOperationTransport<GrantRewardRequest, GrantRewardReply>(client);
            var presenter = new RecordingFailurePresenter();
            // One shared gate is what makes two runs of the same content-key guard against each other.
            var gate = ServerOperationGate.DedupOnly();
            var context = new ServerOperationContext<GrantRewardRequest, GrantRewardReply, ServerOperationResult<TransportProbeResult>>(transport, presenter) { Gate = gate };

            // Same amount ⇒ value-equal GrantReward content ⇒ equal DuplicateKey (verify the equality that
            // the key relies on, so this is a genuine content-DTO dedup, not an accidental type-name match).
            Assert.AreEqual(new GrantReward(10), new GrantReward(10), "IEquatable content DTO must be value-equal for equal amounts");
            var opFirst  = new GrantRewardOperation(context, amount: 10);
            var opSecond = new GrantRewardOperation(context, amount: 10);

            // Start the first run: it enters the gate, passes pre-check, sends, and is now awaiting the
            // network hop (RunAsync runs synchronously up to the first await), so it HOLDS the key.
            Task<ServerOperationRun<ServerOperationResult<TransportProbeResult>>> firstRun = opFirst.RunAsync();
            // Start the second while the first is in flight: TryEnter sees the key held ⇒ suppressed.
            Task<ServerOperationRun<ServerOperationResult<TransportProbeResult>>> secondRun = opSecond.RunAsync();

            Assert.IsTrue(secondRun.IsCompleted, "the suppressed run completes synchronously (it never awaited)");
            Assert.AreEqual(ServerOperationDisposition.DuplicateSuppressed, secondRun.Result.Disposition,
                "the overlapping duplicate must be suppressed by the single-flight gate");
            Assert.IsFalse(secondRun.Result.Accepted, "a suppressed run carries no result");

            for (int i = 0; i < 600 && !firstRun.IsCompleted; i++)
            {
                serverLink.Pump(8);
                client.Update();
                yield return null;
            }

            Assert.IsTrue(firstRun.IsCompleted, "the admitted run should settle through the adapter");
            Assert.IsFalse(firstRun.IsFaulted, "lifecycle faulted: " + (firstRun.Exception == null ? "" : firstRun.Exception.ToString()));
            Assert.IsTrue(firstRun.Result.Accepted, "the first run should be accepted");
            Assert.IsTrue(firstRun.Result.Result.IsSuccess, "the admitted run should succeed");
            Assert.AreEqual(serverBalance, opFirst.AppliedBalance, "the admitted run applied the server balance");
            Assert.AreEqual(1, requestFramesSeenByServer, "two overlapping same-content runs must send exactly ONE request");
        }
    }
}
