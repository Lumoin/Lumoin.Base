using System.Buffers;
using System.Runtime.InteropServices;

namespace Lumoin.Base.Libsodium;

/// <summary>
/// The raw libsodium crypto surface for the Lumoin family: Ed25519 seed-keypair generation,
/// detached signing and verification, Ed25519-to-X25519 conversion and X25519 scalar
/// multiplication, exposed as thin wrappers over the native entry points with libsodium's own
/// return-code semantics. Every operation forces the one-time <c>sodium_init</c> gate first.
/// </summary>
/// <remarks>
/// <para>
/// Secret material crosses this surface only as <see cref="Span{T}"/>/<see cref="ReadOnlySpan{T}"/>
/// or as a raw pointer into caller-composed scratch memory — never as a naked <c>byte[]</c>.
/// </para>
/// <para>
/// The scratch memory that holds libsodium's 64-byte expanded Ed25519 secret key form is composed
/// by the CALLER as a <see cref="MemoryPool{T}"/> (see <see cref="AllocateSecretKeyScratch"/>):
/// a non-browser consumer composes guarded or locked native backing (the family memory-protection
/// packages) and the expanded key never touches managed memory; a browser-wasm consumer composes a
/// managed or pinned pool, where the honest posture is zero-on-return — all wasm linear memory is
/// JS-visible, and that difference is the caller's explicit choice, never silently implied parity.
/// </para>
/// </remarks>
public static class LibsodiumCrypto
{
    /// <summary>The length in bytes of an Ed25519 public key (<c>crypto_sign_PUBLICKEYBYTES</c>).</summary>
    public const int Ed25519PublicKeyLength = 32;

    /// <summary>
    /// The length in bytes of libsodium's expanded Ed25519 secret key form (<c>crypto_sign_SECRETKEYBYTES</c>)
    /// — the RFC 8032 seed concatenated with the public key. The expanded form is produced and consumed
    /// only inside caller-composed scratch memory (<see cref="AllocateSecretKeyScratch"/>) and is never
    /// returned, stored, or exposed by this binding.
    /// </summary>
    public const int Ed25519SecretKeyLength = 64;

    /// <summary>
    /// The length in bytes of an Ed25519 seed (<c>crypto_sign_SEEDBYTES</c>) — the Ed25519 private-key
    /// wire format used throughout the Lumoin family.
    /// </summary>
    public const int Ed25519SeedLength = 32;

    /// <summary>The length in bytes of a detached Ed25519 signature (<c>crypto_sign_BYTES</c>).</summary>
    public const int Ed25519SignatureLength = 64;

    /// <summary>The length in bytes of an X25519 private scalar (<c>crypto_scalarmult_SCALARBYTES</c>).</summary>
    public const int X25519ScalarLength = 32;

    /// <summary>
    /// The length in bytes of an X25519 curve point, public key or shared secret
    /// (<c>crypto_scalarmult_BYTES</c>).
    /// </summary>
    public const int X25519PointLength = 32;


    /// <summary>
    /// Gets a value confirming libsodium has completed its one-time <c>sodium_init</c> initialization.
    /// </summary>
    /// <remarks>
    /// This property has a non-trivial initializer, so the C# compiler emits an explicit static
    /// constructor for this type. The CLR guarantees that constructor runs exactly once, is mutually
    /// exclusive across threads, and completes before the first access to any static member of this
    /// type — giving <c>sodium_init</c> the thread-safe, run-once gate libsodium requires without any
    /// additional manual locking. <see cref="EnsureInitialized"/> is the call-site-friendly entry point.
    /// </remarks>
    private static bool Initialized { get; } = InitializeSodium();


    /// <summary>
    /// Forces libsodium's one-time initialization gate (<see cref="Initialized"/>) to run before any
    /// other native call. Every operation on this type calls this first; consumers may also call it
    /// eagerly at composition time to surface a missing or broken native library early.
    /// </summary>
    public static void EnsureInitialized()
    {
        _ = Initialized;
    }


    /// <summary>
    /// Returns libsodium's compiled-in version string (e.g. <c>"1.0.22"</c>). Safe to call before
    /// <see cref="EnsureInitialized"/>: the underlying native call returns a compiled-in constant
    /// and touches no library state.
    /// </summary>
    /// <returns>The version string, or <c>"unknown"</c> if the native call returned no data.</returns>
    public static string GetVersionString()
    {
        return Marshal.PtrToStringUTF8(NativeMethods.VersionString()) ?? "unknown";
    }


