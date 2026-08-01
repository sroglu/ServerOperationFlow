using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PFound.ServerOperationFlow.Core.Tests
{
    /// <summary>
    /// Standalone mono/csc runner for the engine-free ServerOperation lifecycle: pre-check short-circuit,
    /// the ordered happy path, server-failure funnelling, cancellation of post-effects (incl. a chained
    /// task), manual chaining, and the opt-in run policy (single-flight dedup + loading indicator).
    /// </summary>
    internal static class Program
    {
        static int Main()
        {
            RunAll().GetAwaiter().GetResult();
            return TestKit.Summary("ServerOperationFlow.Core");
        }

        static async Task RunAll()
        {
            await PreCheckFail_SendsNothing_TakesFailurePath();
            await HappyPath_RunsStepsInOrder();
            await ServerFail_FunnelsToFailure_NoSuccessApply();
            await Cancellation_MidPostEffects_StopsRemainingWorkAndChain();
            await ManualChaining_RunsBothLifecyclesInOrder();
            await SingleFlight_OverlappingRunsSendOnce_ThenReleaseAllowsRerun();
            await SingleFlight_OptOut_SendsTwice();
            await SingleFlight_PerTargetKey_DistinctTargetsBothSend();
            await LoadingHook_ShowBeforeSend_HideOnBothSuccessAndFailure();
            await LoadingHook_PreCheckFail_NeverShows();
            SuccessSink_And_Analytics_FireOnSuccess();
            await AmbientHost_ResolvesContext_FromCurrent();
            CodeToast_DefinedCode_ShowsEnumMemberLabel();
            CodeToast_UnknownCode_ShowsRawNumberLabel();
        }

        // ---- helpers ----

        static (ServerOperationContext<ProbeRequest, ProbeResponse, ServerOperationResult<ProbeOpResult>> ctx,
                ProbeTransport transport,
                ProbeFailurePresenter presenter)
            NewContext()
        {
            var transport = new ProbeTransport();
            var presenter = new ProbeFailurePresenter();
            var ctx = new ServerOperationContext<ProbeRequest, ProbeResponse, ServerOperationResult<ProbeOpResult>>(transport, presenter);
            return (ctx, transport, presenter);
        }

        // ---- cases ----

        static async Task PreCheckFail_SendsNothing_TakesFailurePath()
        {
            var (ctx, transport, presenter) = NewContext();
            var log = new List<string>();
            var op = new RecordingOperation(ctx, log)
            {
                PreCheckResult = () => ServerOperationResult<ProbeOpResult>.Failure((ProbeOpResult)7, "rejected locally")
            };

            var run = await op.RunAsync();

            TestKit.Check(run.Accepted, "precheck-fail: run reports completed");
            TestKit.Check(!run.Result.IsSuccess && run.Result.ResultCode == (ProbeOpResult)7, "precheck-fail: carries the pre-check result");
            TestKit.Check(transport.SendCount == 0, "precheck-fail: transport NOT called");
            TestKit.Check(presenter.Count == 1 && presenter.Last.ResultCode == (ProbeOpResult)7, "precheck-fail: failure presenter invoked with the pre-check result");
            TestKit.Check(!log.Contains("op.build"), "precheck-fail: request not built");
        }

        static async Task HappyPath_RunsStepsInOrder()
        {
            var (ctx, transport, presenter) = NewContext();
            var log = new List<string>();
            var op = new RecordingOperation(ctx, log);

            var run = await op.RunAsync();

            TestKit.Check(run.Accepted && run.Result.IsSuccess, "happy: success result");
            TestKit.Check(transport.SendCount == 1, "happy: sent exactly once");
            TestKit.Check(presenter.Count == 0, "happy: failure presenter not invoked");
            bool order = log.Count == 5
                && log[0] == "op.precheck" && log[1] == "op.build" && log[2] == "op.interpret"
                && log[3] == "op.apply" && log[4] == "op.posteffects";
            TestKit.Check(order, "happy: steps ran precheck -> build -> (send) -> interpret -> apply -> posteffects");
        }

        static async Task ServerFail_FunnelsToFailure_NoSuccessApply()
        {
            var (ctx, transport, presenter) = NewContext();
            var log = new List<string>();
            var op = new RecordingOperation(ctx, log)
            {
                InterpretResult = _ => ServerOperationResult<ProbeOpResult>.Failure((ProbeOpResult)42, "server refused")
            };

            var run = await op.RunAsync();

            TestKit.Check(!run.Result.IsSuccess && run.Result.ResultCode == (ProbeOpResult)42, "server-fail: carries the mapped server result");
            TestKit.Check(transport.SendCount == 1, "server-fail: the request WAS sent");
            TestKit.Check(!log.Contains("op.apply"), "server-fail: no success apply");
            TestKit.Check(presenter.Count == 1 && presenter.Last.ResultCode == (ProbeOpResult)42, "server-fail: same failure path as pre-check");
        }

        static async Task Cancellation_MidPostEffects_StopsRemainingWorkAndChain()
        {
            var (outerCtx, outerTransport, _) = NewContext();
            var (innerCtx, innerTransport, _) = NewContext();
            var log = new List<string>();
            var cts = new CancellationTokenSource();

            var inner = new RecordingOperation(innerCtx, log) { Tag = "inner" };
            var outer = new RecordingOperation(outerCtx, log) { Tag = "outer" };
            outer.PostEffects = async (result, cancellation) =>
            {
                log.Add("outer.post1");
                cts.Cancel();
                cancellation.ThrowIfCancellationRequested();
                log.Add("outer.post2");            // must NOT run
                await inner.RunAsync(cancellation); // must NOT run
            };

            bool canceled = false;
            try { await outer.RunAsync(cts.Token); }
            catch (OperationCanceledException) { canceled = true; }

            TestKit.Check(canceled, "cancel: RunAsync surfaces OperationCanceledException");
            TestKit.Check(outerTransport.SendCount == 1, "cancel: outer still sent (success applied before post-effects)");
            TestKit.Check(log.Contains("outer.apply"), "cancel: synchronous apply ran before post-effects");
            TestKit.Check(log.Contains("outer.post1") && !log.Contains("outer.post2"), "cancel: remaining post-effect work stopped");
            TestKit.Check(innerTransport.SendCount == 0, "cancel: chained task did not run");
        }

        static async Task ManualChaining_RunsBothLifecyclesInOrder()
        {
            var (outerCtx, _, _) = NewContext();
            var (innerCtx, innerTransport, _) = NewContext();
            var log = new List<string>();

            var inner = new RecordingOperation(innerCtx, log) { Tag = "inner" };
            var outer = new RecordingOperation(outerCtx, log) { Tag = "outer" };
            outer.PostEffects = async (result, cancellation) => await inner.RunAsync(cancellation);

            var run = await outer.RunAsync();

            TestKit.Check(run.Accepted, "chain: outer completed");
            TestKit.Check(innerTransport.SendCount == 1, "chain: inner ran too");
            int outerPost = log.IndexOf("outer.posteffects");
            int innerStart = log.IndexOf("inner.precheck");
            TestKit.Check(outerPost >= 0 && innerStart > outerPost, "chain: inner lifecycle runs inside outer's post-effects, in order");
        }

        static async Task SingleFlight_OverlappingRunsSendOnce_ThenReleaseAllowsRerun()
        {
            var (ctx, transport, _) = NewContext();
            ctx.Gate = ServerOperationGate.DedupOnly();
            var pending = new TaskCompletionSource<ProbeResponse>();
            transport.Pending = pending;

            var op1 = new RecordingOperation(ctx, new List<string>());
            var op2 = new RecordingOperation(ctx, new List<string>());

            var run1 = op1.RunAsync();      // suspends at the (held) send
            var run2 = await op2.RunAsync(); // gate refuses the duplicate immediately

            TestKit.Check(run2.Disposition == ServerOperationDisposition.DuplicateSuppressed, "single-flight: second overlapping run suppressed");
            TestKit.Check(transport.SendCount == 1, "single-flight: only one request sent");

            pending.SetResult(transport.Response);
            var completed1 = await run1;
            TestKit.Check(completed1.Accepted, "single-flight: first run completes after the response");

            transport.Pending = null;         // key released on terminal -> a fresh run may send again
            var op3 = new RecordingOperation(ctx, new List<string>());
            var run3 = await op3.RunAsync();
            TestKit.Check(run3.Accepted && transport.SendCount == 2, "single-flight: key released, later run sends again");
        }

        static async Task SingleFlight_OptOut_SendsTwice()
        {
            var (ctx, transport, _) = NewContext(); // default Gate = Disabled (no gate)
            var pending = new TaskCompletionSource<ProbeResponse>();
            transport.Pending = pending;

            var op1 = new RecordingOperation(ctx, new List<string>());
            var op2 = new RecordingOperation(ctx, new List<string>());

            var run1 = op1.RunAsync();
            var run2 = op2.RunAsync();

            TestKit.Check(transport.SendCount == 2, "opt-out: no gate, both overlapping runs send");

            pending.SetResult(transport.Response);
            await run1;
            await run2;
        }

        static async Task SingleFlight_PerTargetKey_DistinctTargetsBothSend()
        {
            var (ctx, transport, _) = NewContext();
            ctx.Gate = ServerOperationGate.DedupOnly();
            var pending = new TaskCompletionSource<ProbeResponse>();
            transport.Pending = pending;

            var op1 = new RecordingOperation(ctx, new List<string>()) { OverrideKey = "invite:1" };
            var op2 = new RecordingOperation(ctx, new List<string>()) { OverrideKey = "invite:2" };

            var run1 = op1.RunAsync();
            var run2 = op2.RunAsync();

            TestKit.Check(transport.SendCount == 2, "per-target: distinct dedup keys both send");

            pending.SetResult(transport.Response);
            await run1;
            await run2;
        }

        static async Task LoadingHook_ShowBeforeSend_HideOnBothSuccessAndFailure()
        {
            var indicator = new ProbeLoadingIndicator();

            var (successCtx, _, _) = NewContext();
            successCtx.Gate = ServerOperationGate.WithLoading(indicator);
            await new RecordingOperation(successCtx, new List<string>()).RunAsync();
            TestKit.Check(indicator.ShowCount == 1 && indicator.HideCount == 1, "loading: shown once + hidden once on success");

            indicator.Reset();
            var (failCtx, _, _) = NewContext();
            failCtx.Gate = ServerOperationGate.WithLoading(indicator);
            var failOp = new RecordingOperation(failCtx, new List<string>())
            {
                InterpretResult = _ => ServerOperationResult<ProbeOpResult>.Failure((ProbeOpResult)9, "server no")
            };
            await failOp.RunAsync();
            TestKit.Check(indicator.ShowCount == 1 && indicator.HideCount == 1, "loading: hide always runs, even on server failure");
        }

        static async Task LoadingHook_PreCheckFail_NeverShows()
        {
            var indicator = new ProbeLoadingIndicator();
            var (ctx, _, _) = NewContext();
            ctx.Gate = ServerOperationGate.WithLoading(indicator);
            var op = new RecordingOperation(ctx, new List<string>())
            {
                PreCheckResult = () => ServerOperationResult<ProbeOpResult>.Failure((ProbeOpResult)1, "reject")
            };

            await op.RunAsync();

            TestKit.Check(indicator.ShowCount == 0 && indicator.HideCount == 0, "loading: instant local reject shows no spinner");
        }

        static void SuccessSink_And_Analytics_FireOnSuccess()
        {
            var (ctx, _, _) = NewContext();
            var sink = new ProbeSuccessSink();
            var analytics = new ProbeAnalytics();
            ctx.SuccessSink = sink;
            ctx.Analytics = analytics;

            var run = new RecordingOperation(ctx, new List<string>()).RunAsync().GetAwaiter().GetResult();

            TestKit.Check(run.Accepted, "sink: run completed");
            TestKit.Check(sink.Count == 1 && sink.Last.IsSuccess, "sink: success emitter fired once on success");
            TestKit.Check(analytics.Count == 1 && analytics.LastSuccess, "analytics: outcome recorded on success");
        }

        // ---- code-toast failure presenter ----

        static void CodeToast_DefinedCode_ShowsEnumMemberLabel()
        {
            var toast = new ProbeToastPresenter();
            new CodeToastFailurePresenter<ProbeOpResult>(toast).PresentFailure(
                ServerOperationResult<ProbeOpResult>.Failure(ProbeOpResult.InsufficientBalance, "insufficient balance"));

            TestKit.Check(toast.Count == 1 && toast.Last == "[ProbeOpResult.InsufficientBalance] insufficient balance",
                "code-toast: a defined code shows the [<EnumType>.<Member>] diagnostic label");
        }

        static void CodeToast_UnknownCode_ShowsRawNumberLabel()
        {
            var toast = new ProbeToastPresenter();
            new CodeToastFailurePresenter<ProbeOpResult>(toast).PresentFailure(
                ServerOperationResult<ProbeOpResult>.Failure((ProbeOpResult)999, "unmapped code"));

            TestKit.Check(toast.Count == 1 && toast.Last == "[ProbeOpResult.999] unmapped code",
                "code-toast: an out-of-range code shows the raw number in the label instead of a blank");
        }

        static async Task AmbientHost_ResolvesContext_FromCurrent()
        {
            var factory = new ProbeTransportFactory();
            var channels = new ProbeResultChannels();
            var analytics = new ProbeAnalytics();
            var previous = ServerOperationHost.Current;
            ServerOperationHost.Current = new ServerOperationHost(factory, channels)
            {
                Gate = ServerOperationGate.DedupOnly(),
                Analytics = analytics,
            };

            try
            {
                var op = new AmbientRecordingOperation();     // NO explicit context — resolved from the ambient host
                var run = await op.RunAsync();

                TestKit.Check(run.Accepted && run.Result.IsSuccess, "ambient: flow ran to success with a host-resolved context");
                TestKit.Check(factory.Transport.SendCount == 1, "ambient: the host's transport factory supplied the transport (sent once)");
                TestKit.Check(channels.Sink.Count == 1, "ambient: the host's success sink fired");
                TestKit.Check(analytics.Count == 1 && analytics.LastSuccess, "ambient: the host's analytics recorded the outcome");
                TestKit.Check(op.Log.Contains("apply"), "ambient: the lifecycle applied the success");
            }
            finally
            {
                ServerOperationHost.Current = previous;
            }
        }
    }
}
