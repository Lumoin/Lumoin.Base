using System.Buffers;
using System.Security.Cryptography;

namespace Lumoin.Base.Libsodium;

/// <summary>
/// Converts between Ed25519 (signing) and X25519 (key-exchange) key material using libsodium's
/// birational Edwards-to-Montgomery curve mapping.
/// </summary>
public static class LibsodiumKeyConversion
{
    /// <summary>
    /// Converts an Ed25519 public key to its birationally equivalent X25519 public key.
    /// </summary>
    /// <param name="ed25519PublicKey">The 32-byte Ed25519 public key.</param>
    /// <param name="memoryPool">The pool to allocate the returned X25519 public key from.</param>
    /// <returns>A pooled buffer containing the 32-byte X25519 public key. The caller owns disposal.</returns>
    /// <exception cref="ArgumentException"><paramref name="ed25519PublicKey"/> is not exactly 32 bytes.</exception>
    /// <exception cref="CryptographicException">libsodium rejected the conversion (<c>ret != 0</c>).</exception>
    public static IMemoryOwner<byte> ConvertEd25519PublicKeyToCurve25519PublicKey(ReadOnlySpan<byte> ed25519PublicKey, MemoryPool<byte> memoryPool)
    {
        //Argument validation precedes the initialization gate so it holds on hosts without libsodium.
        ArgumentNullException.ThrowIfNull(memoryPool);
        if(ed25519PublicKey.Length != LibsodiumCrypto.Ed25519PublicKeyLength)
        {
            throw new ArgumentException(
                $"An Ed25519 public key must be exactly {LibsodiumCrypto.Ed25519PublicKeyLength} bytes.",
                nameof(ed25519PublicKey));
        }

        LibsodiumCrypto.EnsureInitialized();

        IMemoryOwner<byte> curve25519PublicKeyOwner = memoryPool.Rent(LibsodiumCrypto.X25519PointLength);
        int result = LibsodiumCrypto.PublicKeyToCurve25519(
            curve25519PublicKeyOwner.Memory.Span[..LibsodiumCrypto.X25519PointLength], ed25519PublicKey);
        if(result != 0)
        {
            curve25519PublicKeyOwner.Dispose();
            throw new CryptographicException("libsodium failed to convert the Ed25519 public key to its X25519 form.");
        }

        return curve25519PublicKeyOwner;
    }


    /// <summary>
    /// Converts an Ed25519 private key (the 32-byte RFC 8032 seed) to its birationally equivalent
    /// X25519 private scalar.
    /// </summary>
    /// <param name="ed25519PrivateKeySeed">The 32-byte RFC 8032 Ed25519 seed.</param>
    /// <param name="memoryPool">The pool to allocate the returned X25519 private scalar from.</param>
    /// <param name="scratchPool">
    /// The pool the transient expanded-secret-key scratch is rented from (see
    /// <see cref="LibsodiumCrypto.AllocateSecretKeyScratch"/>). Deliberately separate from
    /// <paramref name="memoryPool"/>: the scratch holds libsodium's 64-byte expanded secret key,
    /// whose protection posture (guarded native, locked, pinned or managed backing) is the
    /// caller's composition-time choice and need not match the output pool's.
    /// </param>
    /// <returns>A pooled buffer containing the 32-byte X25519 private scalar. The caller owns disposal.</returns>
    /// <exception cref="ArgumentException"><paramref name="ed25519PrivateKeySeed"/> is not exactly 32 bytes.</exception>
    /// <exception cref="InvalidOperationException">The scratch pool failed to allocate the scratch region.</exception>
    /// <exception cref="CryptographicException">
    /// libsodium failed to expand the seed into a keypair, or failed the conversion (<c>ret != 0</c>).
    /// </exception>
    /// <remarks>
    /// The seed is expanded into libsodium's 64-byte secret-key form inside a scratch owner from
    /// <see cref="LibsodiumCrypto.AllocateSecretKeyScratch"/>; the expanded form is reached only by
    /// pinning the owner's memory — with native-backed scratch it never touches managed memory —
    /// and the scratch owner is disposed (wiped per the pool's zero-on-return contract) before this
    /// method returns.
    /// </remarks>
    public static IMemoryOwner<byte> ConvertEd25519PrivateKeyToCurve25519PrivateKey(ReadOnlySpan<byte> ed25519PrivateKeySeed, MemoryPool<byte> memoryPool, MemoryPool<byte> scratchPool)
    {
        //Argument validation precedes the initialization gate so it holds on hosts without libsodium.
        ArgumentNullException.ThrowIfNull(memoryPool);
        ArgumentNullException.ThrowIfNull(scratchPool);
        if(ed25519PrivateKeySeed.Length != LibsodiumCrypto.Ed25519SeedLength)
        {
            throw new ArgumentException(
                $"An Ed25519 private key must be the {LibsodiumCrypto.Ed25519SeedLength}-byte RFC 8032 seed.",
                nameof(ed25519PrivateKeySeed));
        }

        LibsodiumCrypto.EnsureInitialized();

        IMemoryOwner<byte> curve25519PrivateKeyOwner = memoryPool.Rent(LibsodiumCrypto.X25519ScalarLength);

        try
        {
            using IMemoryOwner<byte> secretKeyScratchOwner = LibsodiumCrypto.AllocateSecretKeyScratch(
                scratchPool,
                "The scratch pool failed to allocate scratch memory for the Ed25519-to-X25519 key conversion.");
            using MemoryHandle secretKeyScratchHandle = secretKeyScratchOwner.Memory.Pin();

            nint secretKeyScratch;
            unsafe
            {
                secretKeyScratch = (nint)secretKeyScratchHandle.Pointer;
            }

            Span<byte> publicKeyScratch = stackalloc byte[LibsodiumCrypto.Ed25519PublicKeyLength];
            int keypairResult = LibsodiumCrypto.SignSeedKeypair(publicKeyScratch, secretKeyScratch, ed25519PrivateKeySeed);
            if(keypairResult != 0)
            {
                throw new CryptographicException("libsodium failed to expand the Ed25519 seed into a keypair.");
            }

            int conversionResult = LibsodiumCrypto.SecretKeyToCurve25519(
                curve25519PrivateKeyOwner.Memory.Span[..LibsodiumCrypto.X25519ScalarLength], secretKeyScratch);
            if(conversionResult != 0)
            {
                throw new CryptographicException("libsodium failed to convert the Ed25519 private key to its X25519 form.");
            }
        }
        catch
        {
            curve25519PrivateKeyOwner.Dispose();
            throw;
        }

        return curve25519PrivateKeyOwner;
    }
}
