using DnnManager.Infrastructure.Iis;

namespace DnnManager.IntegrationTests;

/// <summary>IIS's binding information read apart - an edit keeps a binding's own IP address, IPv6 ones too.</summary>
[TestClass]
public sealed class IisBindingTests
{
    [TestMethod]
    [DataRow("*:80:shop.dnndev.me", "*")]
    [DataRow("127.0.0.2:80:shop.dnndev.me", "127.0.0.2")]
    [DataRow("[::1]:80:shop.dnndev.me", "[::1]")]
    [DataRow("[fe80::1%4]:8080:", "[fe80::1%4]")]
    [DataRow("808:*", "*")]
    public void TheAddress_IsAllBeforeThePort(string information, string address) =>
        Assert.AreEqual(address, IisManager.BindingAddress(information));
}
