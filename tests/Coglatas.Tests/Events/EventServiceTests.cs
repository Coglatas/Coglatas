using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Events;
using Coglatas.Application.Groups;
using Coglatas.Application.Projects;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Events;

public sealed class EventServiceTests
{
    [Fact]
    public async Task NonMemberCannotViewPrivateGroupEvent()
    {
        var fixture = EventFixture.Create();
        var creator = fixture.AddUser();
        fixture.AddWorkspaceMember(creator.Id, WorkspaceRole.Admin);
        fixture.Current.UserIdValue = creator.Id;
        var activityEvent = fixture.AddEvent(workspaceId: null, groupId: fixture.Group.Id, projectId: null, createdByUserId: creator.Id, EventStatus.Published);
        fixture.Current.UserIdValue = Guid.NewGuid();

        var result = await fixture.Service.GetAsync(activityEvent.Id);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task StartsAtAfterEndsAtIsRejected()
    {
        var fixture = EventFixture.Create();
        var manager = fixture.AddUser();
        fixture.AddWorkspaceMember(manager.Id, WorkspaceRole.Admin);
        fixture.Current.UserIdValue = manager.Id;

        var result = await fixture.Service.CreateAsync(new CreateEventRequest(
            fixture.Workspace.Id,
            null,
            null,
            "Rehearsal",
            null,
            null,
            fixture.Clock.UtcNow.AddHours(3),
            fixture.Clock.UtcNow.AddHours(1)));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task AttendanceAfterDeadlineIsRejectedForNormalUser()
    {
        var fixture = EventFixture.Create();
        var member = fixture.AddUser();
        fixture.AddWorkspaceMember(member.Id, WorkspaceRole.Member);
        var activityEvent = fixture.AddEvent(
            fixture.Workspace.Id,
            null,
            null,
            fixture.AddUser().Id,
            EventStatus.Published,
            attendanceDeadline: fixture.Clock.UtcNow.AddHours(-1));
        fixture.Current.UserIdValue = member.Id;

        var result = await fixture.Service.UpsertMyAttendanceAsync(activityEvent.Id, new UpdateMyAttendanceRequest(AttendanceStatus.Attending, "I'll be there"));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CapacityLimitIsEnforced()
    {
        var fixture = EventFixture.Create();
        var creator = fixture.AddUser();
        var attendee = fixture.AddUser();
        var secondAttendee = fixture.AddUser();
        fixture.AddWorkspaceMember(creator.Id, WorkspaceRole.Admin);
        fixture.AddWorkspaceMember(attendee.Id, WorkspaceRole.Member);
        fixture.AddWorkspaceMember(secondAttendee.Id, WorkspaceRole.Member);
        var activityEvent = fixture.AddEvent(fixture.Workspace.Id, null, null, creator.Id, EventStatus.Published, capacity: 1);
        fixture.AddAttendance(activityEvent.Id, attendee.Id, AttendanceStatus.Attending);
        fixture.Current.UserIdValue = secondAttendee.Id;

        var result = await fixture.Service.UpsertMyAttendanceAsync(activityEvent.Id, new UpdateMyAttendanceRequest(AttendanceStatus.Attending));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CalendarReturnsOnlyVisibleItems()
    {
        var fixture = EventFixture.Create();
        var member = fixture.AddUser();
        fixture.AddWorkspaceMember(member.Id, WorkspaceRole.Member);
        fixture.Current.UserIdValue = member.Id;
        fixture.AddEvent(fixture.Workspace.Id, null, null, fixture.AddUser().Id, EventStatus.Published);

        var hiddenWorkspace = new Workspace { Name = "Hidden", Slug = "hidden", CreatedByUserId = Guid.NewGuid(), Status = WorkspaceStatus.Active };
        fixture.Workspaces.Items[hiddenWorkspace.Id] = hiddenWorkspace;
        fixture.Events.ProjectCalendarItems.Add(new ProjectCalendarSourceItem(
            "ProjectDeadline",
            Guid.NewGuid(),
            fixture.Project.Id,
            fixture.Workspace.Id,
            null,
            fixture.Project.Name,
            fixture.Project.Name,
            fixture.Clock.UtcNow.AddDays(2),
            null,
            fixture.Project.Status.ToString(),
            $"/projects/{fixture.Project.Id}"));
        fixture.Events.Items.Add(new ActivityEvent
        {
            WorkspaceId = hiddenWorkspace.Id,
            CreatedByUserId = Guid.NewGuid(),
            Title = "Hidden event",
            StartsAt = fixture.Clock.UtcNow.AddDays(1),
            EndsAt = fixture.Clock.UtcNow.AddDays(1).AddHours(1),
            Status = EventStatus.Published
        });

        var result = await fixture.Service.GetCalendarAsync(new CalendarQuery(
            fixture.Clock.UtcNow.AddDays(-1),
            fixture.Clock.UtcNow.AddDays(5)));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
        Assert.DoesNotContain(result.Value, item => item.Title == "Hidden event");
    }

    [Fact]
    public async Task DraftEventIsNotVisibleToNormalMember()
    {
        var fixture = EventFixture.Create();
        var creator = fixture.AddUser();
        var member = fixture.AddUser();
        fixture.AddWorkspaceMember(creator.Id, WorkspaceRole.Admin);
        fixture.AddWorkspaceMember(member.Id, WorkspaceRole.Member);
        var activityEvent = fixture.AddEvent(fixture.Workspace.Id, null, null, creator.Id, EventStatus.Draft);
        fixture.Current.UserIdValue = member.Id;

        var result = await fixture.Service.GetAsync(activityEvent.Id);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CancelledEventRemainsVisible()
    {
        var fixture = EventFixture.Create();
        var creator = fixture.AddUser();
        var member = fixture.AddUser();
        fixture.AddWorkspaceMember(creator.Id, WorkspaceRole.Admin);
        fixture.AddWorkspaceMember(member.Id, WorkspaceRole.Member);
        var activityEvent = fixture.AddEvent(fixture.Workspace.Id, null, null, creator.Id, EventStatus.Cancelled);
        fixture.Current.UserIdValue = member.Id;

        var result = await fixture.Service.GetAsync(activityEvent.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(EventStatus.Cancelled, result.Value!.Status);
    }

    private sealed class EventFixture
    {
        private EventFixture()
        {
            WorkspaceAuthorization = new WorkspaceAuthorizationService(Users, Workspaces);
            GroupAuthorization = new GroupAuthorizationService(Groups, Workspaces, WorkspaceAuthorization);
            ProjectAuthorization = new ProjectAuthorizationService(Projects, WorkspaceAuthorization, GroupAuthorization, Groups);
            EventAuthorization = new EventAuthorizationService(Users, WorkspaceAuthorization, GroupAuthorization, ProjectAuthorization);
            Service = new EventService(
                Events,
                Users,
                Workspaces,
                Groups,
                Projects,
                EventAuthorization,
                ProjectAuthorization,
                Current,
                Clock,
                Audit,
                Notifications,
                UnitOfWork);
        }

        private FakeUsers Users { get; } = new();
        public FakeWorkspaces Workspaces { get; } = new();
        private FakeGroups Groups { get; } = new();
        private FakeProjects Projects { get; } = new();
        public FakeEvents Events { get; } = new();
        public FakeCurrentUser Current { get; } = new();
        public FakeClock Clock { get; } = new();
        private FakeAuditLogger Audit { get; } = new();
        private FakeNotifications Notifications { get; } = new();
        private FakeUnitOfWork UnitOfWork { get; } = new();
        private WorkspaceAuthorizationService WorkspaceAuthorization { get; }
        private GroupAuthorizationService GroupAuthorization { get; }
        private ProjectAuthorizationService ProjectAuthorization { get; }
        private EventAuthorizationService EventAuthorization { get; }
        public EventService Service { get; }
        public Workspace Workspace { get; } = new() { Name = "Workspace", Slug = "workspace", CreatedByUserId = Guid.NewGuid(), Status = WorkspaceStatus.Active };
        public Group Group { get; } = new() { Name = "Group", Slug = "group", WorkspaceId = Guid.Empty, CreatedByUserId = Guid.NewGuid(), Status = GroupStatus.Active };
        public Project Project { get; } = new() { Name = "Project", Slug = "project", WorkspaceId = Guid.Empty, OwnerUserId = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid(), Status = ProjectStatus.Active };

        public static EventFixture Create()
        {
            var fixture = new EventFixture();
            fixture.Group.WorkspaceId = fixture.Workspace.Id;
            fixture.Project.WorkspaceId = fixture.Workspace.Id;
            fixture.Workspaces.Items[fixture.Workspace.Id] = fixture.Workspace;
            fixture.Groups.Items[fixture.Group.Id] = fixture.Group;
            fixture.Projects.ProjectItems[fixture.Project.Id] = fixture.Project;
            return fixture;
        }

        public User AddUser()
        {
            var user = new User
            {
                DisplayName = $"User {Users.Items.Count + 1}",
                Email = $"user{Users.Items.Count + 1}@example.com",
                NormalizedEmail = $"USER{Users.Items.Count + 1}@EXAMPLE.COM",
                PasswordHash = "hash",
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

        public ActivityEvent AddEvent(Guid? workspaceId, Guid? groupId, Guid? projectId, Guid createdByUserId, EventStatus status, DateTimeOffset? attendanceDeadline = null, int? capacity = null)
        {
            var activityEvent = new ActivityEvent
            {
                WorkspaceId = workspaceId,
                GroupId = groupId,
                ProjectId = projectId,
                CreatedByUserId = createdByUserId,
                Title = $"Event {Events.Items.Count + 1}",
                StartsAt = Clock.UtcNow.AddDays(1),
                EndsAt = Clock.UtcNow.AddDays(1).AddHours(2),
                AttendanceDeadline = attendanceDeadline,
                Capacity = capacity,
                Status = status
            };
            Events.Items.Add(activityEvent);
            return activityEvent;
        }

        public void AddAttendance(Guid eventId, Guid userId, AttendanceStatus status)
        {
            Events.Attendances.Add(new EventAttendance
            {
                EventId = eventId,
                UserId = userId,
                User = Users.Items[userId],
                Status = status,
                RespondedAt = Clock.UtcNow
            });
        }
    }

    private sealed class FakeEvents : IEventRepository
    {
        public List<ActivityEvent> Items { get; } = [];
        public List<EventAttendance> Attendances { get; } = [];
        public List<ProjectCalendarSourceItem> ProjectCalendarItems { get; } = [];

        public Task<IReadOnlyList<ActivityEvent>> ListAsync(EventListQuery query, CancellationToken cancellationToken = default)
        {
            var source = ApplyScopeFilters(Items.AsEnumerable(), query.WorkspaceId, query.GroupId, query.ProjectId);
            if (query.FromDate.HasValue)
            {
                source = source.Where(activityEvent => activityEvent.EndsAt >= query.FromDate.Value);
            }

            if (query.ToDate.HasValue)
            {
                source = source.Where(activityEvent => activityEvent.StartsAt <= query.ToDate.Value);
            }

            if (query.Status.HasValue)
            {
                source = source.Where(activityEvent => activityEvent.Status == query.Status.Value);
            }

            return Task.FromResult<IReadOnlyList<ActivityEvent>>(source.OrderBy(activityEvent => activityEvent.StartsAt).ToList());
        }

        public Task<IReadOnlyList<ActivityEvent>> ListCalendarEventsAsync(CalendarQuery query, CancellationToken cancellationToken = default)
        {
            var source = ApplyScopeFilters(Items.AsEnumerable(), query.WorkspaceId, query.GroupId, query.ProjectId)
                .Where(activityEvent => activityEvent.Status != EventStatus.Archived && !activityEvent.DeletedAt.HasValue);

            if (query.FromDate.HasValue)
            {
                source = source.Where(activityEvent => activityEvent.EndsAt >= query.FromDate.Value);
            }

            if (query.ToDate.HasValue)
            {
                source = source.Where(activityEvent => activityEvent.StartsAt <= query.ToDate.Value);
            }

            return Task.FromResult<IReadOnlyList<ActivityEvent>>(source.OrderBy(activityEvent => activityEvent.StartsAt).ToList());
        }

        public Task<ActivityEvent?> GetAsync(Guid eventId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Items.FirstOrDefault(activityEvent => activityEvent.Id == eventId));
        }

        public Task AddAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            Items.Add(activityEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EventAttendance>> ListAttendanceAsync(Guid eventId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<EventAttendance>>(Attendances.Where(attendance => attendance.EventId == eventId).ToList());
        }

        public Task<EventAttendance?> GetAttendanceAsync(Guid eventId, Guid userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Attendances.FirstOrDefault(attendance => attendance.EventId == eventId && attendance.UserId == userId));
        }

        public Task AddAttendanceAsync(EventAttendance attendance, CancellationToken cancellationToken = default)
        {
            Attendances.Add(attendance);
            return Task.CompletedTask;
        }

        public Task<Dictionary<Guid, int>> GetAttendingCountsAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Attendances
                .Where(attendance => eventIds.Contains(attendance.EventId) && attendance.Status == AttendanceStatus.Attending)
                .GroupBy(attendance => attendance.EventId)
                .ToDictionary(group => group.Key, group => group.Count()));
        }

        public Task<IReadOnlyList<Guid>> ListScopeRecipientUserIdsAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }

        public Task<IReadOnlyList<ProjectCalendarSourceItem>> ListProjectCalendarItemsAsync(CalendarQuery query, CancellationToken cancellationToken = default)
        {
            IEnumerable<ProjectCalendarSourceItem> source = ProjectCalendarItems;
            if (query.WorkspaceId.HasValue)
            {
                source = source.Where(item => item.WorkspaceId == query.WorkspaceId.Value);
            }

            if (query.GroupId.HasValue)
            {
                source = source.Where(item => item.GroupId == query.GroupId.Value);
            }

            if (query.ProjectId.HasValue)
            {
                source = source.Where(item => item.ProjectId == query.ProjectId.Value);
            }

            if (query.FromDate.HasValue)
            {
                source = source.Where(item => item.StartsAt >= query.FromDate.Value);
            }

            if (query.ToDate.HasValue)
            {
                source = source.Where(item => item.StartsAt <= query.ToDate.Value);
            }

            return Task.FromResult<IReadOnlyList<ProjectCalendarSourceItem>>(source.ToList());
        }

        private static IEnumerable<ActivityEvent> ApplyScopeFilters(IEnumerable<ActivityEvent> source, Guid? workspaceId, Guid? groupId, Guid? projectId)
        {
            if (workspaceId.HasValue)
            {
                source = source.Where(activityEvent => activityEvent.WorkspaceId == workspaceId.Value);
            }

            if (groupId.HasValue)
            {
                source = source.Where(activityEvent => activityEvent.GroupId == groupId.Value);
            }

            if (projectId.HasValue)
            {
                source = source.Where(activityEvent => activityEvent.ProjectId == projectId.Value);
            }

            return source;
        }
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
        public Dictionary<Guid, Group> Items { get; } = [];
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
        public Dictionary<Guid, Project> ProjectItems { get; } = [];
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
