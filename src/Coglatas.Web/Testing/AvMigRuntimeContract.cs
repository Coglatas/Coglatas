using System.Reflection;
using System.Text.Json;
using Coglatas.Web.Realtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace Coglatas.Web.Testing;

/// <summary>Inspects the actual composed endpoints without starting database workers.</summary>
internal static class AvMigRuntimeContract
{
    public static async Task VerifyAsync(WebApplication app, string policyPath)
    {
        Require(app.Environment.IsEnvironment("Test"), "Contract inspection is Test-only.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(policyPath));
        var signalR = document.RootElement.GetProperty("nonOpenApiContracts").GetProperty("signalR");
        var expectedPath = signalR.GetProperty("path").GetString()!;
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>().Where(e => e.Metadata.GetMetadata<HubMetadata>()?.HubType == typeof(AppHub)).ToArray();
        Require(endpoints.Length == 2, "Expected AppHub transport and negotiate endpoints.");
        Require(endpoints.Select(e => e.RoutePattern.RawText).ToHashSet().SetEquals([expectedPath, expectedPath + "/negotiate"]), "AppHub runtime route drift.");
        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Require((await schemes.GetDefaultAuthenticateSchemeAsync())?.Name == CookieAuthenticationDefaults.AuthenticationScheme, "Default authenticate scheme drift.");
        Require((await schemes.GetDefaultChallengeSchemeAsync())?.Name == CookieAuthenticationDefaults.AuthenticationScheme, "Default challenge scheme drift.");
        var provider = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        foreach (var endpoint in endpoints)
        {
            Require(endpoint.Metadata.GetMetadata<IAllowAnonymous>() == null, "AppHub runtime endpoint permits anonymous access.");
            var data = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Require(data.Count > 0, "AppHub endpoint has no authorization metadata.");
            var policy = await AuthorizationPolicy.CombineAsync(provider, data, endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            Require(policy != null, "AppHub effective policy is missing.");
            Require(policy!.AuthenticationSchemes.Count == 0 || policy.AuthenticationSchemes.SequenceEqual([CookieAuthenticationDefaults.AuthenticationScheme]), "AppHub effective policy scheme drift.");
            Require(policy.Requirements is [DenyAnonymousAuthorizationRequirement], "AppHub effective policy must require an authenticated user.");
        }
        Require(!typeof(AppHub).GetCustomAttributes(true).OfType<IAllowAnonymous>().Any(), "AppHub class permits anonymous access.");
        var methods = typeof(AppHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var expectedNames = signalR.GetProperty("clientMethods").EnumerateArray().Select(item => item.GetString()!).Concat(["OnConnectedAsync", "OnDisconnectedAsync"]).ToHashSet();
        Require(methods.Length == expectedNames.Count && methods.Select(m => m.Name).ToHashSet().SetEquals(expectedNames), "Runtime callable Hub method surface drift.");
        foreach (var required in signalR.GetProperty("clientMethodSignatures").EnumerateArray())
        {
            var matches = methods.Where(m => m.Name == required.GetProperty("name").GetString() && !m.IsGenericMethod).ToArray();
            Require(matches.Length == 1, "Runtime callable Hub method missing or overloaded.");
            var method = matches[0];
            Require(method.GetCustomAttribute<HubMethodNameAttribute>() == null, "Runtime Hub method alias drift.");
            Require(!method.GetCustomAttributes(true).Any(a => a is IAuthorizeData or IAllowAnonymous), "Runtime Hub method authorization drift.");
            Require(TypeName(method.ReturnType) == required.GetProperty("returnType").GetString(), "Runtime Hub return type drift.");
            Require(method.GetParameters().Select(p => TypeName(p.ParameterType)).SequenceEqual(required.GetProperty("parameterTypes").EnumerateArray().Select(p => p.GetString())), "Runtime Hub parameter drift.");
        }
        Console.WriteLine("AV-MIG runtime contract passed: composed endpoints, effective policy, default schemes and callable Hub methods.");
    }
    private static string TypeName(Type type) => type.IsGenericType
        ? type.Name.Split('`')[0] + "<" + string.Join(",", type.GenericTypeArguments.Select(TypeName)) + ">"
        : type == typeof(string) ? "string" : type.Name;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
