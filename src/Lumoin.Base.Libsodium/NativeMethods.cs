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
}
