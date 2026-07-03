using AiRecall.Core.Util;

namespace AiRecall.Core.Tests.Util;

public class HashingTests
{
    [Fact]
    public void Sha256_OfEmpty_IsKnownConstant()
    {
        var hash = Hashing.Sha256(Array.Empty<byte>());
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }

    [Fact]
    public void Sha256_OfSameBytes_IsDeterministic()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var a = Hashing.Sha256(data);
        var b = Hashing.Sha256(data);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Sha256_OfDifferentBytes_Differs()
    {
        var a = Hashing.Sha256(new byte[] { 1, 2, 3 });
        var b = Hashing.Sha256(new byte[] { 1, 2, 4 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Sha256_IsLowercaseHex()
    {
        var hash = Hashing.Sha256(new byte[] { 0xAB, 0xCD });
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }
}
