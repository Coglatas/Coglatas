using System.Reflection;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Coglatas.Tests.Web;

public sealed class StrictQueryParameterFilterTests
{
    [Fact]
    public async Task RejectsUnknownQueryKeyWhileAllowingDeclaredQueryProperties()
    {
        var filter = new StrictQueryParameterFilter();
        var action = typeof(QueryFixture).GetMethod(nameof(QueryFixture.List), BindingFlags.Instance | BindingFlags.Public)!;
        var parameter = action.GetParameters().Single();
        var descriptor = new ControllerActionDescriptor
        {
            MethodInfo = action,
            Parameters = [new ControllerParameterDescriptor
            {
                Name = parameter.Name!,
                ParameterInfo = parameter
            }]
        };
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?page=1&x-schemathesis-unknown-property=42");
        var context = new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), descriptor),
            [],
            new Dictionary<string, object?>(),
            new QueryFixture());

        await filter.OnActionExecutionAsync(context, () => throw new InvalidOperationException("The action must not execute."));

        var badRequest = Assert.IsType<BadRequestObjectResult>(context.Result);
        Assert.IsType<ValidationProblemDetails>(badRequest.Value);
    }

    [Fact]
    public async Task AllowsDeclaredQueryKeysAndInvokesNext()
    {
        var filter = new StrictQueryParameterFilter();
        var action = typeof(QueryFixture).GetMethod(nameof(QueryFixture.List), BindingFlags.Instance | BindingFlags.Public)!;
        var parameter = action.GetParameters().Single();
        var descriptor = new ControllerActionDescriptor
        {
            MethodInfo = action,
            Parameters = [new ControllerParameterDescriptor
            {
                Name = parameter.Name!,
                ParameterInfo = parameter
            }]
        };
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?page=1&pageSize=25");
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        var controller = new QueryFixture();
        var context = new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            controller);
        var nextInvoked = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextInvoked = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], controller));
        });

        Assert.True(nextInvoked);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task RejectsBlankValueForDeclaredTimestampQueryProperty()
    {
        var filter = new StrictQueryParameterFilter();
        var action = typeof(QueryFixture).GetMethod(nameof(QueryFixture.ListTimed), BindingFlags.Instance | BindingFlags.Public)!;
        var parameter = action.GetParameters().Single();
        var descriptor = new ControllerActionDescriptor
        {
            MethodInfo = action,
            Parameters = [new ControllerParameterDescriptor
            {
                Name = parameter.Name!,
                ParameterInfo = parameter
            }]
        };
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?toDateExclusive=");
        var context = new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), descriptor),
            [],
            new Dictionary<string, object?>(),
            new QueryFixture());

        await filter.OnActionExecutionAsync(context, () => throw new InvalidOperationException("The action must not execute."));

        Assert.IsType<BadRequestObjectResult>(context.Result);
    }

    private sealed class QueryFixture
    {
        public void List([FromQuery] Query request)
        {
        }

        public void ListTimed([FromQuery] TimedQuery request)
        {
        }
    }

    private sealed record Query(int Page, int PageSize);
    private sealed record TimedQuery(DateTimeOffset? ToDateExclusive);
}
