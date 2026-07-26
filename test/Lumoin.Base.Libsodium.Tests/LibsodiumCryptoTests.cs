using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Libsodium.Tests;

/// <summary>
/// Behavioral tests for <see cref="LibsodiumCrypto"/>: argument validation that holds regardless of
/// whether libsodium is present, and — gated behind <see cref="LibsodiumTestEnvironment.RequireSodium"/>
/// — the RFC 8032 test vector, tamper rejection, X25519 agreement and the caller-composed scratch
/// path against the real native library.
/// </summary>
[TestClass]
public sealed class LibsodiumCryptoTests
{
    public TestContext TestContext { get; set; } = null!;

    //RFC 8032 §7.1 test vector 1: the empty message signed with the first test secret key.
    private const string Rfc8032Vector1Seed = "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
    private const string Rfc8032Vector1PublicKey = "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";
    private const string Rfc8032Vector1Signature = "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b";


    [TestMethod]
    public void AllocateSecretKeyScratchValidatesArguments()
    {
        //Argument validation precedes the initialization gate (see LibsodiumCrypto), so this holds
        //on hosts without libsodium and runs unconditionally.
        Assert.ThrowsExactly<ArgumentNullException>(() => LibsodiumCrypto.AllocateSecretKeyScratch(null!, "message"));

        using var pool = new BaseMemoryPool();
        Assert.ThrowsExactly<ArgumentNullException>(() => LibsodiumCrypto.AllocateSecretKeyScratch(pool, null!));
    }


