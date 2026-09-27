using Coglatas.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class ConversationsController(IConversationService conversations) : ApiResultControllerBase
{
    [HttpGet("api/conversations")]
    public async Task<IActionResult> List([FromQuery] ConversationListQuery query, CancellationToken cancellationToken) => ToActionResult(await conversations.ListAsync(query, cancellationToken));

    [HttpGet("api/conversations/recipients")]
    public async Task<IActionResult> Recipients([FromQuery] string? query, CancellationToken cancellationToken) => ToActionResult(await conversations.ListRecipientsAsync(query, cancellationToken));

    [HttpPost("api/conversations")]
    public async Task<IActionResult> Create(CreateConversationRequest request, CancellationToken cancellationToken) => ToActionResult(await conversations.CreateAsync(request, cancellationToken));

    [HttpPost("api/conversations/direct")]
    public async Task<IActionResult> CreateDirect(CreateDirectConversationRequest request, CancellationToken cancellationToken) => ToActionResult(await conversations.CreateDirectAsync(request, cancellationToken));

    [HttpGet("api/conversations/{conversationId:guid}")]
    public async Task<IActionResult> Get(Guid conversationId, CancellationToken cancellationToken) => ToActionResult(await conversations.GetAsync(conversationId, cancellationToken));

    [HttpPatch("api/conversations/{conversationId:guid}")]
    public async Task<IActionResult> Update(Guid conversationId, UpdateConversationRequest request, CancellationToken cancellationToken) => ToActionResult(await conversations.UpdateAsync(conversationId, request, cancellationToken));

    [HttpPost("api/conversations/{conversationId:guid}/lock")]
    public async Task<IActionResult> Lock(Guid conversationId, ConversationLockRequest request, CancellationToken cancellationToken) => ToActionResult(await conversations.LockAsync(conversationId, request, cancellationToken));

    [HttpPost("api/conversations/{conversationId:guid}/unlock")]
    public async Task<IActionResult> Unlock(Guid conversationId, ConversationLockRequest request, CancellationToken cancellationToken) => ToActionResult(await conversations.UnlockAsync(conversationId, request, cancellationToken));

    [HttpPost("api/conversations/{conversationId:guid}/archive")]
    public async Task<IActionResult> Archive(Guid conversationId, CancellationToken cancellationToken) => ToActionResult(await conversations.ArchiveAsync(conversationId, cancellationToken));

    [HttpPost("api/conversations/{conversationId:guid}/report")]
    public async Task<IActionResult> ReportConversation(Guid conversationId, ConversationReportRequest request, CancellationToken cancellationToken) => OkOrBad(await conversations.ReportConversationAsync(conversationId, request, cancellationToken));

    [HttpPost("api/conversations/{conversationId:guid}/leave")]
    public async Task<IActionResult> Leave(Guid conversationId, CancellationToken cancellationToken) => OkOrBad(await conversations.LeaveAsync(conversationId, cancellationToken));

    [HttpGet("api/conversations/{conversationId:guid}/members")]
    public async Task<IActionResult> Members(Guid conversationId, CancellationToken cancellationToken) => ToActionResult(await conversations.ListMembersAsync(conversationId, cancellationToken));

    [HttpPost("api/conversations/{conversationId:guid}/members")]
    public async Task<IActionResult> AddMember(Guid conversationId, AddConversationMemberRequest request, CancellationToken cancellationToken) => ToActionResult(await conversations.AddMemberAsync(conversationId, request, cancellationToken));

    [HttpDelete("api/conversations/{conversationId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid conversationId, Guid userId, CancellationToken cancellationToken) => OkOrBad(await conversations.RemoveMemberAsync(conversationId, userId, cancellationToken));

    [HttpGet("api/conversations/{conversationId:guid}/messages")]
    public async Task<IActionResult> Messages(Guid conversationId, [FromQuery] MessageListQuery query, CancellationToken cancellationToken) => ToActionResult(await conversations.ListMessagesAsync(conversationId, query, cancellationToken));

    [HttpPost("api/conversations/{conversationId:guid}/messages")]
    public async Task<IActionResult> Send(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken) => ToActionResult(await conversations.SendMessageAsync(conversationId, request, cancellationToken));

    [HttpGet("api/messages/{messageId:guid}/thread")]
    public async Task<IActionResult> GetMessageThread(Guid messageId, [FromQuery] Guid? anchorReplyMessageId, CancellationToken cancellationToken) => ToActionResult(await conversations.GetMessageThreadAsync(messageId, anchorReplyMessageId, cancellationToken));

    [HttpPost("api/messages/{messageId:guid}/thread/messages")]
    public async Task<IActionResult> SendThreadMessage(Guid messageId, SendThreadMessageRequest request, CancellationToken cancellationToken) => ToActionResult(await conversations.SendThreadMessageAsync(messageId, request, cancellationToken));

    [HttpPatch("api/messages/{messageId:guid}")]
    public async Task<IActionResult> UpdateMessage(Guid messageId, UpdateMessageRequest request, CancellationToken cancellationToken) => ToActionResult(await conversations.UpdateMessageAsync(messageId, request, cancellationToken));

    [HttpDelete("api/messages/{messageId:guid}")]
    public async Task<IActionResult> DeleteMessage(Guid messageId, CancellationToken cancellationToken) => OkOrBad(await conversations.DeleteMessageAsync(messageId, cancellationToken));

    [HttpPost("api/messages/{messageId:guid}/report")]
    public async Task<IActionResult> ReportMessage(Guid messageId, MessageReportRequest request, CancellationToken cancellationToken) => OkOrBad(await conversations.ReportMessageAsync(messageId, request, cancellationToken));

    [HttpGet("api/conversations/{conversationId:guid}/state")]
    public async Task<IActionResult> State(Guid conversationId, CancellationToken cancellationToken) => ToActionResult(await conversations.GetParticipantStateAsync(conversationId, cancellationToken));

    [HttpPatch("api/conversations/{conversationId:guid}/state")]
    public async Task<IActionResult> UpdateState(Guid conversationId, UpdateParticipantStateRequest request, CancellationToken cancellationToken) => ToActionResult(await conversations.UpdateParticipantStateAsync(conversationId, request, cancellationToken));

    [HttpPost("api/conversations/{conversationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid conversationId, MarkConversationReadRequest request, CancellationToken cancellationToken) => OkOrBad(await conversations.MarkReadAsync(conversationId, request, cancellationToken));

}
