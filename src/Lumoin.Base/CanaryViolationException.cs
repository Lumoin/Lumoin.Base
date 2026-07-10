namespace Lumoin.Base;

/// <summary>
/// Thrown when a protected-slab segment's software canary is found stomped as the segment is
/// returned: something wrote outside the exact-size span that was rented — typically native
/// interop handed a pointer with a wrong length, or a genuine memory-corruption bug.
/// </summary>
/// <remarks>
/// <para>
/// The pool responds before throwing: the segment's contents are zeroed regardless, the segment
/// is retired (never recycled), and the violation is recorded on the rental's tracing activity
/// and the <c>Lumoin.BaseMemoryPool.CanaryViolationsTotal</c> counter. The exception then
/// surfaces from the rental owner's <see cref="IDisposable.Dispose"/> — deliberately loud, the
/// protected-slab counterpart of the guarded per-rent tier's process abort, but recoverable:
/// derived from <see cref="InvalidOperationException"/> so existing broad handlers still see it.
/// </para>
/// <para>
/// Detection is delayed by design. Software canaries are verified on return, not enforced by
/// hardware at write time — the trade that buys locked-memory density. See
/// <see cref="NativeRentMode.ProtectedSlab"/>.
/// </para>
/// </remarks>
public sealed class CanaryViolationException: InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance with a default message.
    /// </summary>
    public CanaryViolationException()
    {
    }

    /// <summary>
    /// Initializes a new instance with the given message.
    /// </summary>
    /// <param name="message">The message that describes the violation.</param>
    public CanaryViolationException(string message): base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with the given message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the violation.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public CanaryViolationException(string message, Exception innerException): base(message, innerException)
    {
    }
}
