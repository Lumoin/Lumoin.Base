// Browser-wasm smoke for the SHIPPED Lumoin.Base.Libsodium surface: unlike a unit-test run on a
// desktop host, this executes the binding inside dotnet.wasm with the family-built libsodium.a
// statically linked in. Exercises the same seams a browser consumer composes: BaseMemoryPool as
// the caller-supplied scratch pool, LibsodiumCrypto's raw operations, and both key-conversion
// routes. Returns the failure count as the exit code so the CI job fails mechanically.
using System.Buffers;
using Lumoin.Base;
using Lumoin.Base.Libsodium;

int pass = 0, fail = 0;

void Check(string name, bool ok)
{
    if (ok) { pass++; Console.WriteLine($"PASS {name}"); }
    else { fail++; Console.WriteLine($"FAIL {name}"); }
}

try
{
    LibsodiumCrypto.EnsureInitialized();
    Check("EnsureInitialized completes", true);
    Console.WriteLine($"INFO libsodium version {LibsodiumCrypto.GetVersionString()}");

    Span<byte> r1 = stackalloc byte[32];
    Span<byte> r2 = stackalloc byte[32];
    LibsodiumCrypto.RandomBytes(r1);
    LibsodiumCrypto.RandomBytes(r2);
    Check("RandomBytes yields nonzero, differing output",
        !r1.SequenceEqual(r2) && r1.IndexOfAnyExcept((byte)0) >= 0);

    //RFC 8032 §7.1 test vector 1 (empty message), through the shipped surface with the family pool
    //as the caller-composed scratch backing.
    byte[] seed = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
    byte[] expectedPk = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
    byte[] expectedSig = Convert.FromHexString("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

    using var scratchPool = new BaseMemoryPool();
    using var outputPool = new BaseMemoryPool();

    using var scratchOwner = LibsodiumCrypto.AllocateSecretKeyScratch(scratchPool, "Smoke scratch allocation failed.");
    Check("scratch owner is exactly the expanded key length",
        scratchOwner.Memory.Length == LibsodiumCrypto.Ed25519SecretKeyLength);

    using var scratchHandle = scratchOwner.Memory.Pin();
    nint scratch;
    unsafe
    {
        scratch = (nint)scratchHandle.Pointer;
    }

    Span<byte> publicKey = stackalloc byte[LibsodiumCrypto.Ed25519PublicKeyLength];
    Check("SignSeedKeypair returns 0", LibsodiumCrypto.SignSeedKeypair(publicKey, scratch, seed) == 0);
    Check("public key matches RFC 8032 vector", publicKey.SequenceEqual(expectedPk));

    Span<byte> signature = stackalloc byte[LibsodiumCrypto.Ed25519SignatureLength];
    ReadOnlySpan<byte> message = [];
    Check("SignDetached returns 0", LibsodiumCrypto.SignDetached(signature, message, scratch) == 0);
    Check("signature matches RFC 8032 vector", signature.SequenceEqual(expectedSig));
    Check("VerifyDetached accepts the valid signature", LibsodiumCrypto.VerifyDetached(signature, message, publicKey) == 0);

    Span<byte> tampered = stackalloc byte[LibsodiumCrypto.Ed25519SignatureLength];
    signature.CopyTo(tampered);
    tampered[0] ^= 0x01;
    Check("VerifyDetached rejects a tampered signature", LibsodiumCrypto.VerifyDetached(tampered, message, publicKey) != 0);

    //Both conversion routes must land on the same X25519 public key.
    using var curvePublicKeyOwner = LibsodiumKeyConversion.ConvertEd25519PublicKeyToCurve25519PublicKey(publicKey, outputPool);
    using var curvePrivateKeyOwner = LibsodiumKeyConversion.ConvertEd25519PrivateKeyToCurve25519PrivateKey(seed, outputPool, scratchPool);

    Span<byte> derivedPublicKey = stackalloc byte[LibsodiumCrypto.X25519PointLength];
    Check("ScalarMultBase on the converted scalar returns 0",
        LibsodiumCrypto.ScalarMultBase(derivedPublicKey, curvePrivateKeyOwner.Memory.Span) == 0);
    Check("both conversion routes agree", derivedPublicKey.SequenceEqual(curvePublicKeyOwner.Memory.Span));

    //XChaCha20-Poly1305 AEAD: round-trip and tamper rejection inside dotnet.wasm.
    Span<byte> aeadKey = stackalloc byte[LibsodiumCrypto.XChaCha20Poly1305KeyLength];
    Span<byte> aeadNonce = stackalloc byte[LibsodiumCrypto.XChaCha20Poly1305NonceLength];
    LibsodiumCrypto.RandomBytes(aeadKey);
    LibsodiumCrypto.RandomBytes(aeadNonce);
    ReadOnlySpan<byte> aeadMessage = "wasm smoke"u8;
    Span<byte> aeadCiphertext = stackalloc byte[aeadMessage.Length + LibsodiumCrypto.XChaCha20Poly1305TagLength];
    Check("AEAD encrypt returns 0",
        LibsodiumCrypto.AeadXChaCha20Poly1305Encrypt(aeadCiphertext, aeadMessage, default, aeadNonce, aeadKey) == 0);
    Span<byte> aeadDecrypted = stackalloc byte[aeadMessage.Length];
    Check("AEAD decrypt round-trips",
        LibsodiumCrypto.AeadXChaCha20Poly1305Decrypt(aeadDecrypted, aeadCiphertext, default, aeadNonce, aeadKey) == 0
        && aeadDecrypted.SequenceEqual(aeadMessage));
    aeadCiphertext[0] ^= 0x01;
    Check("AEAD rejects tampered ciphertext",
        LibsodiumCrypto.AeadXChaCha20Poly1305Decrypt(aeadDecrypted, aeadCiphertext, default, aeadNonce, aeadKey) != 0);

    //X-Wing hybrid KEM (which internally exercises ML-KEM-768 + X25519) inside dotnet.wasm.
    using var kemSecretOwner = LibsodiumCrypto.AllocateSecretScratch(
        scratchPool, LibsodiumCrypto.XWingSecretKeyLength, "Smoke KEM scratch allocation failed.");
    using var kemSecretHandle = kemSecretOwner.Memory.Pin();
    nint kemSecret;
    unsafe
    {
        kemSecret = (nint)kemSecretHandle.Pointer;
    }

    byte[] kemPublicKey = new byte[LibsodiumCrypto.XWingPublicKeyLength];
    Check("X-Wing keypair returns 0", LibsodiumCrypto.XWingKeypair(kemPublicKey, kemSecret) == 0);
    Span<byte> kemCiphertext = stackalloc byte[LibsodiumCrypto.XWingCiphertextLength];
    Span<byte> kemEncapsulated = stackalloc byte[LibsodiumCrypto.XWingSharedSecretLength];
    Check("X-Wing encapsulate returns 0", LibsodiumCrypto.XWingEncapsulate(kemCiphertext, kemEncapsulated, kemPublicKey) == 0);
    Span<byte> kemDecapsulated = stackalloc byte[LibsodiumCrypto.XWingSharedSecretLength];
    Check("X-Wing decapsulate returns 0", LibsodiumCrypto.XWingDecapsulate(kemDecapsulated, kemCiphertext, kemSecret) == 0);
    Check("X-Wing shared secrets agree", kemDecapsulated.SequenceEqual(kemEncapsulated));
}
catch (Exception ex)
{
    fail++;
    Console.WriteLine($"FAIL unhandled exception: {ex}");
}

Console.WriteLine(fail == 0 ? $"WASM-SMOKE-SUCCESS: {pass} passed" : $"WASM-SMOKE-FAILURE: {pass} passed, {fail} failed");
return fail;
