namespace PFound.ServerOperationFlow.Core
{
    using System;

    /// <summary>
    /// Marks the game's operation result-code enum — the <c>TCode</c> the ready-made
    /// <see cref="ServerOperationResult{TCode}"/> carries. The "Create ServerOperation flow" quick-fix reads this
    /// marker to scaffold a flow against <c>ServerOperationResult&lt;YourEnum&gt;</c> without guessing a type. Mark
    /// exactly one enum per networking assembly; its first member should be an <c>Invalid = 0</c> sentinel.
    /// </summary>
    [AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
    public sealed class OperationResultCodeAttribute : Attribute
    {
    }
}
