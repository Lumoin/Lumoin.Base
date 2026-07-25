using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lumoin.Base.Libsodium;

/// <summary>
/// The raw libsodium crypto entry points behind <see cref="LibsodiumCrypto"/>. The module name
/// <c>libsodium</c> resolves per platform to <c>libsodium.dll</c>, <c>libsodium.so</c> or
/// <c>libsodium.dylib</c> through the standard .NET native library probing (application directory,
/// <c>runtimes/&lt;rid&gt;/native</c> package assets, OS loader paths); a host or test harness can
/// override resolution with
/// <see cref="NativeLibrary.SetDllImportResolver(System.Reflection.Assembly, DllImportResolver)"/>
/// on this assembly. On browser-wasm the same imports are satisfied by a <c>libsodium.a</c>
/// statically linked into <c>dotnet.wasm</c> at publish, keyed by this same module name.
/// </summary>
/// <remarks>
/// All libsodium exports use the cdecl calling convention (<c>SODIUM_EXPORT</c>), declared
/// explicitly so the win-x86 flavor does not silently marshal through the platform default.
/// Every import restricts the Windows loader to <see cref="DllImportSearchPath.SafeDirectories"/>
/// so a libsodium.dll planted in the current working directory can never be picked up.
/// </remarks>
internal static partial class NativeMethods
{
    /// <summary>
    /// The libsodium module name handed to the .NET native library loader.
    /// </summary>
    private const string LibraryName = "libsodium";