    /// <summary>
    /// Rents <see cref="Ed25519SecretKeyLength"/> bytes of scratch memory from the caller-composed
    /// <paramref name="scratchPool"/>, for expanding an RFC 8032 seed into libsodium's internal
    /// 64-byte secret-key form. Callers <c>Pin</c> the returned owner's
    /// <see cref="IMemoryOwner{T}.Memory"/> to obtain the raw pointer the <c>nint</c> crypto
    /// operations expect, then dispose the owner once signing/keygen/conversion completes.
    /// </summary>
    /// <param name="scratchPool">
    /// The pool the scratch region is rented from. The pool's backing decides the protection
    /// posture: guarded or locked native backing keeps the expanded key out of managed memory
    /// entirely; a managed or pinned pool (the browser-wasm posture) relies on the pool's
    /// zero-on-return contract instead. Use a dedicated scratch pool, not the pool that output key
    /// material is rented from, so the two postures can differ.
    /// </param>
    /// <param name="failureMessage">
    /// The message to surface if the pool fails to allocate the region, giving the call site's
    /// context (key generation, signing, conversion).
    /// </param>
    /// <returns>A scratch owner exactly <see cref="Ed25519SecretKeyLength"/> bytes long.</returns>
    /// <exception cref="InvalidOperationException">
    /// The pool failed to allocate the region; the original failure is the inner exception.
    /// </exception>
    public static IMemoryOwner<byte> AllocateSecretKeyScratch(MemoryPool<byte> scratchPool, string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(scratchPool);
        ArgumentNullException.ThrowIfNull(failureMessage);
        EnsureInitialized();

        try
        {
            return scratchPool.Rent(Ed25519SecretKeyLength);
        }
        catch(InvalidOperationException ex)
        {
            throw new InvalidOperationException(failureMessage, ex);
        }
    }


    /// <summary>
    /// Fills <paramref name="buffer"/> with cryptographically secure random bytes drawn from
    /// libsodium's random number generator (<c>randombytes_buf</c>).
    /// </summary>
    /// <param name="buffer">The buffer to fill.</param>
    public static void RandomBytes(Span<byte> buffer)
    {
        EnsureInitialized();
        NativeMethods.RandomBytes(buffer, (nuint)buffer.Length);
    }


    /// <summary>
    /// Expands an <see cref="Ed25519SeedLength"/>-byte RFC 8032 seed into an Ed25519 public key and
    /// libsodium's expanded secret key form (<c>crypto_sign_seed_keypair</c>).
    /// </summary>
    /// <param name="publicKey">Receives the <see cref="Ed25519PublicKeyLength"/>-byte public key.</param>
    /// <param name="secretKeyScratch">
    /// A pointer to <see cref="Ed25519SecretKeyLength"/> bytes of scratch memory (normally pinned
    /// from <see cref="AllocateSecretKeyScratch"/>) that receives the expanded secret key form.
    /// This form must never be copied into managed memory.
    /// </param>
    /// <param name="seed">The <see cref="Ed25519SeedLength"/>-byte RFC 8032 seed.</param>
    /// <returns><c>0</c> on success.</returns>
    public static int SignSeedKeypair(Span<byte> publicKey, nint secretKeyScratch, ReadOnlySpan<byte> seed)
    {
        EnsureInitialized();
        return NativeMethods.SignSeedKeypair(publicKey, secretKeyScratch, seed);
    }


    /// <summary>
    /// Produces a detached Ed25519 signature per
    /// <see href="https://www.rfc-editor.org/rfc/rfc8032">RFC 8032</see>
    /// (<c>crypto_sign_detached</c>).
    /// </summary>
    /// <param name="signature">Receives the <see cref="Ed25519SignatureLength"/>-byte signature.</param>
    /// <param name="message">The message to sign.</param>
    /// <param name="secretKeyScratch">
    /// A pointer to the <see cref="Ed25519SecretKeyLength"/>-byte expanded secret key produced by
    /// <see cref="SignSeedKeypair"/> in caller-composed scratch memory.
    /// </param>
    /// <returns><c>0</c> on success.</returns>
    public static int SignDetached(Span<byte> signature, ReadOnlySpan<byte> message, nint secretKeyScratch)
    {
        EnsureInitialized();
        return NativeMethods.SignDetached(signature, 0, message, (ulong)message.Length, secretKeyScratch);
    }


