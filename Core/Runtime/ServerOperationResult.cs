namespace PFound.ServerOperationFlow.Core
{
    /// <summary>
    /// A ready-made <see cref="IServerOperationResult"/> so a game (or a test) need not hand-roll one for
    /// the common case: a success flag, a typed <typeparamref name="TCode"/> result code, and an optional
    /// diagnostic message. The code is the game's OWN enum, so a presenter reads <see cref="ResultCode"/> as a
    /// named value rather than a bare number. Games are free to supply their own richer result type instead —
    /// the lifecycle only depends on the interface, which carries no code at all.
    /// </summary>
    public sealed class ServerOperationResult<TCode> : IServerOperationResult where TCode : struct, System.Enum
    {
        public bool IsSuccess { get; }
        public TCode ResultCode { get; }
        public string ErrorMessage { get; }

        ServerOperationResult(bool isSuccess, TCode resultCode, string errorMessage)
        {
            IsSuccess = isSuccess;
            ResultCode = resultCode;
            ErrorMessage = errorMessage;
        }

        public static ServerOperationResult<TCode> Success(TCode resultCode = default)
            => new ServerOperationResult<TCode>(true, resultCode, null);

        public static ServerOperationResult<TCode> Failure(TCode resultCode, string errorMessage = null)
            => new ServerOperationResult<TCode>(false, resultCode, errorMessage);
    }
}