    /// <summary>
    /// Initializes libsodium. Thread-safe and idempotent; every other entry point requires a
    /// successful initialization first.
    /// </summary>
    /// <returns>
    /// <c>0</c> on first successful initialization, <c>1</c> when already initialized,
    /// <c>-1</c> on failure.
    /// </returns>
    [LibraryImport(LibraryName, EntryPoint = "sodium_init")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int Init();


    /// <summary>
    /// Returns a pointer to libsodium's static, null-terminated, UTF-8 version string. The pointer
    /// refers to library-owned static storage and must not be freed by the caller.
    /// </summary>
    /// <returns>A pointer to the version string.</returns>
    [LibraryImport(LibraryName, EntryPoint = "sodium_version_string")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint VersionString();


    /// <summary>
    /// Fills <paramref name="buffer"/> with cryptographically secure random bytes drawn from
    /// libsodium's random number generator.
    /// </summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="size">The number of bytes to fill; must equal <paramref name="buffer"/>'s length.</param>
    [LibraryImport(LibraryName, EntryPoint = "randombytes_buf")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial void RandomBytes(Span<byte> buffer, nuint size);


    /// <summary>
    /// Expands a <see cref="LibsodiumCrypto.Ed25519SeedLength"/>-byte RFC 8032 seed into an Ed25519
    /// public key and libsodium's <see cref="LibsodiumCrypto.Ed25519SecretKeyLength"/>-byte expanded
    /// secret key form (seed || public key).
    /// </summary>
    /// <param name="publicKey">Receives the <see cref="LibsodiumCrypto.Ed25519PublicKeyLength"/>-byte public key.</param>
    /// <param name="secretKey">
    /// A pointer to <see cref="LibsodiumCrypto.Ed25519SecretKeyLength"/> bytes of caller-composed
    /// scratch memory that receives the expanded secret key form. This form must never be copied
    /// into managed memory.
    /// </param>
    /// <param name="seed">The <see cref="LibsodiumCrypto.Ed25519SeedLength"/>-byte RFC 8032 seed.</param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_sign_seed_keypair")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int SignSeedKeypair(Span<byte> publicKey, nint secretKey, ReadOnlySpan<byte> seed);


    /// <summary>
    /// Produces a detached Ed25519 signature per
    /// <see href="https://www.rfc-editor.org/rfc/rfc8032">RFC 8032</see>.
    /// </summary>
    /// <param name="signature">Receives the <see cref="LibsodiumCrypto.Ed25519SignatureLength"/>-byte signature.</param>
    /// <param name="signatureLengthPointer">
    /// Optional pointer to receive the actual signature length; pass zero since Ed25519 signatures
    /// are always exactly <see cref="LibsodiumCrypto.Ed25519SignatureLength"/> bytes.
    /// </param>
    /// <param name="message">The message to sign.</param>
    /// <param name="messageLength">The message length in bytes.</param>
    /// <param name="secretKey">
    /// A pointer to the <see cref="LibsodiumCrypto.Ed25519SecretKeyLength"/>-byte expanded secret key.
    /// </param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_sign_detached")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int SignDetached(Span<byte> signature, nint signatureLengthPointer, ReadOnlySpan<byte> message, ulong messageLength, nint secretKey);


    /// <summary>
    /// Verifies a detached Ed25519 signature per
    /// <see href="https://www.rfc-editor.org/rfc/rfc8032">RFC 8032</see>.
    /// </summary>
    /// <param name="signature">The <see cref="LibsodiumCrypto.Ed25519SignatureLength"/>-byte signature.</param>
    /// <param name="message">The message that was signed.</param>
    /// <param name="messageLength">The message length in bytes.</param>
    /// <param name="publicKey">The <see cref="LibsodiumCrypto.Ed25519PublicKeyLength"/>-byte public key.</param>
    /// <returns><c>0</c> if the signature is valid; <c>-1</c> otherwise.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_sign_verify_detached")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int VerifyDetached(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message, ulong messageLength, ReadOnlySpan<byte> publicKey);


    /// <summary>
    /// Computes the X25519 public point for a private scalar — <c>q = n * basepoint</c> — per
    /// <see href="https://www.rfc-editor.org/rfc/rfc7748">RFC 7748</see> §6.1.
    /// </summary>
    /// <param name="publicPoint">Receives the <see cref="LibsodiumCrypto.X25519PointLength"/>-byte public point.</param>
    /// <param name="scalar">The <see cref="LibsodiumCrypto.X25519ScalarLength"/>-byte private scalar.</param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_scalarmult_base")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int ScalarMultBase(Span<byte> publicPoint, ReadOnlySpan<byte> scalar);


    /// <summary>
    /// Computes an X25519 Diffie-Hellman shared point — <c>q = n * p</c> — per
    /// <see href="https://www.rfc-editor.org/rfc/rfc7748">RFC 7748</see> §6.1.
    /// </summary>
    /// <param name="sharedPoint">Receives the <see cref="LibsodiumCrypto.X25519PointLength"/>-byte shared point.</param>
    /// <param name="scalar">The <see cref="LibsodiumCrypto.X25519ScalarLength"/>-byte private scalar.</param>
    /// <param name="peerPoint">The <see cref="LibsodiumCrypto.X25519PointLength"/>-byte peer public point.</param>
    /// <returns><c>0</c> on success; <c>-1</c> if the result is the all-zero point (a low-order input).</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_scalarmult")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int ScalarMult(Span<byte> sharedPoint, ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> peerPoint);


    /// <summary>
    /// Converts an Ed25519 public key to its birationally equivalent Montgomery-curve (X25519)
    /// public key.
    /// </summary>
    /// <param name="curve25519PublicKey">Receives the <see cref="LibsodiumCrypto.X25519PointLength"/>-byte X25519 public key.</param>
    /// <param name="ed25519PublicKey">The <see cref="LibsodiumCrypto.Ed25519PublicKeyLength"/>-byte Ed25519 public key.</param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_sign_ed25519_pk_to_curve25519")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int PublicKeyToCurve25519(Span<byte> curve25519PublicKey, ReadOnlySpan<byte> ed25519PublicKey);


    /// <summary>
    /// Converts libsodium's expanded Ed25519 secret key form to its birationally equivalent
    /// Montgomery-curve (X25519) private scalar.
    /// </summary>
    /// <param name="curve25519SecretKey">Receives the <see cref="LibsodiumCrypto.X25519ScalarLength"/>-byte X25519 private scalar.</param>
    /// <param name="ed25519SecretKey">
    /// A pointer to the <see cref="LibsodiumCrypto.Ed25519SecretKeyLength"/>-byte expanded Ed25519
    /// secret key in caller-composed scratch memory, matching how <see cref="SignSeedKeypair"/> and
    /// <see cref="SignDetached"/> receive it.
    /// </param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_sign_ed25519_sk_to_curve25519")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int SecretKeyToCurve25519(Span<byte> curve25519SecretKey, nint ed25519SecretKey);


    /// <summary>
    /// XChaCha20-Poly1305 (IETF) authenticated encryption in combined mode: the Poly1305 tag is
    /// appended to the ciphertext.
    /// </summary>
    /// <param name="ciphertext">Receives message length + <see cref="LibsodiumCrypto.XChaCha20Poly1305TagLength"/> bytes.</param>
    /// <param name="ciphertextLengthPointer">Optional pointer to receive the ciphertext length; pass zero.</param>
    /// <param name="message">The plaintext.</param>
    /// <param name="messageLength">The plaintext length in bytes.</param>
    /// <param name="associatedData">Additional authenticated data; may be empty.</param>
    /// <param name="associatedDataLength">The additional data length in bytes.</param>
    /// <param name="notUsedSecretNonce">Unused (<c>nsec</c>); pass zero.</param>
    /// <param name="nonce">The <see cref="LibsodiumCrypto.XChaCha20Poly1305NonceLength"/>-byte public nonce.</param>
    /// <param name="key">The <see cref="LibsodiumCrypto.XChaCha20Poly1305KeyLength"/>-byte key.</param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_aead_xchacha20poly1305_ietf_encrypt")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int AeadXChaCha20Poly1305Encrypt(Span<byte> ciphertext, nint ciphertextLengthPointer, ReadOnlySpan<byte> message, ulong messageLength, ReadOnlySpan<byte> associatedData, ulong associatedDataLength, nint notUsedSecretNonce, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> key);


    /// <summary>
    /// XChaCha20-Poly1305 (IETF) authenticated decryption in combined mode; verifies the appended
    /// Poly1305 tag before releasing any plaintext.
    /// </summary>
    /// <param name="message">Receives ciphertext length - <see cref="LibsodiumCrypto.XChaCha20Poly1305TagLength"/> bytes.</param>
    /// <param name="messageLengthPointer">Optional pointer to receive the plaintext length; pass zero.</param>
    /// <param name="notUsedSecretNonce">Unused (<c>nsec</c>); pass zero.</param>
    /// <param name="ciphertext">The ciphertext with the appended tag.</param>
    /// <param name="ciphertextLength">The ciphertext length in bytes.</param>
    /// <param name="associatedData">The additional authenticated data the message was sealed with; may be empty.</param>
    /// <param name="associatedDataLength">The additional data length in bytes.</param>
    /// <param name="nonce">The <see cref="LibsodiumCrypto.XChaCha20Poly1305NonceLength"/>-byte public nonce.</param>
    /// <param name="key">The <see cref="LibsodiumCrypto.XChaCha20Poly1305KeyLength"/>-byte key.</param>
    /// <returns><c>0</c> when the tag verifies; <c>-1</c> otherwise (no plaintext is released).</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_aead_xchacha20poly1305_ietf_decrypt")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int AeadXChaCha20Poly1305Decrypt(Span<byte> message, nint messageLengthPointer, nint notUsedSecretNonce, ReadOnlySpan<byte> ciphertext, ulong ciphertextLength, ReadOnlySpan<byte> associatedData, ulong associatedDataLength, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> key);


    /// <summary>
    /// Derives an ML-KEM-768 (FIPS 203) keypair deterministically from a
    /// <see cref="LibsodiumCrypto.MlKem768SeedLength"/>-byte seed.
    /// </summary>
    /// <param name="publicKey">Receives the <see cref="LibsodiumCrypto.MlKem768PublicKeyLength"/>-byte public key.</param>
    /// <param name="secretKey">
    /// A pointer to <see cref="LibsodiumCrypto.MlKem768SecretKeyLength"/> bytes of caller-composed
    /// scratch memory that receives the secret key.
    /// </param>
    /// <param name="seed">The <see cref="LibsodiumCrypto.MlKem768SeedLength"/>-byte seed.</param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_kem_mlkem768_seed_keypair")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int MlKem768SeedKeypair(Span<byte> publicKey, nint secretKey, ReadOnlySpan<byte> seed);


    /// <summary>
    /// Generates a random ML-KEM-768 (FIPS 203) keypair.
    /// </summary>
    /// <param name="publicKey">Receives the <see cref="LibsodiumCrypto.MlKem768PublicKeyLength"/>-byte public key.</param>
    /// <param name="secretKey">
    /// A pointer to <see cref="LibsodiumCrypto.MlKem768SecretKeyLength"/> bytes of caller-composed
    /// scratch memory that receives the secret key.
    /// </param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_kem_mlkem768_keypair")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int MlKem768Keypair(Span<byte> publicKey, nint secretKey);


    /// <summary>
    /// ML-KEM-768 encapsulation: derives a fresh shared secret and its ciphertext against a peer
    /// public key.
    /// </summary>
    /// <param name="ciphertext">Receives the <see cref="LibsodiumCrypto.MlKem768CiphertextLength"/>-byte ciphertext.</param>
    /// <param name="sharedSecret">Receives the <see cref="LibsodiumCrypto.MlKem768SharedSecretLength"/>-byte shared secret.</param>
    /// <param name="publicKey">The peer's <see cref="LibsodiumCrypto.MlKem768PublicKeyLength"/>-byte public key.</param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_kem_mlkem768_enc")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int MlKem768Encapsulate(Span<byte> ciphertext, Span<byte> sharedSecret, ReadOnlySpan<byte> publicKey);


    /// <summary>
    /// ML-KEM-768 decapsulation. Implements FIPS 203 implicit rejection: an invalid ciphertext
    /// still returns <c>0</c> and yields a pseudorandom shared secret that will not match the
    /// encapsulator's — it never signals validity through the return code.
    /// </summary>
    /// <param name="sharedSecret">Receives the <see cref="LibsodiumCrypto.MlKem768SharedSecretLength"/>-byte shared secret.</param>
    /// <param name="ciphertext">The <see cref="LibsodiumCrypto.MlKem768CiphertextLength"/>-byte ciphertext.</param>
    /// <param name="secretKey">A pointer to the <see cref="LibsodiumCrypto.MlKem768SecretKeyLength"/>-byte secret key in caller-composed scratch memory.</param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_kem_mlkem768_dec")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int MlKem768Decapsulate(Span<byte> sharedSecret, ReadOnlySpan<byte> ciphertext, nint secretKey);


    /// <summary>
    /// Derives an X-Wing (ML-KEM-768 + X25519 hybrid) keypair deterministically from a
    /// <see cref="LibsodiumCrypto.XWingSeedLength"/>-byte seed.
    /// </summary>
    /// <param name="publicKey">Receives the <see cref="LibsodiumCrypto.XWingPublicKeyLength"/>-byte public key.</param>
    /// <param name="secretKey">
    /// A pointer to <see cref="LibsodiumCrypto.XWingSecretKeyLength"/> bytes of caller-composed
    /// scratch memory that receives the secret key (X-Wing secret keys are seed-form).
    /// </param>
    /// <param name="seed">The <see cref="LibsodiumCrypto.XWingSeedLength"/>-byte seed.</param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_kem_xwing_seed_keypair")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int XWingSeedKeypair(Span<byte> publicKey, nint secretKey, ReadOnlySpan<byte> seed);


    /// <summary>
    /// Generates a random X-Wing (ML-KEM-768 + X25519 hybrid) keypair.
    /// </summary>
    /// <param name="publicKey">Receives the <see cref="LibsodiumCrypto.XWingPublicKeyLength"/>-byte public key.</param>
    /// <param name="secretKey">
    /// A pointer to <see cref="LibsodiumCrypto.XWingSecretKeyLength"/> bytes of caller-composed
    /// scratch memory that receives the secret key (X-Wing secret keys are seed-form).
    /// </param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_kem_xwing_keypair")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int XWingKeypair(Span<byte> publicKey, nint secretKey);


    /// <summary>
    /// X-Wing encapsulation: derives a fresh shared secret and its ciphertext against a peer
    /// public key.
    /// </summary>
    /// <param name="ciphertext">Receives the <see cref="LibsodiumCrypto.XWingCiphertextLength"/>-byte ciphertext.</param>
    /// <param name="sharedSecret">Receives the <see cref="LibsodiumCrypto.XWingSharedSecretLength"/>-byte shared secret.</param>
    /// <param name="publicKey">The peer's <see cref="LibsodiumCrypto.XWingPublicKeyLength"/>-byte public key.</param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_kem_xwing_enc")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int XWingEncapsulate(Span<byte> ciphertext, Span<byte> sharedSecret, ReadOnlySpan<byte> publicKey);


    /// <summary>
    /// X-Wing decapsulation. Like ML-KEM, an invalid ciphertext is rejected implicitly: the call
    /// returns <c>0</c> with a shared secret that will not match the encapsulator's.
    /// </summary>
    /// <param name="sharedSecret">Receives the <see cref="LibsodiumCrypto.XWingSharedSecretLength"/>-byte shared secret.</param>
    /// <param name="ciphertext">The <see cref="LibsodiumCrypto.XWingCiphertextLength"/>-byte ciphertext.</param>
    /// <param name="secretKey">A pointer to the <see cref="LibsodiumCrypto.XWingSecretKeyLength"/>-byte secret key in caller-composed scratch memory.</param>
    /// <returns><c>0</c> on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "crypto_kem_xwing_dec")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int XWingDecapsulate(Span<byte> sharedSecret, ReadOnlySpan<byte> ciphertext, nint secretKey);
}
