using System.Globalization;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Forms;

public sealed class FormService(
    IFormRepository forms,
    IWorkspaceRepository workspaces,
    IGroupRepository groups,
    IProjectRepository projects,
    IFormAuthorizationService authorization,
    ICurrentUser currentUser,
    IClock clock,
    IAuditLogger auditLogger,
    INotificationService notifications,
    IUnitOfWork unitOfWork) : IFormService
{
    private const int MaxPageSize = 100;

    public async Task<Result<PagedResponse<FormListItemResponse>>> ListAsync(FormListQuery query, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<PagedResponse<FormListItemResponse>>.Failure("Authentication is required.");
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var normalizedQuery = query with { Page = page, PageSize = pageSize };
        var candidates = await forms.ListAsync(normalizedQuery, cancellationToken);
        var visible = new List<InternalForm>();

        foreach (var form in candidates)
        {
            if (await authorization.CanViewForm(userId, form, cancellationToken))
            {
                visible.Add(form);
            }
        }

        var items = visible
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToListItem)
            .ToList();

        return Result<PagedResponse<FormListItemResponse>>.Success(new PagedResponse<FormListItemResponse>(items, page, pageSize, visible.Count));
    }

    public async Task<Result<FormDetailResponse>> CreateAsync(CreateFormRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<FormDetailResponse>.Failure("Authentication is required.");
        }

        if (request.Status == FormStatus.Open)
        {
            return Result<FormDetailResponse>.Failure("Create the form as a draft, add questions, then open it.");
        }

        var validation = await ValidateFormRequestAsync(
            request.WorkspaceId,
            request.GroupId,
            request.ProjectId,
            request.Title,
            request.OpensAt,
            request.ClosesAt,
            cancellationToken);
        if (!validation.IsSuccess)
        {
            return Result<FormDetailResponse>.Failure(validation.Error!);
        }

        if (!Enum.IsDefined(request.FormType) || !Enum.IsDefined(request.Status))
        {
            return Result<FormDetailResponse>.Failure("Form type or status is invalid.");
        }

        if (!await authorization.CanCreateForm(userId, request.WorkspaceId, request.GroupId, request.ProjectId, cancellationToken))
        {
            return Result<FormDetailResponse>.Failure("You are not allowed to create forms in the selected scope.");
        }

        var form = new InternalForm
        {
            WorkspaceId = request.WorkspaceId,
            GroupId = request.GroupId,
            ProjectId = request.ProjectId,
            CreatedByUserId = userId,
            Title = request.Title.Trim(),
            Description = NormalizeOptionalText(request.Description),
            FormType = request.FormType,
            Status = request.Status,
            OpensAt = request.OpensAt,
            ClosesAt = request.ClosesAt,
            IsAnonymous = request.IsAnonymous
        };

        await forms.AddAsync(form, cancellationToken);
        await auditLogger.LogUserActionAsync(userId, "FormCreated", "InternalForm", form.Id, "Form created.", cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var persisted = await forms.GetAsync(form.Id, cancellationToken) ?? form;
        return Result<FormDetailResponse>.Success(ToDetail(persisted));
    }

    public async Task<Result<FormDetailResponse>> GetAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result<FormDetailResponse>.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanViewForm(userId, form, cancellationToken))
        {
            return Result<FormDetailResponse>.Failure("Form not found.");
        }

        return Result<FormDetailResponse>.Success(ToDetail(form));
    }

    public async Task<Result<FormDetailResponse>> UpdateAsync(Guid formId, UpdateFormRequest request, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result<FormDetailResponse>.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanManageForm(userId, form, cancellationToken))
        {
            return Result<FormDetailResponse>.Failure("You are not allowed to update this form.");
        }

        var hasScopeUpdate = request.WorkspaceId.HasValue || request.GroupId.HasValue || request.ProjectId.HasValue;
        var nextWorkspaceId = hasScopeUpdate ? request.WorkspaceId : form.WorkspaceId;
        var nextGroupId = hasScopeUpdate ? request.GroupId : form.GroupId;
        var nextProjectId = hasScopeUpdate ? request.ProjectId : form.ProjectId;
        var nextTitle = request.Title ?? form.Title;
        var nextOpensAt = request.OpensAt ?? form.OpensAt;
        var nextClosesAt = request.ClosesAt ?? form.ClosesAt;

        var validation = await ValidateFormRequestAsync(nextWorkspaceId, nextGroupId, nextProjectId, nextTitle, nextOpensAt, nextClosesAt, cancellationToken);
        if (!validation.IsSuccess)
        {
            return Result<FormDetailResponse>.Failure(validation.Error!);
        }

        if (hasScopeUpdate && !await authorization.CanCreateForm(userId, nextWorkspaceId, nextGroupId, nextProjectId, cancellationToken))
        {
            return Result<FormDetailResponse>.Failure("You are not allowed to move this form to the selected scope.");
        }

        if (request.FormType.HasValue && !Enum.IsDefined(request.FormType.Value))
        {
            return Result<FormDetailResponse>.Failure("Form type is invalid.");
        }

        if (request.Status.HasValue && !Enum.IsDefined(request.Status.Value))
        {
            return Result<FormDetailResponse>.Failure("Form status is invalid.");
        }

        if (request.Status == FormStatus.Open && form.Questions.Count == 0)
        {
            return Result<FormDetailResponse>.Failure("A form must have at least one question before it can be opened.");
        }

        var previousStatus = form.Status;
        form.WorkspaceId = nextWorkspaceId;
        form.GroupId = nextGroupId;
        form.ProjectId = nextProjectId;
        form.Title = nextTitle.Trim();
        form.Description = request.Description is null ? form.Description : NormalizeOptionalText(request.Description);
        form.FormType = request.FormType ?? form.FormType;
        form.Status = request.Status ?? form.Status;
        form.OpensAt = nextOpensAt;
        form.ClosesAt = nextClosesAt;
        form.IsAnonymous = request.IsAnonymous ?? form.IsAnonymous;

        if (form is { Status: FormStatus.Archived, DeletedAt: null })
        {
            form.MarkDeleted(clock.UtcNow);
        }

        await auditLogger.LogUserActionAsync(userId, "FormUpdated", "InternalForm", form.Id, "Form updated.", cancellationToken: cancellationToken);
        if (form.Status == FormStatus.Open && previousStatus != FormStatus.Open)
        {
            await auditLogger.LogUserActionAsync(userId, "FormOpened", "InternalForm", form.Id, "Form opened.", cancellationToken: cancellationToken);
            await NotifyFormOpenedAsync(userId, form, cancellationToken);
        }

        if (form.Status == FormStatus.Closed && previousStatus != FormStatus.Closed)
        {
            await auditLogger.LogUserActionAsync(userId, "FormClosed", "InternalForm", form.Id, "Form closed.", cancellationToken: cancellationToken);
        }

        if (form.Status == FormStatus.Archived && previousStatus != FormStatus.Archived)
        {
            await auditLogger.LogUserActionAsync(userId, "FormArchived", "InternalForm", form.Id, "Form archived.", cancellationToken: cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<FormDetailResponse>.Success(ToDetail(form));
    }

    public async Task<Result> DeleteAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanManageForm(userId, form, cancellationToken))
        {
            return Result.Failure("You are not allowed to archive this form.");
        }

        form.Status = FormStatus.Archived;
        if (!form.DeletedAt.HasValue)
        {
            form.MarkDeleted(clock.UtcNow);
        }

        await auditLogger.LogUserActionAsync(userId, "FormArchived", "InternalForm", form.Id, "Form archived.", cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public Task<Result<FormDetailResponse>> OpenAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        return UpdateAsync(formId, new UpdateFormRequest(Status: FormStatus.Open), cancellationToken);
    }

    public Task<Result<FormDetailResponse>> CloseAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        return UpdateAsync(formId, new UpdateFormRequest(Status: FormStatus.Closed), cancellationToken);
    }

    public async Task<Result<IReadOnlyList<FormQuestionResponse>>> ListQuestionsAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result<IReadOnlyList<FormQuestionResponse>>.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanViewForm(userId, form, cancellationToken))
        {
            return Result<IReadOnlyList<FormQuestionResponse>>.Failure("Form not found.");
        }

        return Result<IReadOnlyList<FormQuestionResponse>>.Success(ToQuestions(form));
    }

    public async Task<Result<FormQuestionResponse>> AddQuestionAsync(Guid formId, CreateFormQuestionRequest request, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result<FormQuestionResponse>.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanManageForm(userId, form, cancellationToken))
        {
            return Result<FormQuestionResponse>.Failure("You are not allowed to update questions for this form.");
        }

        var validation = ValidateQuestion(request.QuestionText, request.QuestionType, request.Options);
        if (!validation.IsSuccess)
        {
            return Result<FormQuestionResponse>.Failure(validation.Error!);
        }

        var question = new FormQuestion
        {
            FormId = form.Id,
            QuestionText = request.QuestionText.Trim(),
            QuestionType = request.QuestionType,
            IsRequired = request.IsRequired,
            SortOrder = request.SortOrder,
            OptionsJson = SerializeOptions(request.QuestionType, request.Options)
        };

        await forms.AddQuestionAsync(question, cancellationToken);
        await auditLogger.LogUserActionAsync(userId, "FormQuestionCreated", "InternalForm", form.Id, "Form question created.", cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<FormQuestionResponse>.Success(ToQuestion(question));
    }

    public async Task<Result<FormQuestionResponse>> UpdateQuestionAsync(Guid formId, Guid questionId, UpdateFormQuestionRequest request, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result<FormQuestionResponse>.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanManageForm(userId, form, cancellationToken))
        {
            return Result<FormQuestionResponse>.Failure("You are not allowed to update questions for this form.");
        }

        var question = form.Questions.FirstOrDefault(item => item.Id == questionId) ?? await forms.GetQuestionAsync(formId, questionId, cancellationToken);
        if (question is null)
        {
            return Result<FormQuestionResponse>.Failure("Question not found.");
        }

        var hasResponses = await forms.HasResponsesAsync(form.Id, cancellationToken);
        var nextType = request.QuestionType ?? question.QuestionType;
        if (hasResponses && nextType != question.QuestionType)
        {
            return Result<FormQuestionResponse>.Failure("Question type cannot be changed after responses exist.");
        }

        var nextText = request.QuestionText ?? question.QuestionText;
        var nextOptions = request.Options ?? (IsChoiceQuestion(nextType) ? ParseOptions(question.OptionsJson) : null);
        var validation = ValidateQuestion(nextText, nextType, nextOptions);
        if (!validation.IsSuccess)
        {
            return Result<FormQuestionResponse>.Failure(validation.Error!);
        }

        question.QuestionText = nextText.Trim();
        question.QuestionType = nextType;
        question.IsRequired = request.IsRequired ?? question.IsRequired;
        question.SortOrder = request.SortOrder ?? question.SortOrder;
        question.OptionsJson = SerializeOptions(nextType, nextOptions);

        await auditLogger.LogUserActionAsync(userId, "FormQuestionUpdated", "InternalForm", form.Id, "Form question updated.", cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<FormQuestionResponse>.Success(ToQuestion(question));
    }

    public async Task<Result> DeleteQuestionAsync(Guid formId, Guid questionId, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanManageForm(userId, form, cancellationToken))
        {
            return Result.Failure("You are not allowed to update questions for this form.");
        }

        if (await forms.HasResponsesAsync(form.Id, cancellationToken))
        {
            return Result.Failure("Questions cannot be deleted after responses exist.");
        }

        var question = form.Questions.FirstOrDefault(item => item.Id == questionId) ?? await forms.GetQuestionAsync(formId, questionId, cancellationToken);
        if (question is null)
        {
            return Result.Failure("Question not found.");
        }

        forms.RemoveQuestion(question);
        await auditLogger.LogUserActionAsync(userId, "FormQuestionDeleted", "InternalForm", form.Id, "Form question deleted.", cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<FormResponseDetailResponse>> SubmitResponseAsync(Guid formId, SubmitFormResponseRequest request, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result<FormResponseDetailResponse>.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanViewForm(userId, form, cancellationToken))
        {
            return Result<FormResponseDetailResponse>.Failure("Form not found.");
        }

        if (!CanAcceptNormalResponse(form))
        {
            return Result<FormResponseDetailResponse>.Failure("This form is not accepting responses.");
        }

        if (await forms.GetResponseForUserAsync(form.Id, userId, cancellationToken) is not null)
        {
            return Result<FormResponseDetailResponse>.Failure("You have already submitted a response for this form.");
        }

        var validation = ValidateSubmittedAnswers(form, request.Answers);
        if (!validation.IsSuccess)
        {
            return Result<FormResponseDetailResponse>.Failure(validation.Error!);
        }

        var response = new FormResponse
        {
            FormId = form.Id,
            RespondentUserId = userId,
            SubmittedAt = clock.UtcNow
        };

        foreach (var answer in validation.Value!)
        {
            response.Answers.Add(new FormAnswer
            {
                FormQuestionId = answer.Question.Id,
                AnswerText = answer.AnswerText,
                AnswerJson = answer.AnswerJson
            });
        }

        await forms.AddResponseAsync(response, cancellationToken);
        await auditLogger.LogUserActionAsync(userId, "FormResponseSubmitted", "InternalForm", form.Id, "Form response submitted.", cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<FormResponseDetailResponse>.Success(ToResponse(response, form, revealRespondent: !form.IsAnonymous));
    }

    public async Task<Result<PagedResponse<FormResponseDetailResponse>>> ListResponsesAsync(Guid formId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result<PagedResponse<FormResponseDetailResponse>>.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanManageForm(userId, form, cancellationToken))
        {
            return Result<PagedResponse<FormResponseDetailResponse>>.Failure("You are not allowed to view responses for this form.");
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var allResponses = await forms.ListResponsesAsync(form.Id, cancellationToken);
        var items = allResponses
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(response => ToResponse(response, form, revealRespondent: !form.IsAnonymous))
            .ToList();

        return Result<PagedResponse<FormResponseDetailResponse>>.Success(new PagedResponse<FormResponseDetailResponse>(items, page, pageSize, allResponses.Count));
    }

    public async Task<Result<FormResponseDetailResponse?>> GetMyResponseAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result<FormResponseDetailResponse?>.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanViewForm(userId, form, cancellationToken))
        {
            return Result<FormResponseDetailResponse?>.Failure("Form not found.");
        }

        var response = await forms.GetResponseForUserAsync(form.Id, userId, cancellationToken);
        return Result<FormResponseDetailResponse?>.Success(response is null ? null : ToResponse(response, form, revealRespondent: !form.IsAnonymous));
    }

    public async Task<Result<FormSummaryResponse>> GetSummaryAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result<FormSummaryResponse>.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanManageForm(userId, form, cancellationToken))
        {
            return Result<FormSummaryResponse>.Failure("You are not allowed to view the summary for this form.");
        }

        var responses = await forms.ListResponsesAsync(form.Id, cancellationToken);
        var summaries = form.Questions
            .OrderBy(question => question.SortOrder)
            .ThenBy(question => question.CreatedAt)
            .Select(question => BuildQuestionSummary(question, responses))
            .ToList();

        return Result<FormSummaryResponse>.Success(new FormSummaryResponse(form.Id, form.Title, responses.Count, summaries));
    }

    public async Task<Result<IReadOnlyList<UnansweredUserResponse>>> ListUnansweredUsersAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        var form = await forms.GetAsync(formId, cancellationToken);
        if (form is null)
        {
            return Result<IReadOnlyList<UnansweredUserResponse>>.Failure("Form not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanManageForm(userId, form, cancellationToken))
        {
            return Result<IReadOnlyList<UnansweredUserResponse>>.Failure("You are not allowed to view unanswered users for this form.");
        }

        var members = await forms.ListScopeMembersAsync(form, cancellationToken);
        var respondentIds = (await forms.ListResponsesAsync(form.Id, cancellationToken))
            .Select(response => response.RespondentUserId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        var unanswered = members
            .Where(member => !respondentIds.Contains(member.UserId))
            .OrderBy(member => member.DisplayName)
            .Select(member => new UnansweredUserResponse(member.UserId, member.DisplayName, member.ScopeRole))
            .ToList();

        return Result<IReadOnlyList<UnansweredUserResponse>>.Success(unanswered);
    }

    private async Task<Result> ValidateFormRequestAsync(
        Guid? workspaceId,
        Guid? groupId,
        Guid? projectId,
        string title,
        DateTimeOffset? opensAt,
        DateTimeOffset? closesAt,
        CancellationToken cancellationToken)
    {
        if (!ScopedResourceValidation.HasExactlyOneScope(workspaceId, groupId, projectId))
        {
            return Result.Failure("Exactly one of WorkspaceId, GroupId, or ProjectId must be set.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return Result.Failure("Form title is required.");
        }

        if (opensAt.HasValue && closesAt.HasValue && opensAt.Value >= closesAt.Value)
        {
            return Result.Failure("Form open time must be before the close time.");
        }

        return await ScopedResourceValidation.ValidateExistingScopeAsync(
            workspaces,
            groups,
            projects,
            workspaceId,
            groupId,
            projectId,
            cancellationToken);
    }

    private static Result ValidateQuestion(string questionText, FormQuestionType questionType, IReadOnlyList<string>? options)
    {
        if (string.IsNullOrWhiteSpace(questionText))
        {
            return Result.Failure("Question text is required.");
        }

        if (!Enum.IsDefined(questionType))
        {
            return Result.Failure("Question type is invalid.");
        }

        if (IsChoiceQuestion(questionType))
        {
            if (options is null || options.Count == 0 || options.Any(string.IsNullOrWhiteSpace))
            {
                return Result.Failure("Choice questions require at least one non-empty option.");
            }

            if (options.Select(option => option.Trim()).Distinct(StringComparer.Ordinal).Count() != options.Count)
            {
                return Result.Failure("Choice question options must be unique.");
            }
        }
        else if (options is { Count: > 0 })
        {
            return Result.Failure("Options are only allowed for choice questions.");
        }

        return Result.Success();
    }

    private Result<IReadOnlyList<ValidatedAnswer>> ValidateSubmittedAnswers(InternalForm form, IReadOnlyList<SubmitFormAnswerRequest> submittedAnswers)
    {
        var questions = form.Questions.OrderBy(question => question.SortOrder).ThenBy(question => question.CreatedAt).ToList();
        if (questions.Count == 0)
        {
            return Result<IReadOnlyList<ValidatedAnswer>>.Failure("This form has no questions.");
        }

        var answersByQuestionId = new Dictionary<Guid, SubmitFormAnswerRequest>();
        foreach (var answer in submittedAnswers)
        {
            if (questions.All(question => question.Id != answer.FormQuestionId))
            {
                return Result<IReadOnlyList<ValidatedAnswer>>.Failure("An answer references an unknown question.");
            }

            if (!answersByQuestionId.TryAdd(answer.FormQuestionId, answer))
            {
                return Result<IReadOnlyList<ValidatedAnswer>>.Failure("A question has duplicate answers.");
            }
        }

        var validated = new List<ValidatedAnswer>();
        foreach (var question in questions)
        {
            answersByQuestionId.TryGetValue(question.Id, out var answer);
            if (answer is null)
            {
                if (question.IsRequired)
                {
                    return Result<IReadOnlyList<ValidatedAnswer>>.Failure($"Question '{question.QuestionText}' is required.");
                }

                continue;
            }

            var answerResult = ValidateAnswer(question, answer);
            if (!answerResult.IsSuccess)
            {
                return Result<IReadOnlyList<ValidatedAnswer>>.Failure(answerResult.Error!);
            }

            if (answerResult.Value is not null)
            {
                validated.Add(answerResult.Value);
            }
        }

        return Result<IReadOnlyList<ValidatedAnswer>>.Success(validated);
    }

    private static Result<ValidatedAnswer?> ValidateAnswer(FormQuestion question, SubmitFormAnswerRequest answer)
    {
        var text = NormalizeOptionalText(answer.AnswerText);
        var json = NormalizeOptionalText(answer.AnswerJson);
        var hasValue = text is not null || json is not null;

        if (!hasValue)
        {
            return question.IsRequired
                ? Result<ValidatedAnswer?>.Failure($"Question '{question.QuestionText}' is required.")
                : Result<ValidatedAnswer?>.Success(null);
        }

        return question.QuestionType switch
        {
            FormQuestionType.ShortText or FormQuestionType.LongText => ValidateTextAnswer(question, text),
            FormQuestionType.SingleChoice => ValidateSingleChoiceAnswer(question, text, json),
            FormQuestionType.MultipleChoice => ValidateMultipleChoiceAnswer(question, json),
            FormQuestionType.Boolean => ValidateBooleanAnswer(question, text, json),
            FormQuestionType.Date => ValidateDateAnswer(question, text, json),
            FormQuestionType.Number => ValidateNumberAnswer(question, text, json),
            _ => Result<ValidatedAnswer?>.Failure("Question type is invalid.")
        };
    }

    private static Result<ValidatedAnswer?> ValidateTextAnswer(FormQuestion question, string? text)
    {
        if (question.IsRequired && string.IsNullOrWhiteSpace(text))
        {
            return Result<ValidatedAnswer?>.Failure($"Question '{question.QuestionText}' is required.");
        }

        return text is null
            ? Result<ValidatedAnswer?>.Success(null)
            : Result<ValidatedAnswer?>.Success(new ValidatedAnswer(question, text, null));
    }

    private static Result<ValidatedAnswer?> ValidateSingleChoiceAnswer(FormQuestion question, string? text, string? json)
    {
        var selected = text ?? TryReadJsonString(json);
        var options = ParseOptions(question.OptionsJson);
        if (selected is null)
        {
            return question.IsRequired
                ? Result<ValidatedAnswer?>.Failure($"Question '{question.QuestionText}' is required.")
                : Result<ValidatedAnswer?>.Success(null);
        }

        if (!options.Contains(selected, StringComparer.Ordinal))
        {
            return Result<ValidatedAnswer?>.Failure($"Answer for '{question.QuestionText}' must match one of the configured options.");
        }

        return Result<ValidatedAnswer?>.Success(new ValidatedAnswer(question, selected, null));
    }

    private static Result<ValidatedAnswer?> ValidateMultipleChoiceAnswer(FormQuestion question, string? json)
    {
        var selected = TryReadJsonStringArray(json);
        if (selected is null)
        {
            return Result<ValidatedAnswer?>.Failure($"Answer for '{question.QuestionText}' must be a JSON array of strings.");
        }

        if (question.IsRequired && selected.Count == 0)
        {
            return Result<ValidatedAnswer?>.Failure($"Question '{question.QuestionText}' requires at least one selected option.");
        }

        var options = ParseOptions(question.OptionsJson);
        if (selected.Any(value => !options.Contains(value, StringComparer.Ordinal)))
        {
            return Result<ValidatedAnswer?>.Failure($"Answer for '{question.QuestionText}' contains an option that is not configured.");
        }

        return selected.Count == 0
            ? Result<ValidatedAnswer?>.Success(null)
            : Result<ValidatedAnswer?>.Success(new ValidatedAnswer(question, null, JsonSerializer.Serialize(selected)));
    }

    private static Result<ValidatedAnswer?> ValidateBooleanAnswer(FormQuestion question, string? text, string? json)
    {
        var value = TryReadJsonBoolean(json);
        if (!value.HasValue && bool.TryParse(text, out var parsed))
        {
            value = parsed;
        }

        if (!value.HasValue)
        {
            return Result<ValidatedAnswer?>.Failure($"Answer for '{question.QuestionText}' must be true or false.");
        }

        return Result<ValidatedAnswer?>.Success(new ValidatedAnswer(question, value.Value ? "true" : "false", null));
    }

    private static Result<ValidatedAnswer?> ValidateDateAnswer(FormQuestion question, string? text, string? json)
    {
        var value = text ?? TryReadJsonString(json);
        if (value is null || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return Result<ValidatedAnswer?>.Failure($"Answer for '{question.QuestionText}' must be a valid date.");
        }

        return Result<ValidatedAnswer?>.Success(new ValidatedAnswer(question, parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null));
    }

    private static Result<ValidatedAnswer?> ValidateNumberAnswer(FormQuestion question, string? text, string? json)
    {
        var value = text ?? TryReadJsonNumber(json);
        if (value is null || !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return Result<ValidatedAnswer?>.Failure($"Answer for '{question.QuestionText}' must be a valid number.");
        }

        return Result<ValidatedAnswer?>.Success(new ValidatedAnswer(question, parsed.ToString(CultureInfo.InvariantCulture), null));
    }

    private bool CanAcceptNormalResponse(InternalForm form)
    {
        if (form.Status != FormStatus.Open || form.DeletedAt.HasValue)
        {
            return false;
        }

        var now = clock.UtcNow;
        if (form.OpensAt.HasValue && now < form.OpensAt.Value)
        {
            return false;
        }

        return !form.ClosesAt.HasValue || now <= form.ClosesAt.Value;
    }

    private async Task NotifyFormOpenedAsync(Guid actorUserId, InternalForm form, CancellationToken cancellationToken)
    {
        var recipientUserIds = await forms.ListScopeRecipientUserIdsAsync(form, cancellationToken);
        if (recipientUserIds.Count == 0)
        {
            return;
        }

        await notifications.CreateManyAsync(
            recipientUserIds,
            NotificationType.System,
            $"Form opened: {form.Title}",
            form.Description,
            "InternalForm",
            form.Id,
            actorUserId,
            cancellationToken);
    }

    private static bool IsChoiceQuestion(FormQuestionType questionType)
    {
        return questionType is FormQuestionType.SingleChoice or FormQuestionType.MultipleChoice;
    }

    private static string? SerializeOptions(FormQuestionType questionType, IReadOnlyList<string>? options)
    {
        return IsChoiceQuestion(questionType)
            ? JsonSerializer.Serialize(options!.Select(option => option.Trim()).ToArray())
            : null;
    }

    private static IReadOnlyList<string> ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(optionsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? TryReadJsonString(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.String
                ? NormalizeOptionalText(document.RootElement.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadJsonNumber(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Number
                ? document.RootElement.GetRawText()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool? TryReadJsonBoolean(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? TryReadJsonStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var values = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var value = NormalizeOptionalText(item.GetString());
                if (value is null)
                {
                    return null;
                }

                values.Add(value);
            }

            return values;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static FormListItemResponse ToListItem(InternalForm form)
    {
        return new FormListItemResponse(
            form.Id,
            form.Title,
            form.Description,
            form.FormType,
            form.Status,
            form.OpensAt,
            form.ClosesAt,
            form.IsAnonymous,
            ToScopeSummary(form),
            form.CreatedAt);
    }

    private static FormDetailResponse ToDetail(InternalForm form)
    {
        return new FormDetailResponse(
            form.Id,
            form.WorkspaceId,
            form.GroupId,
            form.ProjectId,
            form.CreatedByUserId,
            form.Title,
            form.Description,
            form.FormType,
            form.Status,
            form.OpensAt,
            form.ClosesAt,
            form.IsAnonymous,
            ToQuestions(form),
            ToScopeSummary(form),
            form.CreatedAt,
            form.UpdatedAt);
    }

    private static IReadOnlyList<FormQuestionResponse> ToQuestions(InternalForm form)
    {
        return form.Questions
            .OrderBy(question => question.SortOrder)
            .ThenBy(question => question.CreatedAt)
            .Select(ToQuestion)
            .ToList();
    }

    private static FormQuestionResponse ToQuestion(FormQuestion question)
    {
        return new FormQuestionResponse(
            question.Id,
            question.QuestionText,
            question.QuestionType,
            question.IsRequired,
            question.SortOrder,
            IsChoiceQuestion(question.QuestionType) ? ParseOptions(question.OptionsJson) : null);
    }

    private static FormResponseDetailResponse ToResponse(FormResponse response, InternalForm form, bool revealRespondent)
    {
        return new FormResponseDetailResponse(
            response.Id,
            response.FormId,
            revealRespondent ? response.RespondentUserId : null,
            revealRespondent ? response.RespondentUser?.DisplayName : null,
            revealRespondent ? response.RespondentUser?.Email : null,
            response.SubmittedAt,
            response.Answers
                .OrderBy(answer => answer.FormQuestion?.SortOrder ?? 0)
                .ThenBy(answer => answer.FormQuestion?.CreatedAt)
                .Select(answer => new FormAnswerResponse(
                    answer.FormQuestionId,
                    answer.FormQuestion?.QuestionText ?? form.Questions.FirstOrDefault(question => question.Id == answer.FormQuestionId)?.QuestionText ?? "Question",
                    answer.FormQuestion?.QuestionType ?? form.Questions.FirstOrDefault(question => question.Id == answer.FormQuestionId)?.QuestionType ?? FormQuestionType.ShortText,
                    answer.AnswerText,
                    answer.AnswerJson))
                .ToList());
    }

    private static FormQuestionSummaryResponse BuildQuestionSummary(FormQuestion question, IReadOnlyList<FormResponse> responses)
    {
        var answers = responses
            .SelectMany(response => response.Answers)
            .Where(answer => answer.FormQuestionId == question.Id)
            .ToList();

        if (question.QuestionType == FormQuestionType.SingleChoice)
        {
            var counts = ParseOptions(question.OptionsJson).ToDictionary(option => option, _ => 0, StringComparer.Ordinal);
            foreach (var answer in answers.Where(answer => answer.AnswerText is not null))
            {
                counts[answer.AnswerText!] = counts.GetValueOrDefault(answer.AnswerText!) + 1;
            }

            return new FormQuestionSummaryResponse(question.Id, question.QuestionText, question.QuestionType, answers.Count, counts, null);
        }

        if (question.QuestionType == FormQuestionType.MultipleChoice)
        {
            var counts = ParseOptions(question.OptionsJson).ToDictionary(option => option, _ => 0, StringComparer.Ordinal);
            foreach (var answer in answers)
            {
                foreach (var value in TryReadJsonStringArray(answer.AnswerJson) ?? [])
                {
                    counts[value] = counts.GetValueOrDefault(value) + 1;
                }
            }

            return new FormQuestionSummaryResponse(question.Id, question.QuestionText, question.QuestionType, answers.Count, counts, null);
        }

        if (question.QuestionType == FormQuestionType.Boolean)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["true"] = answers.Count(answer => string.Equals(answer.AnswerText, "true", StringComparison.OrdinalIgnoreCase)),
                ["false"] = answers.Count(answer => string.Equals(answer.AnswerText, "false", StringComparison.OrdinalIgnoreCase))
            };

            return new FormQuestionSummaryResponse(question.Id, question.QuestionText, question.QuestionType, answers.Count, counts, null);
        }

        var samples = answers
            .Select(answer => answer.AnswerText ?? answer.AnswerJson)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Take(10)
            .ToList();

        return new FormQuestionSummaryResponse(question.Id, question.QuestionText, question.QuestionType, answers.Count, null, samples);
    }

    private static FormScopeSummaryResponse ToScopeSummary(InternalForm form)
    {
        if (form.ProjectId.HasValue)
        {
            return new FormScopeSummaryResponse(
                "Project",
                form.Project?.WorkspaceId,
                form.Project?.GroupId,
                form.ProjectId,
                form.Project?.Name ?? "Project");
        }

        if (form.GroupId.HasValue)
        {
            return new FormScopeSummaryResponse(
                "Group",
                form.Group?.WorkspaceId,
                form.GroupId,
                null,
                form.Group?.Name ?? "Group");
        }

        return new FormScopeSummaryResponse(
            "Workspace",
            form.WorkspaceId,
            null,
            null,
            form.Workspace?.Name ?? "Workspace");
    }

    private sealed record ValidatedAnswer(FormQuestion Question, string? AnswerText, string? AnswerJson);
}
