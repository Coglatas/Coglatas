using Coglatas.Application.Common.Interfaces;
using Coglatas.Infrastructure.Files;
using Coglatas.Infrastructure.Audit;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Infrastructure.Security;
using Coglatas.Infrastructure.TaskExecution;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Application.Notifications;
using Coglatas.Application.Workspaces;
using Coglatas.Application.Announcements;
using Coglatas.Application.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Coglatas.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

        services.AddScoped<ProjectGovernanceSaveChangesInterceptor>();
        services.AddScoped<TaskPhaseActivitySaveChangesInterceptor>();
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
            options
                .UseNpgsql(connectionString)
                .AddInterceptors(
                    serviceProvider.GetRequiredService<ProjectGovernanceSaveChangesInterceptor>(),
                    serviceProvider.GetRequiredService<TaskPhaseActivitySaveChangesInterceptor>()));

        services.AddScoped<CoglatasUiCanonicalChangeJournalCoordinator>();

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ICapabilityGrantRepository, CapabilityGrantRepository>();
        services.AddScoped<ITenantExportRepository, TenantExportRepository>();
        services.AddScoped<IIntegrationRepository, IntegrationRepository>();
        services.AddScoped<Coglatas.Application.Admin.IAdminRepository, AdminRepository>();
        services.AddScoped<IInviteRepository, InviteRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<IWorkspaceDashboardQuery, WorkspaceDashboardQuery>();
        services.AddScoped<ITaskNotificationPreferenceRepository, TaskNotificationPreferenceRepository>();
        services.AddScoped<ITaskDeadlineDigestRepository, TaskDeadlineDigestRepository>();
        services.AddScoped<IGroupRepository, GroupRepository>();
        services.AddScoped<IChannelRepository, ChannelRepository>();
        services.AddScoped<IMessagingRepository, MessagingRepository>();
        services.AddScoped<IMessageFollowUpRepository, MessageFollowUpRepository>();
        services.AddScoped<IMessageIdempotencyCommitCoordinator, EfMessageIdempotencyCommitCoordinator>();
        services.AddScoped<IMessageFollowUpCommitCoordinator, EfMessageFollowUpCommitCoordinator>();
        services.AddScoped<IDefaultConversationStore, DefaultConversationStore>();
        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<ITaskExecutionScopeRepository, TaskExecutionScopeRepository>();
        services.AddScoped<ITaskExecutionInterventionRepository, TaskExecutionInterventionRepository>();
        services.AddScoped<IResearchPlanRepository, ResearchPlanRepository>();
        services.AddScoped<ITaskExecutionResultRepository, TaskExecutionResultRepository>();
        services.AddScoped<DurableTaskExecutionResultRuntime>();
        services.AddScoped<ITaskExecutionRuntime>(provider => provider.GetRequiredService<DurableTaskExecutionResultRuntime>());
        services.AddScoped<IProjectVisibilityService, ProjectVisibilityService>();
        services.AddScoped<IProjectActivationWorkflowStore, ProjectActivationWorkflowStore>();
        services.AddScoped<IProjectActivationUnitOfWork, ProjectActivationUnitOfWork>();
        services.AddScoped<IConfiguredProjectTaskWorkflowSource, ConfiguredProjectTaskWorkflowSource>();
        services.AddScoped<IProjectKanbanRepository, ProjectKanbanRepository>();
        services.AddScoped<IProjectKanbanService, ProjectKanbanService>();
        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IStudentRecordRepository, StudentRecordRepository>();
        services.AddScoped<IStudentRecordExportGrantRepository, StudentRecordExportGrantRepository>();
        services.AddScoped<IFormRepository, FormRepository>();
        services.AddScoped<IFileRepository, FileRepository>();
        services.AddScoped<IFileAccessGrantRepository, FileAccessGrantRepository>();
        services.AddScoped<IFileDownloadGrantRepository, FileDownloadGrantRepository>();
        services.AddScoped<Coglatas.Application.Files.IFileSelectionSnapshotService, FileSelectionSnapshotService>();
        services.AddScoped<IFileFolderService, FileFolderService>();
        services.AddScoped<ITenantPlanRepository, TenantPlanRepository>();
        services.AddScoped<IArtifactRepository, ArtifactRepository>();
        services.AddScoped<IArtifactEvidenceRepository, ArtifactEvidenceRepository>();
        services.AddScoped<Coglatas.Application.Artifacts.IArtifactReportService, DbArtifactReportService>();
        services.AddScoped<Coglatas.Application.Artifacts.IArtifactReportRefinementService, GuardedArtifactReportRefinementService>();
        services.AddScoped<IPlanningRepository, PlanningRepository>();
        services.AddScoped<IUiShellRepository, UiShellRepository>();
        services.AddScoped<IAnnouncementRepository, AnnouncementRepository>();
        services.AddScoped<IAnnouncementDraftRepository, AnnouncementDraftRepository>();
        services.AddScoped<IAnnouncementDistributionStore, AnnouncementDistributionStore>();
        services.AddScoped<IAnnouncementEngagementStore, AnnouncementEngagementStore>();
        services.AddScoped<Coglatas.Application.Realtime.IOutboxEventRepository, OutboxEventRepository>();
        services.AddScoped<ITransactionalOutbox, TransactionalOutbox>();
        services.AddScoped<IOutboxReplayService, OutboxReplayService>();
        services.AddScoped<EfUnitOfWork>();
        services.AddScoped<IUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<EfUnitOfWork>());
        services.AddScoped<ITaskCommandUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<EfUnitOfWork>());
        services.AddScoped<ICreateIdempotencyCoordinator, EfCreateIdempotencyCoordinator>();
        services.Configure<FileStorageOptions>(options =>
        {
            var section = configuration.GetSection("FileStorage");
            options.Provider = section["Provider"] ?? "LocalFileSystem";
            options.RootPath = section["RootPath"] ?? string.Empty;
            if (long.TryParse(section["MaxFileSizeBytes"] as string, out var maxFileSizeBytes))
            {
                options.MaxFileSizeBytes = maxFileSizeBytes;
            }

            options.AllowedExtensions = section.GetSection("AllowedExtensions")
                .GetChildren()
                .Select(item => item.Value)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
            options.AllowedContentTypes = section.GetSection("AllowedContentTypes")
                .GetChildren()
                .Select(item => item.Value)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
            options.UseSignedUrls = bool.TryParse(section["UseSignedUrls"] as string, out var useSignedUrls) && useSignedUrls;
            options.BucketName = section["BucketName"];
            options.Region = section["Region"];
            options.Endpoint = section["Endpoint"];
            options.UsePathStyle = bool.TryParse(section["UsePathStyle"], out var usePathStyle) && usePathStyle;
            options.AccessKey = section["AccessKey"];
            options.SecretKey = section["SecretKey"];
        });
        services.AddScoped<IFileUploadPolicy, ConfiguredFileUploadPolicy>();
        services.AddScoped<IFileStorageService>(provider =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileStorageOptions>>().Value;
            return options.Provider switch
            {
                "LocalFileSystem" => provider.GetRequiredService<LocalFileStorageService>(),
                "ObjectStorage" or "S3Compatible" or "OCIObjectStorage" => provider.GetRequiredService<UnsupportedObjectStorageService>(),
                _ => throw new InvalidOperationException($"Unsupported FileStorage:Provider '{options.Provider}'.")
            };
        });
        services.AddScoped<LocalFileStorageService>();
        services.AddScoped<UnsupportedObjectStorageService>();
        services.AddScoped<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddScoped<ITokenHasher, Sha256TokenHasher>();
        services.AddScoped<IAuditLogger, DbAuditLogger>();
        services.AddScoped<INotificationService, DbNotificationService>();
        services.AddScoped<CurrentAuthorizationTargetResolver>();
        services.AddScoped<CanonicalCurrentAuthorizationTargetResolver>();
        services.AddScoped<NotificationNavigationTargetResolver>();
        services.AddScoped<CanonicalNotificationTargetResolver>();
        services.AddScoped<INotificationTargetResolver>(provider => provider.GetRequiredService<CanonicalNotificationTargetResolver>());
        services.AddScoped<IRealtimeEventTargetResolver>(provider => provider.GetRequiredService<CanonicalCurrentAuthorizationTargetResolver>());
        services.AddScoped<INotificationOpenService, NotificationOpenService>();
        services.AddScoped<Coglatas.Application.Search.ISearchService, DbSearchService>();
        services.AddScoped<Coglatas.Application.Audit.IAuditQueryService, DbAuditQueryService>();
        services.AddScoped<Coglatas.Application.Audit.IAuditClaimsEvidenceService, DbAuditClaimsEvidenceService>();
        services.AddScoped<Coglatas.Application.Audit.IAuditFindingsService, DbAuditFindingsService>();
        services.AddScoped<Coglatas.Application.Audit.IAuditFindingReviewerMentionsService, DbAuditFindingReviewerMentionsService>();
        services.AddScoped<Coglatas.Application.Audit.IAuditFindingDecisionService, DbAuditFindingDecisionService>();
        services.AddScoped<Coglatas.Application.Audit.IAuditPackageExportService, AuditPackageExportService>();
        services.AddScoped<Coglatas.Application.Audit.IAuditPackageExportProcessor, AuditPackageExportProcessor>();
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }
}