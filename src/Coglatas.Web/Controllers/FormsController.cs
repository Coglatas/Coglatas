using Coglatas.Application.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class FormsController(IFormService formService) : ApiResultControllerBase
{
    [HttpGet("api/forms")]
    public async Task<IActionResult> List([FromQuery] FormListQuery query, CancellationToken cancellationToken) => ToActionResult(await formService.ListAsync(query, cancellationToken));

    [HttpPost("api/forms")]
    public async Task<IActionResult> Create(CreateFormRequest request, CancellationToken cancellationToken) => ToActionResult(await formService.CreateAsync(request, cancellationToken));

    [HttpGet("api/forms/{formId:guid}")]
    public async Task<IActionResult> Get(Guid formId, CancellationToken cancellationToken) => ToActionResult(await formService.GetAsync(formId, cancellationToken));

    [HttpPatch("api/forms/{formId:guid}")]
    public async Task<IActionResult> Update(Guid formId, UpdateFormRequest request, CancellationToken cancellationToken) => ToActionResult(await formService.UpdateAsync(formId, request, cancellationToken));

    [HttpDelete("api/forms/{formId:guid}")]
    public async Task<IActionResult> Delete(Guid formId, CancellationToken cancellationToken) => OkOrBad(await formService.DeleteAsync(formId, cancellationToken));

    [HttpPost("api/forms/{formId:guid}/open")]
    public async Task<IActionResult> Open(Guid formId, CancellationToken cancellationToken) => ToActionResult(await formService.OpenAsync(formId, cancellationToken));

    [HttpPost("api/forms/{formId:guid}/close")]
    public async Task<IActionResult> Close(Guid formId, CancellationToken cancellationToken) => ToActionResult(await formService.CloseAsync(formId, cancellationToken));

    [HttpGet("api/forms/{formId:guid}/questions")]
    public async Task<IActionResult> ListQuestions(Guid formId, CancellationToken cancellationToken) => ToActionResult(await formService.ListQuestionsAsync(formId, cancellationToken));

    [HttpPost("api/forms/{formId:guid}/questions")]
    public async Task<IActionResult> AddQuestion(Guid formId, CreateFormQuestionRequest request, CancellationToken cancellationToken) => ToActionResult(await formService.AddQuestionAsync(formId, request, cancellationToken));

    [HttpPatch("api/forms/{formId:guid}/questions/{questionId:guid}")]
    public async Task<IActionResult> UpdateQuestion(Guid formId, Guid questionId, UpdateFormQuestionRequest request, CancellationToken cancellationToken) => ToActionResult(await formService.UpdateQuestionAsync(formId, questionId, request, cancellationToken));

    [HttpDelete("api/forms/{formId:guid}/questions/{questionId:guid}")]
    public async Task<IActionResult> DeleteQuestion(Guid formId, Guid questionId, CancellationToken cancellationToken) => OkOrBad(await formService.DeleteQuestionAsync(formId, questionId, cancellationToken));

    [HttpPost("api/forms/{formId:guid}/responses")]
    public async Task<IActionResult> SubmitResponse(Guid formId, SubmitFormResponseRequest request, CancellationToken cancellationToken) => ToActionResult(await formService.SubmitResponseAsync(formId, request, cancellationToken));

    [HttpGet("api/forms/{formId:guid}/responses")]
    public async Task<IActionResult> ListResponses(Guid formId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        return ToActionResult(await formService.ListResponsesAsync(formId, page, pageSize, cancellationToken));
    }

    [HttpGet("api/forms/{formId:guid}/responses/me")]
    public async Task<IActionResult> GetMyResponse(Guid formId, CancellationToken cancellationToken) => ToActionResult(await formService.GetMyResponseAsync(formId, cancellationToken));

    [HttpGet("api/forms/{formId:guid}/summary")]
    public async Task<IActionResult> Summary(Guid formId, CancellationToken cancellationToken) => ToActionResult(await formService.GetSummaryAsync(formId, cancellationToken));

    [HttpGet("api/forms/{formId:guid}/unanswered-users")]
    public async Task<IActionResult> UnansweredUsers(Guid formId, CancellationToken cancellationToken) => ToActionResult(await formService.ListUnansweredUsersAsync(formId, cancellationToken));

}
