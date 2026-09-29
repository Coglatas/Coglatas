using Microsoft.Extensions.DependencyInjection;
using Coglatas.Application.Admin;
using Coglatas.Application.Auth;
using Coglatas.Application.Announcements;
using Coglatas.Application.Artifacts;
using Coglatas.Application.Audit;
using Coglatas.Application.Channels;
using Coglatas.Application.Communication;
using Coglatas.Application.Events;
using Coglatas.Application.Files;
using Coglatas.Application.Forms;
using Coglatas.Application.Groups;
using Coglatas.Application.Integrations;
using Coglatas.Application.Messaging;
using Coglatas.Application.Notifications;
using Coglatas.Application.Planning;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Application.Security.Redaction;
using Coglatas.Application.StudentRecords;
using Coglatas.Application.Tenancy;
using Coglatas.Application.TenantAdministration;
using Coglatas.Application.TenantExports;
using Coglatas.Application.UiShell;
using Coglatas.Application.Workspaces;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;

namespace Coglatas.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<CurrentTenantService>();
        services.AddScoped<ICurrentTenant>(provider => provider.GetRequiredService<CurrentTenantService>());
        services.AddScoped<ICurrentTenantAccessor>(provider => provider.GetRequiredService<CurrentTenantService>());
        services.AddSingleton<CanonicalRedactionService>();
        services.AddSingleton<IRedactionService, CanonicalFileMetadataRedactionService>();
        services.AddScoped<ITenantAuthorizationService, TenantAuthorizationService>();
        services.AddScoped<ICapabilityGrantRepository, UnavailableCapabilityGrantRepository>();
        services.AddScoped<IMessageFollowUpRepository, UnavailableMessageFollowUpRepository>();
        services.AddScoped<IDefaultConversationStore, UnavailableDefaultConversationStore>();
        services.AddScoped<ICreateIdempotencyCoordinator, UnavailableCreateIdempotencyCoordinator>();
        services.AddScoped<IProjectActivationWorkflowStore, UnavailableProjectActivationWorkflowStore>();
        services.AddScoped<IProjectActivationUnitOfWork, UnavailableProjectActivationUnitOfWork>();
        services.AddScoped<IArtifactEvidenceRepository, UnavailableArtifactEvidenceRepository>();
        services.AddScoped<IResearchPlanRepository, UnavailableResearchPlanRepository>();
        services.AddScoped<IAnnouncementDraftRepository, UnavailableAnnouncementDraftRepository>();
        services.AddScoped<ITaskExecutionResultRepository, UnavailableTaskExecutionResultRepository>();
        services.AddScoped<ITaskExecutionInterventionRepository, UnavailableTaskExecutionInterventionRepository>();
        services.AddScoped<ICapabilityGrantEvaluator, CapabilityGrantEvaluator>();
        services.AddScoped<ICapabilityGrantService, CapabilityGrantService>();
        services.AddScoped<IAuditAuthorizationService, AuditAuthorizationService>();
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ITenantExportService, TenantExportService>();
        services.AddScoped<IFeatureFlagService, FeatureFlagService>();
        services.AddScoped<IQuotaService, QuotaService>();
        services.AddScoped<ITenantAdministrationService, TenantAdministrationService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserSessionService, UserSessionService>();
        services.AddScoped<IAuthorizationStateChangePublisher, AuthorizationStateChangePublisher>();
        services.AddScoped<IBusinessInvalidationPublisher, BusinessInvalidationPublisher>();
        services.AddScoped<IAdminService, AdminService>();
        services.AddScoped<IWorkspaceAuthorizationService, WorkspaceAuthorizationService>();
        services.AddScoped<IGroupAuthorizationService, GroupAuthorizationService>();
        services.AddScoped<IChannelAuthorizationService, ChannelAuthorizationService>();
        services.AddScoped<IConversationAuthorizationService, ConversationAuthorizationService>();
        services.AddScoped<IMessageIdempotencyCommitCoordinator, UnitOfWorkMessageIdempotencyCommitCoordinator>();
        services.AddScoped<IMessageFollowUpCommitCoordinator, UnitOfWorkMessageFollowUpCommitCoordinator>();
        services.AddScoped<ProjectAuthorizationService>();
        services.AddScoped<IProjectAuthorizationService>(provider => provider.GetRequiredService<ProjectAuthorizationService>());
        services.AddScoped<ITaskAuthorizationService>(provider => provider.GetRequiredService<ProjectAuthorizationService>());
        services.AddScoped<ICommentAuthorizationService>(provider => provider.GetRequiredService<ProjectAuthorizationService>());
        services.AddScoped<IEventAuthorizationService, EventAuthorizationService>();
        services.AddScoped<IFormAuthorizationService, FormAuthorizationService>();
        services.AddScoped<IFileAuthorizationService, FileAuthorizationService>();
        services.AddScoped<IArtifactAuthorizationService, ArtifactAuthorizationService>();
        services.AddScoped<IStudentRecordSchoolAccessContextProvider, WorkspaceSchoolAccessContextProvider>();
        services.AddScoped<IStudentRecordAuthorizationService, StudentRecordAuthorizationService>();
        services.AddScoped<INotificationApplicationService, NotificationApplicationService>();
        services.AddScoped<ITaskNotificationPreferenceService, TaskNotificationPreferenceService>();
        services.AddScoped<ITaskNotificationRecipientPolicy, TaskNotificationRecipientPolicy>();
        services.AddScoped<ITaskRelationshipTargetPolicy, TaskRelationshipTargetPolicy>();
        services.AddScoped<ITaskNotificationProducer, TaskNotificationProducer>();
        services.AddScoped<ICommunicationPollingService, CommunicationPollingService>();
        services.AddSingleton(new CommunicationSafetyOptions());
        services.AddSingleton<ICommunicationSafetyGuard, InMemoryCommunicationSafetyGuard>();
        services.AddScoped<IAnnouncementAttachmentService, AnnouncementAttachmentService>();
        services.AddScoped<IAnnouncementService, AnnouncementService>();
        services.AddScoped<IAnnouncementAnalyticsService, AnnouncementAnalyticsService>();
        services.AddScoped<IAnnouncementAudienceService, AnnouncementAudienceService>();
        services.AddScoped<AnnouncementDraftService>();
        services.AddScoped<IAnnouncementDraftService>(provider => provider.GetRequiredService<AnnouncementDraftService>());
        services.AddScoped<IAnnouncementPublicationProcessor>(provider => provider.GetRequiredService<AnnouncementDraftService>());
        services.AddScoped<WorkspaceGeneralRequiredInitialization>();
        services.AddScoped<IWorkspaceRequiredInitialization>(provider =>
            provider.GetRequiredService<IDefaultConversationStore>() is UnavailableDefaultConversationStore
                ? new UnavailableWorkspaceRequiredInitialization()
                : provider.GetRequiredService<WorkspaceGeneralRequiredInitialization>());
        services.AddScoped<IWorkspaceGeneralMembershipSynchronizer, WorkspaceGeneralMembershipSynchronizer>();
        services.AddScoped<IWorkspaceMemberProjectionService, WorkspaceMemberProjectionService>();
        services.AddScoped<IWorkspaceService, WorkspaceService>();
        services.AddScoped<IGroupService, GroupService>();
        services.AddScoped<IChannelService, ChannelService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IMessageFollowUpService, MessageFollowUpService>();
        services.AddScoped<IntegrationService>();
        services.AddScoped<IIntegrationService>(provider => provider.GetRequiredService<IntegrationService>());
        services.AddScoped<IApiTokenValidator>(provider => provider.GetRequiredService<IntegrationService>());
        services.AddScoped<ICanonicalProjectCreateService, CanonicalProjectCreateService>();
        services.AddScoped<ICanonicalTaskCreateService, CanonicalTaskCreateService>();
        services.AddScoped<IProjectGeneralActivationProvisioner, ProjectGeneralActivationProvisioner>();
        services.AddScoped<IProjectGeneralMembershipSynchronizer, ProjectGeneralMembershipSynchronizer>();
        services.AddScoped<IProjectMembershipService, ProjectMembershipService>();
        services.AddScoped<IConfiguredProjectTaskWorkflowSource, NoConfiguredProjectTaskWorkflowSource>();
        services.AddScoped<IProjectTaskWorkflowResolver, ProjectTaskWorkflowResolver>();
        services.AddScoped<IProjectTaskWorkflowActivationProvisioner, ProjectTaskWorkflowActivationProvisioner>();
        services.AddScoped<IProjectActivationService, ProjectActivationService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<IProjectService, CanonicalProjectService>();
        services.AddScoped<ITaskCommandUnitOfWork>(provider =>
            provider.GetRequiredService<IUnitOfWork>() as ITaskCommandUnitOfWork
            ?? throw new InvalidOperationException("IUnitOfWork must implement ITaskCommandUnitOfWork for Task commands."));
        services.AddScoped<ITaskCommandService, TaskCommandService>();
        services.AddScoped<ITaskSubresourceService, TaskSubresourceService>();
        services.AddScoped<ITaskExecutionScopeService, TaskExecutionScopeService>();
        services.AddScoped<ITaskExecutionInterventionService, TaskExecutionInterventionService>();
        services.AddScoped<IResearchPlanService, ResearchPlanService>();
        services.AddScoped<ITaskExecutionResultService, TaskExecutionResultService>();
        services.AddScoped<ITaskWorkspaceTimeZoneResolver, TaskWorkspaceTimeZoneResolver>();
        services.AddScoped<IAnnouncementScheduleTimeZoneResolver, AnnouncementScheduleTimeZoneResolver>();
        services.AddScoped<IEventService, EventService>();
        services.AddScoped<IFormService, FormService>();
        services.AddScoped<IFileSharingService, FileSharingService>();
        services.AddScoped<IFileActivityService, FileActivityService>();
        services.AddScoped<IFileService, FileService>();
        services.AddScoped<IFileObjectService>(provider => provider.GetRequiredService<IFileService>() as IFileObjectService
            ?? throw new InvalidOperationException("IFileService must be implemented by IFileObjectService."));
        services.AddScoped<IArtifactService, ArtifactService>();
        services.AddScoped<IArtifactEvidenceManifestService, ArtifactEvidenceManifestService>();
        services.AddScoped<IArtifactReportService, UnavailableArtifactReportService>();
        services.AddScoped<IPlanningService, PlanningService>();
        services.AddScoped<IUiShellService, UiShellService>();
        services.AddScoped<IStudentRecordService, StudentRecordService>();
        return services;
    }
}
