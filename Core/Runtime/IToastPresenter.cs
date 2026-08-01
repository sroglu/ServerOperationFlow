namespace PFound.ServerOperationFlow.Core
{
    /// <summary>
    /// The game-side destination a resolved failure message is shown through — a toast, a banner, an inline
    /// error line, whatever the game uses. The Core never picks the UI; it only hands over the finished string.
    /// </summary>
    public interface IToastPresenter
    {
        void Show(string message);
    }
}
