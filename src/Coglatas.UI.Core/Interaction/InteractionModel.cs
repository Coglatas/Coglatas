namespace Coglatas.Ui.Core.Interaction;

public sealed record ContextScope(
    string TenantId,
    string WorkspaceId,
    string? ProjectId = null)
{
    public bool SameBoundary(ContextScope other) =>
        string.Equals(TenantId, other.TenantId, StringComparison.Ordinal) &&
        string.Equals(WorkspaceId, other.WorkspaceId, StringComparison.Ordinal) &&
        string.Equals(ProjectId, other.ProjectId, StringComparison.Ordinal);
}

public readonly record struct RendererId(string Value);

public readonly record struct QueryDescriptor(string Value)
{
    public static QueryDescriptor Empty { get; } = new(string.Empty);
}

public readonly record struct EntityReference(string Kind, string Id);

public enum TemporalMode
{
    Now = 0,
    ExplicitVersion = 1,
    Historical = 2
}

public sealed record TemporalContext(TemporalMode Mode, string? Version = null)
{
    public static TemporalContext Current { get; } = new(TemporalMode.Now);

    public bool IsReadOnlySnapshot =>
        Mode is TemporalMode.ExplicitVersion or TemporalMode.Historical;
}

public enum ConflictKind
{
    None = 0,
    RemoteChanged = 1,
    Conflict = 2,
    Revoked = 3
}

public sealed record ConflictState(
    ConflictKind Kind,
    long? BaseRevision = null,
    long? RemoteRevision = null)
{
    public static ConflictState None { get; } = new(ConflictKind.None);
}

public enum AuthorityStatus
{
    Unknown = 0,
    Allowed = 1,
    Denied = 2,
    Revoked = 3
}

[Flags]
public enum CanonicalDependencyDomain
{
    None = 0,
    QueryMembership = 1 << 0,
    SelectedEntity = 1 << 1,
    InspectorTarget = 1 << 2,
    WorkflowMetadata = 1 << 3,
    Auxiliary = 1 << 4,
    All = QueryMembership | SelectedEntity | InspectorTarget | WorkflowMetadata | Auxiliary
}

public sealed record WorkSurfaceState(
    ContextScope Scope,
    QueryDescriptor Query,
    EntityReference? Selection,
    EntityReference? InspectorTarget,
    TemporalContext Temporal,
    ConflictState Conflict,
    RendererId ActiveRenderer,
    CanonicalDependencyDomain CanonicalDependencies,
    long ContextGeneration,
    long AuthorityGeneration,
    long CanonicalGeneration,
    long CanonicalRevision,
    AuthorityStatus LastKnownAuthority,
    bool HasLocalDraft)
{
    public static WorkSurfaceState Create(
        ContextScope scope,
        RendererId renderer,
        long authorityGeneration = 0,
        long canonicalGeneration = 0,
        long canonicalRevision = 0,
        CanonicalDependencyDomain canonicalDependencies = CanonicalDependencyDomain.All) =>
        new(
            scope,
            QueryDescriptor.Empty,
            null,
            null,
            TemporalContext.Current,
            ConflictState.None,
            renderer,
            canonicalDependencies,
            ContextGeneration: 0,
            AuthorityGeneration: authorityGeneration,
            CanonicalGeneration: canonicalGeneration,
            CanonicalRevision: canonicalRevision,
            LastKnownAuthority: AuthorityStatus.Unknown,
            HasLocalDraft: false);
}

public readonly record struct ProjectionStamp(
    long ContextGeneration,
    long AuthorityGeneration,
    long CanonicalGeneration);

[Flags]
public enum GenerationDomain
{
    None = 0,
    Context = 1 << 0,
    Authority = 1 << 1,
    Canonical = 1 << 2,
    All = Context | Authority | Canonical
}

public enum InteractionActionKind
{
    Read = 0,
    Mutate = 1,
    Restore = 2,
    Branch = 3,
    ResolveConflict = 4
}

public enum CanonicalConsistencyRequirement
{
    None = 0,
    CurrentProjection = 1
}

public readonly record struct ActionContractId(string Value);

public sealed record ActionSemantics(
    InteractionActionKind Kind,
    bool ContextBound,
    bool RequiresCurrentAuthority,
    CanonicalConsistencyRequirement CanonicalConsistency);

/// <summary>
/// Core-owned action definitions. Renderers send only an ActionContractId;
/// they do not choose the generation dependency mask.
/// </summary>
public static class CoreActionContracts
{
    public static ActionContractId ReadProjection { get; } = new("read.projection");
    public static ActionContractId ReadCanonical { get; } = new("read.canonical");
    public static ActionContractId MutateCanonical { get; } = new("mutate.canonical");
    public static ActionContractId RestoreSnapshot { get; } = new("restore.snapshot");
    public static ActionContractId BranchSnapshot { get; } = new("branch.snapshot");
    public static ActionContractId ResolveConflict { get; } = new("resolve.conflict");