    /// <summary>
    /// Verifies a detached Ed25519 signature per
    /// <see href="https://www.rfc-editor.org/rfc/rfc8032">RFC 8032</see>
    /// (<c>crypto_sign_verify_detached</c>).
    /// </summary>
    /// <param name="signature">The <see cref="Ed25519SignatureLength"/>-byte signature.</param>
    /// <param name="message">The message that was signed.</param>
    /// <param name="publicKey">The <see cref="Ed25519PublicKeyLength"/>-byte public key.</param>
    /// <returns><c>0</c> if the signature is valid; <c>-1</c> otherwise.</returns>
    public static int VerifyDetached(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message, ReadOnlySpan<byte> publicKey)
    {
        EnsureInitialized();
        return NativeMethods.VerifyDetached(signature, message, (ulong)message.Length, publicKey);
    }


    /// <summary>
    /// Computes the X25519 public point for a private scalar — <c>q = n * basepoint</c> — per
    /// <see href="https://www.rfc-editor.org/rfc/rfc7748">RFC 7748</see> §6.1
    /// (<c>crypto_scalarmult_base</c>).
    /// </summary>
    /// <param name="publicPoint">Receives the <see cref="X25519PointLength"/>-byte public point.</param>
    /// <param name="scalar">The <see cref="X25519ScalarLength"/>-byte private scalar.</param>
    /// <returns><c>0</c> on success.</returns>
    public static int ScalarMultBase(Span<byte> publicPoint, ReadOnlySpan<byte> scalar)
    {
        EnsureInitialized();
        return NativeMethods.ScalarMultBase(publicPoint, scalar);
    }


    /// <summary>
    /// Computes an X25519 Diffie-Hellman shared point — <c>q = n * p</c> — per
    /// <see href="https://www.rfc-editor.org/rfc/rfc7748">RFC 7748</see> §6.1
    /// (<c>crypto_scalarmult</c>).
    /// </summary>
    /// <param name="sharedPoint">Receives the <see cref="X25519PointLength"/>-byte shared point.</param>
    /// <param name="scalar">The <see cref="X25519ScalarLength"/>-byte private scalar.</param>
    /// <param name="peerPoint">The <see cref="X25519PointLength"/>-byte peer public point.</param>
    /// <returns><c>0</c> on success; <c>-1</c> if the result is the all-zero point (a low-order input).</returns>
    public static int ScalarMult(Span<byte> sharedPoint, ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> peerPoint)
    {
        EnsureInitialized();
        return NativeMethods.ScalarMult(sharedPoint, scalar, peerPoint);
    }


    /// <summary>
    /// Converts an Ed25519 public key to its birationally equivalent Montgomery-curve (X25519)
    /// public key (<c>crypto_sign_ed25519_pk_to_curve25519</c>).
    /// </summary>
    /// <param name="curve25519PublicKey">Receives the <see cref="X25519PointLength"/>-byte X25519 public key.</param>
    /// <param name="ed25519PublicKey">The <see cref="Ed25519PublicKeyLength"/>-byte Ed25519 public key.</param>
    /// <returns><c>0</c> on success.</returns>
    public static int PublicKeyToCurve25519(Span<byte> curve25519PublicKey, ReadOnlySpan<byte> ed25519PublicKey)
    {
        EnsureInitialized();
        return NativeMethods.PublicKeyToCurve25519(curve25519PublicKey, ed25519PublicKey);
    }


    /// <summary>
    /// Converts libsodium's expanded Ed25519 secret key form to its birationally equivalent
    /// Montgomery-curve (X25519) private scalar (<c>crypto_sign_ed25519_sk_to_curve25519</c>).
    /// </summary>
    /// <param name="curve25519SecretKey">Receives the <see cref="X25519ScalarLength"/>-byte X25519 private scalar.</param>
    /// <param name="ed25519SecretKeyScratch">
    /// A pointer to the <see cref="Ed25519SecretKeyLength"/>-byte expanded Ed25519 secret key in
    /// caller-composed scratch memory, matching how <see cref="SignSeedKeypair"/> and
    /// <see cref="SignDetached"/> receive it.
    /// </param>
    /// <returns><c>0</c> on success.</returns>
    public static int SecretKeyToCurve25519(Span<byte> curve25519SecretKey, nint ed25519SecretKeyScratch)
    {
        EnsureInitialized();
        return NativeMethods.SecretKeyToCurve25519(curve25519SecretKey, ed25519SecretKeyScratch);
    }


    /// <summary>
    /// Calls <c>sodium_init</c> and fails closed if it reports an error.
    /// </summary>
    /// <returns><see langword="true"/> once libsodium is confirmed initialized.</returns>
    /// <exception cref="InvalidOperationException"><c>sodium_init</c> returned a negative status.</exception>
    private static bool InitializeSodium()
    {
        int result = NativeMethods.Init();
        if(result < 0)
        {
            throw new InvalidOperationException($"libsodium failed to initialize: sodium_init() returned {result}.");
        }

        return true;
    }
}