    [TestMethod]
    public void GetVersionStringReturnsNonEmpty()
    {
        LibsodiumTestEnvironment.RequireSodium();

        string version = LibsodiumCrypto.GetVersionString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(version), "The compiled-in libsodium version string should not be empty.");
    }


    [TestMethod]
    public void SignSeedKeypairMatchesRfc8032Vector()
    {
        LibsodiumTestEnvironment.RequireSodium();

        byte[] seed = Convert.FromHexString(Rfc8032Vector1Seed);
        byte[] expectedPublicKey = Convert.FromHexString(Rfc8032Vector1PublicKey);

        using var scratchPool = new BaseMemoryPool();
        using var scratchOwner = LibsodiumCrypto.AllocateSecretKeyScratch(scratchPool, "Scratch allocation failed in test.");
        using var scratchHandle = scratchOwner.Memory.Pin();

        nint scratch;
        unsafe
        {
            scratch = (nint)scratchHandle.Pointer;
        }

        Span<byte> publicKey = stackalloc byte[LibsodiumCrypto.Ed25519PublicKeyLength];
        int result = LibsodiumCrypto.SignSeedKeypair(publicKey, scratch, seed);

        Assert.AreEqual(0, result, "crypto_sign_seed_keypair should succeed.");
        Assert.IsTrue(publicKey.SequenceEqual(expectedPublicKey), "The derived public key must match the RFC 8032 test vector.");
    }


    [TestMethod]
    public void SignDetachedProducesRfc8032SignatureThatVerifies()
    {
        LibsodiumTestEnvironment.RequireSodium();

        byte[] seed = Convert.FromHexString(Rfc8032Vector1Seed);
        byte[] expectedSignature = Convert.FromHexString(Rfc8032Vector1Signature);

        using var scratchPool = new BaseMemoryPool();
        using var scratchOwner = LibsodiumCrypto.AllocateSecretKeyScratch(scratchPool, "Scratch allocation failed in test.");
        using var scratchHandle = scratchOwner.Memory.Pin();

        nint scratch;
        unsafe
        {
            scratch = (nint)scratchHandle.Pointer;
        }

        Span<byte> publicKey = stackalloc byte[LibsodiumCrypto.Ed25519PublicKeyLength];
        Assert.AreEqual(0, LibsodiumCrypto.SignSeedKeypair(publicKey, scratch, seed), "crypto_sign_seed_keypair should succeed.");

        Span<byte> signature = stackalloc byte[LibsodiumCrypto.Ed25519SignatureLength];
        ReadOnlySpan<byte> message = [];
        Assert.AreEqual(0, LibsodiumCrypto.SignDetached(signature, message, scratch), "crypto_sign_detached should succeed.");
        Assert.IsTrue(signature.SequenceEqual(expectedSignature), "The signature must match the RFC 8032 test vector.");

        Assert.AreEqual(0, LibsodiumCrypto.VerifyDetached(signature, message, publicKey), "A valid signature must verify.");
    }


    [TestMethod]
    public void VerifyDetachedRejectsTamperedSignature()
    {
        LibsodiumTestEnvironment.RequireSodium();

        byte[] publicKey = Convert.FromHexString(Rfc8032Vector1PublicKey);
        byte[] signature = Convert.FromHexString(Rfc8032Vector1Signature);

        signature[0] ^= 0x01;

        int result = LibsodiumCrypto.VerifyDetached(signature, [], publicKey);
        Assert.AreNotEqual(0, result, "A tampered signature must not verify.");

        //The untampered vector still verifies, proving the rejection above tested the tamper, not the setup.
        signature[0] ^= 0x01;
        Assert.AreEqual(0, LibsodiumCrypto.VerifyDetached(signature, [], publicKey), "The untampered vector signature must verify.");
    }


    [TestMethod]
    public void X25519SharedSecretsAgree()
    {
        LibsodiumTestEnvironment.RequireSodium();

        Span<byte> scalarA = stackalloc byte[LibsodiumCrypto.X25519ScalarLength];
        Span<byte> scalarB = stackalloc byte[LibsodiumCrypto.X25519ScalarLength];
        LibsodiumCrypto.RandomBytes(scalarA);
        LibsodiumCrypto.RandomBytes(scalarB);

        Span<byte> publicA = stackalloc byte[LibsodiumCrypto.X25519PointLength];
        Span<byte> publicB = stackalloc byte[LibsodiumCrypto.X25519PointLength];
        Assert.AreEqual(0, LibsodiumCrypto.ScalarMultBase(publicA, scalarA), "crypto_scalarmult_base should succeed for A.");
        Assert.AreEqual(0, LibsodiumCrypto.ScalarMultBase(publicB, scalarB), "crypto_scalarmult_base should succeed for B.");

        Span<byte> sharedA = stackalloc byte[LibsodiumCrypto.X25519PointLength];
        Span<byte> sharedB = stackalloc byte[LibsodiumCrypto.X25519PointLength];
        Assert.AreEqual(0, LibsodiumCrypto.ScalarMult(sharedA, scalarA, publicB), "crypto_scalarmult should succeed for A.");
        Assert.AreEqual(0, LibsodiumCrypto.ScalarMult(sharedB, scalarB, publicA), "crypto_scalarmult should succeed for B.");

        Assert.IsTrue(sharedA.SequenceEqual(sharedB), "Both sides of the X25519 exchange must derive the same shared point.");
    }


    [TestMethod]
    public void AllocateSecretKeyScratchReturnsExactSize()
    {
        LibsodiumTestEnvironment.RequireSodium();

        using var scratchPool = new BaseMemoryPool();
        using var owner = LibsodiumCrypto.AllocateSecretKeyScratch(scratchPool, "Scratch allocation failed in test.");

        Assert.HasCount(LibsodiumCrypto.Ed25519SecretKeyLength, owner.Memory,
            "The scratch owner must be exactly the expanded secret key length.");
    }


    [TestMethod]
    public void AllocateSecretKeyScratchWrapsPoolFailureWithCallSiteMessage()
    {
        LibsodiumTestEnvironment.RequireSodium();

        const string FailureMessage = "Scratch allocation failed for the frobnicating call site.";

        using var pool = new ThrowingPool();
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => LibsodiumCrypto.AllocateSecretKeyScratch(pool, FailureMessage));

        Assert.AreEqual(FailureMessage, exception.Message, "The wrapper must surface the call site's context.");
        Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException,
            "The pool's original failure must be preserved as the inner exception.");
    }


    //A pool whose Rent always fails, standing in for an exhausted or misconfigured scratch backing.
    private sealed class ThrowingPool : MemoryPool<byte>
    {
        public override int MaxBufferSize => int.MaxValue;


        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            throw new InvalidOperationException("Deliberate test pool failure.");
        }


        protected override void Dispose(bool disposing)
        {
        }
    }
}
