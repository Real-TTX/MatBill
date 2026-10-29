using System.Net;
using MatBill.Core.Security;

namespace MatBill.Tests.Core;

/// <summary>Whose X-Forwarded-* headers are believed: by default only a proxy on this machine, the LAN or the Docker network.</summary>
public sealed class TrustedProxiesTests
{
    private static bool Trusted(IReadOnlyList<IPNetwork> networks, string address) =>
        networks.Any(network => network.Contains(IPAddress.Parse(address)));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.18.0.1", true)]     // the Docker network's gateway
    [InlineData("192.168.178.20", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::5", true)]
    [InlineData("203.0.113.9", false)]  // a caller on the internet
    [InlineData("172.32.0.1", false)]   // just outside 172.16.0.0/12
    [InlineData("8.8.8.8", false)]
    public void By_default_only_private_senders_are_believed(string address, bool expected)
    {
        var networks = TrustedProxies.Resolve(null, out var ignored);

        Assert.NotNull(networks);
        Assert.Empty(ignored);
        Assert.Equal(expected, Trusted(networks, address));
    }

    [Fact]
    public void A_configured_list_replaces_the_default_and_takes_addresses_and_ranges()
    {
        var networks = TrustedProxies.Resolve("203.0.113.9; 198.51.100.0/24\n2001:db8::1", out var ignored);

        Assert.NotNull(networks);
        Assert.Empty(ignored);
        Assert.True(Trusted(networks, "203.0.113.9"));
        Assert.True(Trusted(networks, "198.51.100.77"));
        Assert.True(Trusted(networks, "2001:db8::1"));
        Assert.False(Trusted(networks, "10.0.0.1")); // the private default is gone
    }

    [Fact]
    public void Entries_that_are_no_address_are_named_and_never_widen_the_list()
    {
        var networks = TrustedProxies.Resolve("proxy.example.com, 10.0.0.0/8", out var ignored);

        Assert.Equal(["proxy.example.com"], ignored);
        Assert.Single(networks!);
    }

    [Theory]
    [InlineData("any")]
    [InlineData("ANY")]
    [InlineData("*")]
    public void Any_trusts_every_sender(string configured) =>
        Assert.Null(TrustedProxies.Resolve(configured, out _));
}
