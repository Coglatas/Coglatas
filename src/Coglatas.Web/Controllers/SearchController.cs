using Coglatas.Application.Search;
using Coglatas.Application.Security.Redaction;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class SearchController(ISearchService search) : ControllerBase
{
    [HttpGet("api/search/message-authors")]
    [EnableRateLimiting("search")]
    public async Task<IActionResult> MessageAuthors(
        [FromQuery] MessageAuthorOptionsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await search.SearchMessageAuthorsAsync(request, cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(
                HttpContext,
                result.Value!,
                RedactionProfile.SearchSnippet,
                "Search",
                RedactionAuthorizationState.Allowed))
            : BadRequest(CanonicalErrorEnvelope.FromSensitiveResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                "MessageAuthorSearchFailed"));
    }

    [HttpGet("api/search")]
    [EnableRateLimiting("search")]
    public async Task<IActionResult> Search([FromQuery] SearchRequest request, CancellationToken cancellationToken)
    {
        var result = await search.SearchAsync(request, cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(
                HttpContext,
                result.Value!,
                RedactionProfile.SearchSnippet,
                "Search",
                RedactionAuthorizationState.Allowed))
            : BadRequest(CanonicalErrorEnvelope.FromSensitiveResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                "SearchFailed"));
    }
}
