using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Forms;
using Coglatas.Application.Groups;
using Coglatas.Application.Projects;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Forms;

public sealed class FormServiceTests
{
    [Fact]
    public async Task ClosedFormRejectsResponse()
    {
        var fixture = FormFixture.Create();
        var member = fixture.AddUser();
        fixture.AddWorkspaceMember(member.Id, WorkspaceRole.Member);
        fixture.Current.UserIdValue = member.Id;
        var form = fixture.AddForm(FormStatus.Closed);
        fixture.AddQuestion(form, FormQuestionType.ShortText, isRequired: true);

        var result = await fixture.Service.SubmitResponseAsync(form.Id, new SubmitFormResponseRequest([
            new SubmitFormAnswerRequest(form.Questions.Single().Id, AnswerText: "Yes")
        ]));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RequiredQuestionMustBeAnswered()
    {
        var fixture = FormFixture.Create();
        var member = fixture.AddUser();
        fixture.AddWorkspaceMember(member.Id, WorkspaceRole.Member);
        fixture.Current.UserIdValue = member.Id;
        var form = fixture.AddForm(FormStatus.Open);
        fixture.AddQuestion(form, FormQuestionType.ShortText, isRequired: true);

        var result = await fixture.Service.SubmitResponseAsync(form.Id, new SubmitFormResponseRequest([]));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task SingleChoiceAnswerMustMatchOptions()
    {
        var fixture = FormFixture.Create();
        var member = fixture.AddUser();
        fixture.AddWorkspaceMember(member.Id, WorkspaceRole.Member);
        fixture.Current.UserIdValue = member.Id;
        var form = fixture.AddForm(FormStatus.Open);
        var question = fixture.AddQuestion(form, FormQuestionType.SingleChoice, isRequired: true, ["A", "B"]);

        var result = await fixture.Service.SubmitResponseAsync(form.Id, new SubmitFormResponseRequest([
            new SubmitFormAnswerRequest(question.Id, AnswerText: "C")
        ]));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task MultipleChoiceAnswersMustAllMatchOptions()
    {
        var fixture = FormFixture.Create();
        var member = fixture.AddUser();
        fixture.AddWorkspaceMember(member.Id, WorkspaceRole.Member);
        fixture.Current.UserIdValue = member.Id;
        var form = fixture.AddForm(FormStatus.Open);
        var question = fixture.AddQuestion(form, FormQuestionType.MultipleChoice, isRequired: true, ["A", "B"]);

        var result = await fixture.Service.SubmitResponseAsync(form.Id, new SubmitFormResponseRequest([
            new SubmitFormAnswerRequest(question.Id, AnswerJson: """["A","C"]""")
        ]));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task DuplicateResponseIsRejected()
    {
        var fixture = FormFixture.Create();
        var member = fixture.AddUser();
        fixture.AddWorkspaceMember(member.Id, WorkspaceRole.Member);
        fixture.Current.UserIdValue = member.Id;
        var form = fixture.AddForm(FormStatus.Open);
        var question = fixture.AddQuestion(form, FormQuestionType.ShortText, isRequired: true);
        fixture.AddResponse(form, member.Id, [(question, "First", null)]);

        var result = await fixture.Service.SubmitResponseAsync(form.Id, new SubmitFormResponseRequest([
            new SubmitFormAnswerRequest(question.Id, AnswerText: "Second")
        ]));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task NonManagerCannotViewAllResponses()
    {
        var fixture = FormFixture.Create();
        var member = fixture.AddUser();
        fixture.AddWorkspaceMember(member.Id, WorkspaceRole.Member);
        fixture.Current.UserIdValue = member.Id;
        var form = fixture.AddForm(FormStatus.Open);

        var result = await fixture.Service.ListResponsesAsync(form.Id);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task AnonymousResponseListHidesRespondentIdentity()
    {
        var fixture = FormFixture.Create();
        var manager = fixture.AddUser();
        var respondent = fixture.AddUser();
        fixture.AddWorkspaceMember(manager.Id, WorkspaceRole.Admin);
        fixture.AddWorkspaceMember(respondent.Id, WorkspaceRole.Member);
        var form = fixture.AddForm(FormStatus.Open, isAnonymous: true);
        var question = fixture.AddQuestion(form, FormQuestionType.ShortText);
        fixture.AddResponse(form, respondent.Id, [(question, "Secret", null)]);
        fixture.Current.UserIdValue = manager.Id;

        var result = await fixture.Service.ListResponsesAsync(form.Id);

        Assert.True(result.IsSuccess);
        var response = Assert.Single(result.Value!.Items);
        Assert.Null(response.RespondentUserId);
        Assert.Null(response.RespondentDisplayName);
        Assert.Null(response.RespondentEmail);
    }

    [Fact]
    public async Task ManagerCanOpenAndCloseForm()
    {
        var fixture = FormFixture.Create();
        var manager = fixture.AddUser();
        fixture.AddWorkspaceMember(manager.Id, WorkspaceRole.Admin);
        fixture.Current.UserIdValue = manager.Id;
        var form = fixture.AddForm(FormStatus.Draft);
        fixture.AddQuestion(form, FormQuestionType.Boolean);

        var open = await fixture.Service.OpenAsync(form.Id);
        var close = await fixture.Service.CloseAsync(form.Id);

        Assert.True(open.IsSuccess);
        Assert.True(close.IsSuccess);
        Assert.Equal(FormStatus.Closed, form.Status);
    }

    [Fact]
    public async Task UnsafeQuestionTypeChangeAfterResponsesExistIsRejected()
    {
        var fixture = FormFixture.Create();
        var manager = fixture.AddUser();
        var respondent = fixture.AddUser();
        fixture.AddWorkspaceMember(manager.Id, WorkspaceRole.Admin);
        fixture.AddWorkspaceMember(respondent.Id, WorkspaceRole.Member);
        fixture.Current.UserIdValue = manager.Id;
        var form = fixture.AddForm(FormStatus.Open);
        var question = fixture.AddQuestion(form, FormQuestionType.ShortText);
        fixture.AddResponse(form, respondent.Id, [(question, "Answer", null)]);

        var result = await fixture.Service.UpdateQuestionAsync(form.Id, question.Id, new UpdateFormQuestionRequest(QuestionType: FormQuestionType.Number));

        Assert.False(result.IsSuccess);
    }

    private sealed class FormFixture
    {
        private FormFixture()
        {
            Forms = new FakeForms(Users);
            WorkspaceAuthorization = new WorkspaceAuthorizationService(Users, Workspaces);
            GroupAuthorization = new GroupAuthorizationService(Groups, Workspaces, WorkspaceAuthorization);
            ProjectAuthorization = new ProjectAuthorizationService(Projects, WorkspaceAuthorization, GroupAuthorization, Groups);
            FormAuthorization = new FormAuthorizationService(Users, WorkspaceAuthorization, GroupAuthorization, ProjectAuthorization);
            Service = new FormService(
                Forms,
                Workspaces,
                Groups,
                Projects,
                FormAuthorization,
                Current,
                Clock,
                Audit,
                Notifications,
                UnitOfWork);
        }

        private FakeUsers Users { get; } = new();
        private FakeWorkspaces Workspaces { get; } = new();
        private FakeGroups Groups { get; } = new();
        private FakeProjects Projects { get; } = new();
        private FakeForms Forms { get; }
        public FakeCurrentUser Current { get; } = new();
        private FakeClock Clock { get; } = new();
        private FakeAuditLogger Audit { get; } = new();
        private FakeNotifications Notifications { get; } = new();
        private FakeUnitOfWork UnitOfWork { get; } = new();
        private WorkspaceAuthorizationService WorkspaceAuthorization { get; }
        private GroupAuthorizationService GroupAuthorization { get; }
        private ProjectAuthorizationService ProjectAuthorization { get; }
        private FormAuthorizationService FormAuthorization { get; }
        public FormService Service { get; }
        private Workspace Workspace { get; } = new() { Name = "Workspace", Slug = "workspace", CreatedByUserId = Guid.NewGuid(), Status = WorkspaceStatus.Active };

        public static FormFixture Create()
        {
            var fixture = new FormFixture();
            fixture.Workspaces.Items[fixture.Workspace.Id] = fixture.Workspace;
            return fixture;
        }

        public User AddUser(SystemRole role = SystemRole.User)
        {
            var number = Users.Items.Count + 1;
            var user = new User
            {
                DisplayName = $"User {number}",
                Email = $"user{number}@example.com",
                NormalizedEmail = $"USER{number}@EXAMPLE.COM",
                PasswordHash = "hash",
                SystemRole = role,
                Status = UserStatus.Active
            };
            Users.Items[user.Id] = user;
            return user;
        }

        public void AddWorkspaceMember(Guid userId, WorkspaceRole role)
        {
            Workspaces.Members.Add(new WorkspaceMember
            {
                WorkspaceId = Workspace.Id,
                UserId = userId,
                User = Users.Items[userId],
                Role = role,
                Status = MembershipStatus.Active,
                JoinedAt = Clock.UtcNow
            });
        }

        public InternalForm AddForm(FormStatus status, bool isAnonymous = false)
        {
            var form = new InternalForm
            {
                WorkspaceId = Workspace.Id,
                Workspace = Workspace,
                CreatedByUserId = Guid.NewGuid(),
                Title = $"Form {Forms.Items.Count + 1}",
                Status = status,
                FormType = FormType.Survey,
                IsAnonymous = isAnonymous,
                CreatedAt = Clock.UtcNow
            };
            Forms.Items.Add(form);
            return form;
        }

        public FormQuestion AddQuestion(InternalForm form, FormQuestionType type, bool isRequired = false, IReadOnlyList<string>? options = null)
        {
            var question = new FormQuestion
            {
                FormId = form.Id,
                Form = form,
                QuestionText = $"Question {form.Questions.Count + 1}",
                QuestionType = type,
                IsRequired = isRequired,
                SortOrder = form.Questions.Count,
                OptionsJson = options is null ? null : System.Text.Json.JsonSerializer.Serialize(options),
                CreatedAt = Clock.UtcNow
            };
            form.Questions.Add(question);
            Forms.Questions.Add(question);
            return question;
        }

        public void AddResponse(InternalForm form, Guid userId, IReadOnlyList<(FormQuestion Question, string? Text, string? Json)> answers)
        {
            var response = new FormResponse
            {
                FormId = form.Id,
                Form = form,
                RespondentUserId = userId,
                RespondentUser = Users.Items[userId],
                SubmittedAt = Clock.UtcNow,
                CreatedAt = Clock.UtcNow
            };

            foreach (var answer in answers)
            {
                response.Answers.Add(new FormAnswer
                {
                    FormResponseId = response.Id,
                    FormResponse = response,
                    FormQuestionId = answer.Question.Id,
                    FormQuestion = answer.Question,
                    AnswerText = answer.Text,
                    AnswerJson = answer.Json,
                    CreatedAt = Clock.UtcNow
                });
            }

            form.Responses.Add(response);
            Forms.Responses.Add(response);
        }
    }

    private sealed class FakeForms(FakeUsers users) : IFormRepository
    {
        public List<InternalForm> Items { get; } = [];
        public List<FormQuestion> Questions { get; } = [];
        public List<FormResponse> Responses { get; } = [];

        public Task<IReadOnlyList<InternalForm>> ListAsync(FormListQuery query, CancellationToken cancellationToken = default)
        {
            IEnumerable<InternalForm> source = Items;
            if (query.WorkspaceId.HasValue)
            {
                source = source.Where(form => form.WorkspaceId == query.WorkspaceId.Value);
            }

            if (query.GroupId.HasValue)
            {
                source = source.Where(form => form.GroupId == query.GroupId.Value);
            }

            if (query.ProjectId.HasValue)
            {
                source = source.Where(form => form.ProjectId == query.ProjectId.Value);
            }

            source = query.Status.HasValue
                ? source.Where(form => form.Status == query.Status.Value)
                : source.Where(form => form.Status != FormStatus.Archived && !form.DeletedAt.HasValue);

            if (query.FormType.HasValue)
            {
                source = source.Where(form => form.FormType == query.FormType.Value);
            }

            return Task.FromResult<IReadOnlyList<InternalForm>>(source.ToList());
        }

        public Task<InternalForm?> GetAsync(Guid formId, CancellationToken cancellationToken = default) => Task.FromResult(Items.FirstOrDefault(form => form.Id == formId));
        public Task AddAsync(InternalForm form, CancellationToken cancellationToken = default) { Items.Add(form); return Task.CompletedTask; }
        public Task AddQuestionAsync(FormQuestion question, CancellationToken cancellationToken = default)
        {
            Questions.Add(question);
            Items.First(form => form.Id == question.FormId).Questions.Add(question);
            return Task.CompletedTask;
        }

        public Task<FormQuestion?> GetQuestionAsync(Guid formId, Guid questionId, CancellationToken cancellationToken = default) => Task.FromResult(Questions.FirstOrDefault(question => question.FormId == formId && question.Id == questionId));
        public void RemoveQuestion(FormQuestion question)
        {
            Questions.Remove(question);
            Items.First(form => form.Id == question.FormId).Questions.Remove(question);
        }

        public Task<bool> HasResponsesAsync(Guid formId, CancellationToken cancellationToken = default) => Task.FromResult(Responses.Any(response => response.FormId == formId));
        public Task<FormResponse?> GetResponseForUserAsync(Guid formId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Responses.FirstOrDefault(response => response.FormId == formId && response.RespondentUserId == userId));
        public Task<IReadOnlyList<FormResponse>> ListResponsesAsync(Guid formId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FormResponse>>(Responses.Where(response => response.FormId == formId).ToList());
        public Task AddResponseAsync(FormResponse response, CancellationToken cancellationToken = default)
        {
            response.RespondentUser = response.RespondentUserId.HasValue ? users.Items.GetValueOrDefault(response.RespondentUserId.Value) : null;
            foreach (var answer in response.Answers)
            {
                answer.FormResponse = response;
                answer.FormQuestion = Questions.FirstOrDefault(question => question.Id == answer.FormQuestionId);
            }

            Responses.Add(response);
            Items.First(form => form.Id == response.FormId).Responses.Add(response);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> ListScopeRecipientUserIdsAsync(InternalForm form, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<IReadOnlyList<FormScopeMember>> ListScopeMembersAsync(InternalForm form, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FormScopeMember>>([]);
    }

    private sealed class FakeUsers : IUserRepository
    {
        public Dictionary<Guid, User> Items { get; } = [];
        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) => Task.FromResult(Items.Values.FirstOrDefault(user => user.NormalizedEmail == normalizedEmail));
        public Task AddAsync(User user, CancellationToken cancellationToken = default) { Items[user.Id] = user; return Task.CompletedTask; }
    }

    private sealed class FakeWorkspaces : IWorkspaceRepository
    {
        public Dictionary<Guid, Workspace> Items { get; } = [];
        public List<WorkspaceMember> Members { get; } = [];
        public Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid userId, bool includeAll, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Workspace>>(Items.Values.ToList());
        public Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(workspaceId));
        public Task<WorkspaceMember?> GetMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Members.FirstOrDefault(member => member.WorkspaceId == workspaceId && member.UserId == userId));
        public Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkspaceMember>>(Members.Where(member => member.WorkspaceId == workspaceId).ToList());
        public Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default) { Items[workspace.Id] = workspace; return Task.CompletedTask; }
        public Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken = default) { Members.Add(member); return Task.CompletedTask; }
    }

    private sealed class FakeGroups : IGroupRepository
    {
        private Dictionary<Guid, Group> Items { get; } = [];
        private List<GroupMember> Members { get; } = [];
        public Task<IReadOnlyList<Group>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Group>>(Items.Values.Where(group => group.WorkspaceId == workspaceId).ToList());
        public Task<IReadOnlyList<Group>> ListManagedByUserAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Group>>(Items.Values.Where(group => group.WorkspaceId == workspaceId && Members.Any(member => member.GroupId == group.Id && member.UserId == userId && member.Role is GroupRole.Owner or GroupRole.Admin)).ToList());
        public Task<Group?> GetByIdAsync(Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(groupId));
        public Task<GroupMember?> GetMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Members.FirstOrDefault(member => member.GroupId == groupId && member.UserId == userId));
        public Task<IReadOnlyList<GroupMember>> ListMembersAsync(Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GroupMember>>(Members.Where(member => member.GroupId == groupId).ToList());
        public Task AddAsync(Group group, CancellationToken cancellationToken = default) { Items[group.Id] = group; return Task.CompletedTask; }
        public Task AddMemberAsync(GroupMember member, CancellationToken cancellationToken = default) { Members.Add(member); return Task.CompletedTask; }
    }

    private sealed class FakeProjects : IProjectRepository
    {
        private Dictionary<Guid, Project> ProjectItems { get; } = [];
        private List<ProjectMember> Members { get; } = [];
        public Task<IReadOnlyList<Project>> ListVisibleAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Project>>(ProjectItems.Values.ToList());
        public Task<Project?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(ProjectItems.GetValueOrDefault(projectId));
        public Task<ProjectMember?> GetMemberAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Members.FirstOrDefault(member => member.ProjectId == projectId && member.UserId == userId));
        public Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectMember>>(Members.Where(member => member.ProjectId == projectId).ToList());
        public Task<IReadOnlyList<Milestone>> ListMilestonesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Milestone>>([]);
        public Task<Milestone?> GetMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default) => Task.FromResult<Milestone?>(null);
        public Task<IReadOnlyList<TaskItem>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskItem>>([]);
        public Task<TaskItem?> GetTaskAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<TaskItem?>(null);
        public Task<IReadOnlyList<TaskAssignment>> ListAssignmentsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskAssignment>>([]);
        public Task<TaskAssignment?> GetAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default) => Task.FromResult<TaskAssignment?>(null);
        public Task<IReadOnlyList<TaskDependency>> ListDependenciesAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskDependency>>([]);
        public Task<IReadOnlyList<TaskDependency>> ListProjectDependenciesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskDependency>>([]);
        public Task<TaskDependency?> GetDependencyAsync(Guid dependencyId, CancellationToken cancellationToken = default) => Task.FromResult<TaskDependency?>(null);
        public Task<bool> DependencyExistsAsync(Guid predecessorTaskId, Guid successorTaskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Comment>> ListCommentsAsync(CommentTargetType targetType, Guid targetId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Comment>>([]);
        public Task<Comment?> GetCommentAsync(Guid commentId, CancellationToken cancellationToken = default) => Task.FromResult<Comment?>(null);
        public Task AddProjectAsync(Project project, CancellationToken cancellationToken = default) { ProjectItems[project.Id] = project; return Task.CompletedTask; }
        public Task AddMemberAsync(ProjectMember member, CancellationToken cancellationToken = default) { Members.Add(member); return Task.CompletedTask; }
        public Task AddMilestoneAsync(Milestone milestone, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddTaskAsync(TaskItem task, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddAssignmentAsync(TaskAssignment assignment, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddDependencyAsync(TaskDependency dependency, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddCommentAsync(Comment comment, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void RemoveMember(ProjectMember member) => Members.Remove(member);
        public void RemoveAssignment(TaskAssignment assignment) { }
        public void RemoveDependency(TaskDependency dependency) { }
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? UserIdValue { get; set; }
        public Guid? UserId => UserIdValue;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => UserIdValue.HasValue;
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 6, 7, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeNotifications : INotificationService
    {
        public Task NotifyAsync(Guid recipientUserId, string title, string? body, string sourceType, Guid sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
    }
}
