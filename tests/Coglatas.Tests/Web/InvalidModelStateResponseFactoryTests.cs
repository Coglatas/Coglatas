using Coglatas.Web.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Coglatas.Tests.Web;

public sealed class InvalidModelStateResponseFactoryTests
{
    [Fact]
    public void GenericValidationFailureReturnsBadRequestWhenMvcDefaultFactoryIsUnavailable()
    {
        var services = new ServiceCollection();
        services.AddWebServices(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ApiBehaviorOptions>>().Value;
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            Request = { Path = "/api/files/00000000-0000-0000-0000-000000000001/move" }
        };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        actionContext.ModelState.AddModelError("expectedDestinationVersion", "The field must be non-negative.");

        var result = options.InvalidModelStateResponseFactory(actionContext);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var details = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Equal(
            "The field must be non-negative.",
            Assert.Single(details.Errors["expectedDestinationVersion"]));
    }

    [Fact]
    public void CommunicationPollingValidationDoesNotReflectAttackerControlledValues()
    {
        var services = new ServiceCollection();
        services.AddWebServices(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ApiBehaviorOptions>>().Value;
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            Request = { Path = "/api/communication/poll/updates" }
        };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());

        const string scannerPayload = "4111111111111111";
        actionContext.ModelState.AddModelError(
            "WorkspaceId",
            $"The value '{scannerPayload}' is not valid for WorkspaceId.");

        var result = options.InvalidModelStateResponseFactory(actionContext);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var details = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Equal("The supplied value is invalid.", Assert.Single(details.Errors["WorkspaceId"]));
        Assert.DoesNotContain(
            scannerPayload,
            System.Text.Json.JsonSerializer.Serialize(details),
            StringComparison.Ordinal);
    }
}
