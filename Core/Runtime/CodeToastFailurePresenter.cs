namespace PFound.ServerOperationFlow.Core
{
    using System;

    /// <summary>
    /// A minimal failure destination: it labels a failed operation's typed <typeparamref name="TCode"/> result
    /// code and shows that label plus the diagnostic through a toast seam —
    /// <c>"[&lt;EnumType&gt;.&lt;Member&gt;] &lt;diagnostic&gt;"</c>. Because the code is the enum itself,
    /// <c>ResultCode.ToString()</c> already yields the member name for a defined value and the raw number for an
    /// out-of-range one, so an unmapped code stays legible instead of silently blank — no lookup helper needed.
    /// No localization: the text is developer-facing, so there is nothing to translate and no key to author. A
    /// shipping game that wants translated, user-facing copy swaps this for a presenter that resolves the code
    /// through its own localization instead.
    /// </summary>
    public sealed class CodeToastFailurePresenter<TCode> : IServerOperationFailurePresenter<ServerOperationResult<TCode>>
        where TCode : struct, Enum
    {
        readonly IToastPresenter _toast;

        public CodeToastFailurePresenter(IToastPresenter toast) => _toast = toast;

        public void PresentFailure(ServerOperationResult<TCode> result)
            => _toast.Show($"[{typeof(TCode).Name}.{result.ResultCode}] {result.ErrorMessage}");
    }
}
