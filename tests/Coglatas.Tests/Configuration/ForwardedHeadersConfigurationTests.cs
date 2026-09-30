using System.Net;
using Coglatas.Web.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Coglatas.Tests.Configuration;

public sealed class ForwardedHeadersConfigurationTests
{
    [Fact]
    public void TrustedForwardedHeadersRequireAnExplicitProxyBoundary()
    {
        var configuration = CreateConfiguration(
            (ForwardedHeadersConfiguration.TrustForwardedHeadersKey, "true"));

        var errors = ForwardedHeadersConfiguration.GetConfigurationErrors(configuration);

        var error = Assert.Single(errors);
        Assert.Contains("requires at least one explicit", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("portal-proxy.internal", null, "TrustedProxies")]
    [InlineData(null, "not-a-cidr", "TrustedNetworks")]
    [InlineData("0.0.0.0", null, "TrustedProxies")]
    [InlineData(null, "0.0.0.0/0", "TrustedNetworks")]
    [InlineData(null, "::/0", "TrustedNetworks")]
    public void TrustedForwardedHeadersRejectInvalidTrustBoundaryValues(
        string? trustedProxy,
        string? trustedNetwork,
        string expectedSetting)
    {
        var values = new List<(string Key, string Value)>
        {
            (ForwardedHeadersConfiguration.TrustForwardedHeadersKey, "true")
        };
        if (trustedProxy is not null)
        {
            values.Add(($"{ForwardedHeadersConfiguration.TrustedProxiesKey}:0", trustedProxy));
        }

        if (trustedNetwork is not null)
        {
            values.Add(($"{ForwardedHeadersConfiguration.TrustedNetworksKey}:0", trustedNetwork));
        }

        var errors = ForwardedHeadersConfiguration.GetConfigurationErrors(CreateConfiguration([.. values]));

        Assert.Contains(errors, error => error.Contains(expectedSetting, StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurePreservesLoopbackDefaultsAndAddsOnlyConfiguredBoundaries()
    {
        var configuration = CreateConfiguration(
            (ForwardedHeadersConfiguration.TrustForwardedHeadersKey, "true"),
            ($"{ForwardedHeadersConfiguration.TrustedProxiesKey}:0", "192.0.2.10"),
            ($"{ForwardedHeadersConfiguration.TrustedNetworksKey}:0", "10.42.0.0/16"));
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersConfiguration.Configure(options, configuration);

        Assert.Empty(ForwardedHeadersConfiguration.GetConfigurationErrors(configuration));
        Assert.True(options.RequireHeaderSymmetry);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Contains(IPAddress.Parse("192.0.2.10"), options.KnownProxies);
        Assert.Contains(options.KnownIPNetworks, network =>
            network.BaseAddress.Equals(IPAddress.Parse("10.42.0.0")) && network.PrefixLength == 16);
        Assert.DoesNotContain(IPAddress.Any, options.KnownProxies);
        Assert.DoesNotContain(options.KnownIPNetworks, network =>
            network.BaseAddress.Equals(IPAddress.Any) && network.PrefixLength == 0);
    }

    [Fact]
    public void ConfigureCanDisableHeaderSymmetryWithoutWeakeningProxyBoundary()
    {
        var configuration = CreateConfiguration(
            (ForwardedHeadersConfiguration.TrustForwardedHeadersKey, "true"),
            (ForwardedHeadersConfiguration.RequireHeaderSymmetryKey, "false"),
            ($"{ForwardedHeadersConfiguration.TrustedProxiesKey}:0", "192.0.2.10"));
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersConfiguration.Configure(options, configuration);

        Assert.Empty(ForwardedHeadersConfiguration.GetConfigurationErrors(configuration));
        Assert.False(options.RequireHeaderSymmetry);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Contains(IPAddress.Parse("192.0.2.10"), options.KnownProxies);
        Assert.DoesNotContain(IPAddress.Any, options.KnownProxies);
    }

    [Fact]
    public void DisabledForwardedHeadersDoNotRequireAProxyBoundary()
    {
        var configuration = CreateConfiguration(
            (ForwardedHeadersConfiguration.TrustForwardedHeadersKey, "false"));

        Assert.Empty(ForwardedHeadersConfiguration.GetConfigurationErrors(configuration));
    }

    private static IConfiguration CreateConfiguration(params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value))
            .Build();
    }
}