    public static bool TryResolve(ActionContractId id, out ActionSemantics semantics)
    {
        semantics = id.Value switch
        {
            "read.projection" => new(
                InteractionActionKind.Read,
                ContextBound: true,
                RequiresCurrentAuthority: true,
                CanonicalConsistencyRequirement.None),

            "read.canonical" => new(
                InteractionActionKind.Read,
                ContextBound: true,
                RequiresCurrentAuthority: true,
                CanonicalConsistencyRequirement.CurrentProjection),

            "mutate.canonical" => new(
                InteractionActionKind.Mutate,
                ContextBound: true,
                RequiresCurrentAuthority: true,
                CanonicalConsistencyRequirement.CurrentProjection),

            "restore.snapshot" => new(
                InteractionActionKind.Restore,
                ContextBound: true,
                RequiresCurrentAuthority: true,
                CanonicalConsistencyRequirement.CurrentProjection),

            "branch.snapshot" => new(
                InteractionActionKind.Branch,
                ContextBound: true,
                RequiresCurrentAuthority: true,
                CanonicalConsistencyRequirement.CurrentProjection),

            "resolve.conflict" => new(
                InteractionActionKind.ResolveConflict,
                ContextBound: true,
                RequiresCurrentAuthority: true,
                CanonicalConsistencyRequirement.CurrentProjection),

            _ => null!
        };

        return semantics is not null;
    }
}

public static class GenerationDependencyResolver
{
    public static GenerationDomain Resolve(ActionSemantics semantics)
    {
        var dependencies = GenerationDomain.None;

        if (semantics.ContextBound)
        {
            dependencies |= GenerationDomain.Context;
        }

        if (semantics.RequiresCurrentAuthority)
        {
            dependencies |= GenerationDomain.Authority;
        }

        if (semantics.CanonicalConsistency == CanonicalConsistencyRequirement.CurrentProjection)
        {
            dependencies |= GenerationDomain.Canonical;
        }

        return dependencies;
    }
}

public enum RecoveryAction
{
    None = 0,
    Refetch = 1,
    ResolveConflict = 2,
    RestoreOrBranch = 3,
    Reauthorize = 4
}

public sealed record RequiredSafetyContext(
    TemporalMode TemporalMode,
    ConflictKind ConflictKind,
    AuthorityStatus AuthorityStatus,
    RecoveryAction RecoveryAction);

public sealed record ProjectionEnvelope(
    RendererId Renderer,
    ProjectionStamp Stamp,
    RequiredSafetyContext RequiredSafety,
    WorkSurfaceState SharedState);

public sealed record AuthoritySnapshot(
    ContextScope Scope,
    AuthorityStatus Status,
    long Generation);

public enum ActionDecisionCode
{
    None = 0,
    Allowed = 1,
    UnknownActionContract = 2,
    StaleProjection = 3,
    ScopeMismatch = 4,
    StaleAuthority = 5,
    AuthorityDenied = 6,
    TemporalMutationDenied = 7,
    ConflictResolutionRequired = 8
}

public sealed record ActionDecision(
    ActionDecisionCode Code,
    bool IsAllowed,
    string Reason)
{
    public static ActionDecision None { get; } =
        new(ActionDecisionCode.None, false, "No action was evaluated.");

    public static ActionDecision Allow() =>
        new(ActionDecisionCode.Allowed, true, "Action may proceed.");
}

/// <summary>
/// Preconditions emitted after an interaction action is allowed. A server-side
/// commit boundary can re-evaluate these conditions atomically with the write.
/// </summary>
public sealed record ExecutionPrecondition(
    ActionContractId ActionContract,
    ContextScope Scope,
    long ExpectedAuthorityGeneration,
    long ObservedCanonicalRevision,
    CanonicalDependencyDomain RequiredCanonicalDomains);

public sealed record ExecutionPermit(ExecutionPrecondition Precondition);

public sealed record CanonicalChangeRecord(
    long Revision,
    CanonicalDependencyDomain ChangedDomains);

public sealed record AuthoritativeCommitSnapshot(
    ContextScope Scope,
    AuthorityStatus AuthorityStatus,
    long AuthorityGeneration,
    long CurrentCanonicalRevision,
    IReadOnlyList<CanonicalChangeRecord> CanonicalChangesSinceObservation);

public enum CommitDecisionCode
{
    Allowed = 0,
    ScopeMismatch = 1,
    StaleAuthority = 2,
    AuthorityDenied = 3,
    StaleCanonical = 4,
    InvalidCanonicalHistory = 5,
    InvalidExecutionPrecondition = 6
}

public sealed record CommitDecision(
    CommitDecisionCode Code,
    bool IsAllowed,
    string Reason)
{
    public static CommitDecision Allow() =>
        new(CommitDecisionCode.Allowed, true, "Atomic commit preconditions are satisfied.");
}

