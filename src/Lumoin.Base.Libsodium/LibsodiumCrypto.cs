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

    /// <summary>The length in bytes of an XChaCha20-Poly1305 key (<c>crypto_aead_xchacha20poly1305_ietf_KEYBYTES</c>).</summary>
    public const int XChaCha20Poly1305KeyLength = 32;

    /// <summary>The length in bytes of an XChaCha20-Poly1305 public nonce (<c>crypto_aead_xchacha20poly1305_ietf_NPUBBYTES</c>).</summary>
    public const int XChaCha20Poly1305NonceLength = 24;

    /// <summary>The length in bytes of the Poly1305 tag appended in combined mode (<c>crypto_aead_xchacha20poly1305_ietf_ABYTES</c>).</summary>
    public const int XChaCha20Poly1305TagLength = 16;

    /// <summary>The length in bytes of an ML-KEM-768 public key (<c>crypto_kem_mlkem768_PUBLICKEYBYTES</c>).</summary>
    public const int MlKem768PublicKeyLength = 1184;

    /// <summary>The length in bytes of an ML-KEM-768 secret key (<c>crypto_kem_mlkem768_SECRETKEYBYTES</c>).</summary>
    public const int MlKem768SecretKeyLength = 2400;

    /// <summary>The length in bytes of an ML-KEM-768 ciphertext (<c>crypto_kem_mlkem768_CIPHERTEXTBYTES</c>).</summary>
    public const int MlKem768CiphertextLength = 1088;

    /// <summary>The length in bytes of an ML-KEM-768 shared secret (<c>crypto_kem_mlkem768_SHAREDSECRETBYTES</c>).</summary>
    public const int MlKem768SharedSecretLength = 32;

    /// <summary>The length in bytes of an ML-KEM-768 keypair seed (<c>crypto_kem_mlkem768_SEEDBYTES</c>).</summary>
    public const int MlKem768SeedLength = 64;

    /// <summary>The length in bytes of an X-Wing public key (<c>crypto_kem_xwing_PUBLICKEYBYTES</c>).</summary>
    public const int XWingPublicKeyLength = 1216;

    /// <summary>
    /// The length in bytes of an X-Wing secret key (<c>crypto_kem_xwing_SECRETKEYBYTES</c>) —
    /// X-Wing secret keys are seed-form, expanded internally on use.
    /// </summary>
    public const int XWingSecretKeyLength = 32;

    /// <summary>The length in bytes of an X-Wing ciphertext (<c>crypto_kem_xwing_CIPHERTEXTBYTES</c>).</summary>
    public const int XWingCiphertextLength = 1120;

    /// <summary>The length in bytes of an X-Wing shared secret (<c>crypto_kem_xwing_SHAREDSECRETBYTES</c>).</summary>
    public const int XWingSharedSecretLength = 32;

    /// <summary>The length in bytes of an X-Wing keypair seed (<c>crypto_kem_xwing_SEEDBYTES</c>).</summary>
    public const int XWingSeedLength = 32;


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
        return AllocateSecretScratch(scratchPool, Ed25519SecretKeyLength, failureMessage);
    }


    /// <summary>
    /// Rents <paramref name="length"/> bytes of scratch memory from the caller-composed
    /// <paramref name="scratchPool"/> for secret material handed to the <c>nint</c> crypto
    /// operations — the Ed25519 expanded secret key (<see cref="AllocateSecretKeyScratch"/>), an
    /// ML-KEM-768 secret key (<see cref="MlKem768SecretKeyLength"/>), or an X-Wing secret key
    /// (<see cref="XWingSecretKeyLength"/>). Callers <c>Pin</c> the returned owner's
    /// <see cref="IMemoryOwner{T}.Memory"/> to obtain the raw pointer, then dispose the owner once
    /// the operation completes.
    /// </summary>
    /// <param name="scratchPool">
    /// The pool the scratch region is rented from; the pool's backing decides the protection
    /// posture, exactly as documented on <see cref="AllocateSecretKeyScratch"/>.
    /// </param>
    /// <param name="length">The number of bytes to rent; must be positive.</param>
    /// <param name="failureMessage">
    /// The message to surface if the pool fails to allocate the region, giving the call site's context.
    /// </param>
    /// <returns>A scratch owner exactly <paramref name="length"/> bytes long.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">
    /// The pool failed to allocate the region; the original failure is the inner exception.
    /// </exception>
    public static IMemoryOwner<byte> AllocateSecretScratch(MemoryPool<byte> scratchPool, int length, string failureMessage)
    {
        //Argument validation precedes the initialization gate so it holds on hosts without libsodium.
        ArgumentNullException.ThrowIfNull(scratchPool);
        ArgumentNullException.ThrowIfNull(failureMessage);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        EnsureInitialized();

        try
        {
            return scratchPool.Rent(length);
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
    /// XChaCha20-Poly1305 (IETF) authenticated encryption in combined mode
    /// (<c>crypto_aead_xchacha20poly1305_ietf_encrypt</c>): seals <paramref name="message"/> under
    /// <paramref name="key"/> and <paramref name="nonce"/>, authenticating
    /// <paramref name="associatedData"/> alongside, and appends the Poly1305 tag to the ciphertext.
    /// </summary>
    /// <param name="ciphertext">
    /// Receives the sealed message: exactly <paramref name="message"/> length +
    /// <see cref="XChaCha20Poly1305TagLength"/> bytes.
    /// </param>
    /// <param name="message">The plaintext.</param>
    /// <param name="associatedData">Additional authenticated (not encrypted) data; may be empty.</param>
    /// <param name="nonce">
    /// The <see cref="XChaCha20Poly1305NonceLength"/>-byte public nonce. XChaCha20's 24-byte nonce
    /// is large enough to draw randomly per message (<see cref="RandomBytes"/>).
    /// </param>
    /// <param name="key">The <see cref="XChaCha20Poly1305KeyLength"/>-byte key.</param>
    /// <returns><c>0</c> on success.</returns>
    public static int AeadXChaCha20Poly1305Encrypt(Span<byte> ciphertext, ReadOnlySpan<byte> message, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> key)
    {
        EnsureInitialized();
        return NativeMethods.AeadXChaCha20Poly1305Encrypt(ciphertext, 0, message, (ulong)message.Length, associatedData, (ulong)associatedData.Length, 0, nonce, key);
    }


    /// <summary>
    /// XChaCha20-Poly1305 (IETF) authenticated decryption in combined mode
    /// (<c>crypto_aead_xchacha20poly1305_ietf_decrypt</c>); the tag is verified before any
    /// plaintext is released.
    /// </summary>
    /// <param name="message">
    /// Receives the plaintext: exactly <paramref name="ciphertext"/> length -
    /// <see cref="XChaCha20Poly1305TagLength"/> bytes.
    /// </param>
    /// <param name="ciphertext">The sealed message with its appended tag.</param>
    /// <param name="associatedData">The additional authenticated data the message was sealed with; may be empty.</param>
    /// <param name="nonce">The <see cref="XChaCha20Poly1305NonceLength"/>-byte public nonce used at encryption.</param>
    /// <param name="key">The <see cref="XChaCha20Poly1305KeyLength"/>-byte key.</param>
    /// <returns><c>0</c> when the tag verifies; <c>-1</c> otherwise.</returns>
    public static int AeadXChaCha20Poly1305Decrypt(Span<byte> message, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> key)
    {
        EnsureInitialized();
        return NativeMethods.AeadXChaCha20Poly1305Decrypt(message, 0, 0, ciphertext, (ulong)ciphertext.Length, associatedData, (ulong)associatedData.Length, nonce, key);
    }


    /// <summary>
    /// Derives an ML-KEM-768 (FIPS 203) keypair deterministically from a
    /// <see cref="MlKem768SeedLength"/>-byte seed (<c>crypto_kem_mlkem768_seed_keypair</c>).
    /// </summary>
    /// <param name="publicKey">Receives the <see cref="MlKem768PublicKeyLength"/>-byte public key.</param>
    /// <param name="secretKeyScratch">
    /// A pointer to <see cref="MlKem768SecretKeyLength"/> bytes of scratch memory (normally pinned
    /// from <see cref="AllocateSecretScratch"/>) that receives the secret key.
    /// </param>
    /// <param name="seed">The <see cref="MlKem768SeedLength"/>-byte seed.</param>
    /// <returns><c>0</c> on success.</returns>
    public static int MlKem768SeedKeypair(Span<byte> publicKey, nint secretKeyScratch, ReadOnlySpan<byte> seed)
    {
        EnsureInitialized();
        return NativeMethods.MlKem768SeedKeypair(publicKey, secretKeyScratch, seed);
    }


    /// <summary>
    /// Generates a random ML-KEM-768 (FIPS 203) keypair (<c>crypto_kem_mlkem768_keypair</c>).
    /// </summary>
    /// <param name="publicKey">Receives the <see cref="MlKem768PublicKeyLength"/>-byte public key.</param>
    /// <param name="secretKeyScratch">
    /// A pointer to <see cref="MlKem768SecretKeyLength"/> bytes of scratch memory (normally pinned
    /// from <see cref="AllocateSecretScratch"/>) that receives the secret key.
    /// </param>
    /// <returns><c>0</c> on success.</returns>
    public static int MlKem768Keypair(Span<byte> publicKey, nint secretKeyScratch)
    {
        EnsureInitialized();
        return NativeMethods.MlKem768Keypair(publicKey, secretKeyScratch);
    }


    /// <summary>
    /// ML-KEM-768 encapsulation (<c>crypto_kem_mlkem768_enc</c>): derives a fresh shared secret
    /// and the ciphertext that transports it to the holder of <paramref name="publicKey"/>.
    /// </summary>
    /// <param name="ciphertext">Receives the <see cref="MlKem768CiphertextLength"/>-byte ciphertext.</param>
    /// <param name="sharedSecret">Receives the <see cref="MlKem768SharedSecretLength"/>-byte shared secret.</param>
    /// <param name="publicKey">The peer's <see cref="MlKem768PublicKeyLength"/>-byte public key.</param>
    /// <returns><c>0</c> on success.</returns>
    public static int MlKem768Encapsulate(Span<byte> ciphertext, Span<byte> sharedSecret, ReadOnlySpan<byte> publicKey)
    {
        EnsureInitialized();
        return NativeMethods.MlKem768Encapsulate(ciphertext, sharedSecret, publicKey);
    }


    /// <summary>
    /// ML-KEM-768 decapsulation (<c>crypto_kem_mlkem768_dec</c>). FIPS 203 implicit rejection: an
    /// invalid ciphertext still returns <c>0</c> and yields a pseudorandom shared secret that will
    /// not match the encapsulator's — validity is never signaled through the return code, so
    /// protocols must authenticate the derived secret (e.g. through the AEAD that consumes it).
    /// </summary>
    /// <param name="sharedSecret">Receives the <see cref="MlKem768SharedSecretLength"/>-byte shared secret.</param>
    /// <param name="ciphertext">The <see cref="MlKem768CiphertextLength"/>-byte ciphertext.</param>
    /// <param name="secretKeyScratch">
    /// A pointer to the <see cref="MlKem768SecretKeyLength"/>-byte secret key in caller-composed
    /// scratch memory.
    /// </param>
    /// <returns><c>0</c> on success.</returns>
    public static int MlKem768Decapsulate(Span<byte> sharedSecret, ReadOnlySpan<byte> ciphertext, nint secretKeyScratch)
    {
        EnsureInitialized();
        return NativeMethods.MlKem768Decapsulate(sharedSecret, ciphertext, secretKeyScratch);
    }


    /// <summary>
    /// Derives an X-Wing (ML-KEM-768 + X25519 hybrid) keypair deterministically from an
    /// <see cref="XWingSeedLength"/>-byte seed (<c>crypto_kem_xwing_seed_keypair</c>).
    /// </summary>
    /// <param name="publicKey">Receives the <see cref="XWingPublicKeyLength"/>-byte public key.</param>
    /// <param name="secretKeyScratch">
    /// A pointer to <see cref="XWingSecretKeyLength"/> bytes of scratch memory (normally pinned
    /// from <see cref="AllocateSecretScratch"/>) that receives the seed-form secret key.
    /// </param>
    /// <param name="seed">The <see cref="XWingSeedLength"/>-byte seed.</param>
    /// <returns><c>0</c> on success.</returns>
    public static int XWingSeedKeypair(Span<byte> publicKey, nint secretKeyScratch, ReadOnlySpan<byte> seed)
    {
        EnsureInitialized();
        return NativeMethods.XWingSeedKeypair(publicKey, secretKeyScratch, seed);
    }


    /// <summary>
    /// Generates a random X-Wing (ML-KEM-768 + X25519 hybrid) keypair (<c>crypto_kem_xwing_keypair</c>).
    /// </summary>
    /// <param name="publicKey">Receives the <see cref="XWingPublicKeyLength"/>-byte public key.</param>
    /// <param name="secretKeyScratch">
    /// A pointer to <see cref="XWingSecretKeyLength"/> bytes of scratch memory (normally pinned
    /// from <see cref="AllocateSecretScratch"/>) that receives the seed-form secret key.
    /// </param>
    /// <returns><c>0</c> on success.</returns>
    public static int XWingKeypair(Span<byte> publicKey, nint secretKeyScratch)
    {
        EnsureInitialized();
        return NativeMethods.XWingKeypair(publicKey, secretKeyScratch);
    }


    /// <summary>
    /// X-Wing encapsulation (<c>crypto_kem_xwing_enc</c>): derives a fresh shared secret and the
    /// ciphertext that transports it to the holder of <paramref name="publicKey"/>.
    /// </summary>
    /// <param name="ciphertext">Receives the <see cref="XWingCiphertextLength"/>-byte ciphertext.</param>
    /// <param name="sharedSecret">Receives the <see cref="XWingSharedSecretLength"/>-byte shared secret.</param>
    /// <param name="publicKey">The peer's <see cref="XWingPublicKeyLength"/>-byte public key.</param>
    /// <returns><c>0</c> on success.</returns>
    public static int XWingEncapsulate(Span<byte> ciphertext, Span<byte> sharedSecret, ReadOnlySpan<byte> publicKey)
    {
        EnsureInitialized();
        return NativeMethods.XWingEncapsulate(ciphertext, sharedSecret, publicKey);
    }


    /// <summary>
    /// X-Wing decapsulation (<c>crypto_kem_xwing_dec</c>). Like ML-KEM-768, rejection is implicit:
    /// an invalid ciphertext still returns <c>0</c> with a shared secret that will not match the
    /// encapsulator's.
    /// </summary>
    /// <param name="sharedSecret">Receives the <see cref="XWingSharedSecretLength"/>-byte shared secret.</param>
    /// <param name="ciphertext">The <see cref="XWingCiphertextLength"/>-byte ciphertext.</param>
    /// <param name="secretKeyScratch">
    /// A pointer to the <see cref="XWingSecretKeyLength"/>-byte secret key in caller-composed
    /// scratch memory.
    /// </param>
    /// <returns><c>0</c> on success.</returns>
    public static int XWingDecapsulate(Span<byte> sharedSecret, ReadOnlySpan<byte> ciphertext, nint secretKeyScratch)
    {
        EnsureInitialized();
        return NativeMethods.XWingDecapsulate(sharedSecret, ciphertext, secretKeyScratch);
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
