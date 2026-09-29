using Coglatas.Web.Configuration;
using Microsoft.AspNetCore.Http;

namespace Coglatas.Tests.Security;

public sealed class TenantCookiePolicyTests
{
    [Theory]
    [InlineData(CookieSecurePolicy.Always, "http", true)]
    [InlineData(CookieSecurePolicy.Always, "https", true)]
    [InlineData(CookieSecurePolicy.SameAsRequest, "http", false)]
    [InlineData(CookieSecurePolicy.SameAsRequest, "https", true)]
    [InlineData(CookieSecurePolicy.None, "http", false)]
    [InlineData(CookieSecurePolicy.None, "https", false)]
    public void BuildUsesConfiguredSecureCookiePolicy(
        CookieSecurePolicy securePolicy,
        string requestScheme,
        bool expectedSecure)
    {
        var context = new DefaultHttpContext
        {
            Request = { Scheme = requestScheme }
        };

        var options = TenantCookiePolicy.Build(
            context,
            new SecurityOptions { CookieSecurePolicy = securePolicy });

        Assert.Equal(expectedSecure, options.Secure);
        Assert.True(options.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.SameSite);
        Assert.True(options.IsEssential);
    }
}