/// <summary>
/// Reference precondition evaluator. The authoritative adapter must call this
/// while holding the same transaction/critical section that performs the write,
/// and CanonicalChangesSinceObservation must be complete for the observed range.
/// </summary>
public static class AtomicCommitPreconditionEvaluator
{
    public static CommitDecision Evaluate(
        ExecutionPrecondition precondition,
        AuthoritativeCommitSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(precondition);
        ArgumentNullException.ThrowIfNull(current);

        if (!CoreActionContracts.TryResolve(precondition.ActionContract, out var semantics) ||
            semantics.Kind == InteractionActionKind.Read)
        {
            return new(
                CommitDecisionCode.InvalidExecutionPrecondition,
                false,
                "Execution precondition must identify a known non-read action contract.");
        }

        if ((precondition.RequiredCanonicalDomains & ~CanonicalDependencyDomain.All) != CanonicalDependencyDomain.None ||
            (semantics.CanonicalConsistency == CanonicalConsistencyRequirement.CurrentProjection &&
             precondition.RequiredCanonicalDomains == CanonicalDependencyDomain.None))
        {
            return new(
                CommitDecisionCode.InvalidExecutionPrecondition,
                false,
                "Execution precondition contains an invalid or weakened canonical dependency mask.");
        }

        if (!precondition.Scope.SameBoundary(current.Scope))
        {
            return new(
                CommitDecisionCode.ScopeMismatch,
                false,
                "Commit scope no longer matches the permitted interaction scope.");
        }

        if (current.AuthorityGeneration != precondition.ExpectedAuthorityGeneration)
        {
            return new(
                CommitDecisionCode.StaleAuthority,
                false,
                "Authority generation changed after the interaction decision.");
        }

        if (current.AuthorityStatus != AuthorityStatus.Allowed)
        {
            return new(
                CommitDecisionCode.AuthorityDenied,
                false,
                "Current authoritative permission no longer allows the commit.");
        }

        if (current.CurrentCanonicalRevision < precondition.ObservedCanonicalRevision)
        {
            return new(
                CommitDecisionCode.InvalidCanonicalHistory,
                false,
                "Authoritative canonical revision moved behind the observed revision.");
        }

        var expectedRevision = checked(precondition.ObservedCanonicalRevision + 1);
        foreach (var change in current.CanonicalChangesSinceObservation)
        {
            if (change.Revision != expectedRevision ||
                change.Revision > current.CurrentCanonicalRevision)
            {
                return new(
                    CommitDecisionCode.InvalidCanonicalHistory,
                    false,
                    "Canonical change history is incomplete, duplicated, or out of order.");
            }

            if (precondition.RequiredCanonicalDomains != CanonicalDependencyDomain.None &&
                (change.ChangedDomains & precondition.RequiredCanonicalDomains) != CanonicalDependencyDomain.None)
            {
                return new(
                    CommitDecisionCode.StaleCanonical,
                    false,
                    "A canonical change relevant to the permitted operation occurred before commit.");
            }

            expectedRevision = checked(expectedRevision + 1);
        }

        if (expectedRevision - 1 != current.CurrentCanonicalRevision)
        {
            return new(
                CommitDecisionCode.InvalidCanonicalHistory,
                false,
                "Canonical change history does not cover the full observed-to-current revision range.");
        }

        return CommitDecision.Allow();
    }
}

public abstract record InteractionEvent;

public sealed record RendererChanged(RendererId Renderer) : InteractionEvent;

public sealed record ScopeChanged(ContextScope Scope) : InteractionEvent;

public sealed record TemporalChanged(TemporalContext Temporal) : InteractionEvent;

public sealed record LocalDraftChanged(bool HasLocalDraft) : InteractionEvent;

/// <summary>
/// Notification that canonical data reached a strictly ordered authoritative revision.
/// CanonicalRevision always advances for a newer accepted event; CanonicalGeneration
/// advances only when the changed domains intersect this projection's dependencies.
/// </summary>
public sealed record RemoteUpdated(
    long CanonicalRevision,
    CanonicalDependencyDomain ChangedDomains) : InteractionEvent;

/// <summary>
/// A refetch/reconciliation acknowledgement. It may clear RemoteChanged when it
/// covers the latest revision that actually affected the projection.
/// </summary>
public sealed record CanonicalSynchronized(long CanonicalRevision) : InteractionEvent;

public sealed record AuthorityChanged(
    AuthorityStatus Status,
    long Generation) : InteractionEvent;

/// <summary>
/// Conflict resolution acknowledged by the authoritative store at a newer canonical revision.
/// </summary>
public sealed record ConflictResolved(long CanonicalRevision) : InteractionEvent;

public sealed record ActionRequested(
    ActionContractId ActionContract,
    ProjectionStamp SourceProjection,
    AuthoritySnapshot CurrentAuthority) : InteractionEvent;

public sealed record TransitionResult(
    WorkSurfaceState State,
    ProjectionEnvelope Projection,
    ActionDecision ActionDecision,
    ExecutionPermit? ExecutionPermit,
    bool RequiresRefetch);
