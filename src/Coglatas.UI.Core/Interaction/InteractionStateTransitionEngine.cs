namespace Coglatas.Ui.Core.Interaction;

/// <summary>
/// Renderer-independent transition engine for WorkSurface interaction state.
///
/// The engine is deliberately pure: it does not call HTTP, Avalonia, Dock,
/// MSAGL, persistence, or generated backend DTOs. Adapters obtain the current
/// server-authoritative authorization snapshot and pass it into ActionRequested.
/// </summary>
public sealed class InteractionStateTransitionEngine
{
    public TransitionResult Process(WorkSurfaceState state, InteractionEvent @event)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(@event);

        var next = state;
        var decision = ActionDecision.None;
        ExecutionPermit? executionPermit = null;
        var requiresRefetch = false;

        switch (@event)
        {
            case RendererChanged changed:
                // Renderer identity changes, semantic interaction state does not.
                next = state with { ActiveRenderer = changed.Renderer };
                break;

            case ScopeChanged changed:
                next = state with
                {
                    Scope = changed.Scope,
                    Query = QueryDescriptor.Empty,
                    Selection = null,
                    InspectorTarget = null,
                    Temporal = TemporalContext.Current,
                    Conflict = ConflictState.None,
                    ContextGeneration = checked(state.ContextGeneration + 1),
                    LastKnownAuthority = AuthorityStatus.Unknown,
                    HasLocalDraft = false
                };
                requiresRefetch = true;
                break;

            case TemporalChanged changed:
                if (changed.Temporal == state.Temporal)
                {
                    break;
                }

                next = state with
                {
                    Temporal = changed.Temporal,
                    ContextGeneration = checked(state.ContextGeneration + 1)
                };
                requiresRefetch = true;
                break;

            case LocalDraftChanged changed:
                next = state with { HasLocalDraft = changed.HasLocalDraft };
                break;

            case RemoteUpdated changed:
                if (changed.CanonicalRevision <= state.CanonicalRevision)
                {
                    // Duplicate/reordered canonical events cannot move the authoritative
                    // revision backwards or create synthetic local generations.
                    break;
                }

                var affectsProjection =
                    (state.CanonicalDependencies & changed.ChangedDomains) !=
                    CanonicalDependencyDomain.None;

                if (!affectsProjection)
                {
                    // The authoritative stream advanced, but the semantic inputs for this
                    // projection did not. Cr advances while Gd intentionally does not.
                    next = state with
                    {
                        CanonicalRevision = changed.CanonicalRevision
                    };
                    break;
                }

                next = state with
                {
                    CanonicalRevision = changed.CanonicalRevision,
                    CanonicalGeneration = checked(state.CanonicalGeneration + 1),
                    Conflict = state.Conflict.Kind == ConflictKind.Revoked
                        ? state.Conflict
                        : state.HasLocalDraft
                            ? new ConflictState(
                                ConflictKind.Conflict,
                                BaseRevision: state.CanonicalRevision,
                                RemoteRevision: changed.CanonicalRevision)
                            : new ConflictState(
                                ConflictKind.RemoteChanged,
                                BaseRevision: state.CanonicalRevision,
                                RemoteRevision: changed.CanonicalRevision)
                };
                requiresRefetch =
                    state.Conflict.Kind == ConflictKind.Revoked || !state.HasLocalDraft;
                break;

            case CanonicalSynchronized changed:
                if (state.Conflict.Kind != ConflictKind.RemoteChanged ||
                    state.Conflict.RemoteRevision is not { } affectedRevision)
                {
                    break;
                }

                if (changed.CanonicalRevision < affectedRevision ||
                    changed.CanonicalRevision > state.CanonicalRevision)
                {
                    // An acknowledgement must cover the latest revision that actually
                    // affected the projection, but it need not equal a later unrelated Cr.
                    break;
                }

                next = state with { Conflict = ConflictState.None };
                break;

            case AuthorityChanged changed:
                if (changed.Generation <= state.AuthorityGeneration)
                {
                    // Generation is immutable and monotonic: duplicate, contradictory
                    // same-generation, and reordered older authority events are no-ops.
                    break;
                }

                var revoked = changed.Status is AuthorityStatus.Denied or AuthorityStatus.Revoked;
                var restoredFromRevoked =
                    changed.Status == AuthorityStatus.Allowed &&
                    state.Conflict.Kind == ConflictKind.Revoked;

                next = state with
                {
                    AuthorityGeneration = changed.Generation,
                    LastKnownAuthority = changed.Status,
                    Selection = revoked ? null : state.Selection,
                    InspectorTarget = revoked ? null : state.InspectorTarget,
                    Conflict = revoked
                        ? new ConflictState(ConflictKind.Revoked)
                        : restoredFromRevoked
                            ? ConflictState.None
                            : state.Conflict
                };
                requiresRefetch = true;
                break;

            case ConflictResolved changed:
                if (changed.CanonicalRevision <= state.CanonicalRevision)
                {
                    // A stale or duplicate resolution acknowledgement cannot clear a
                    // newer conflict or advance the local canonical generation.
                    break;
                }

                next = state with
                {
                    CanonicalRevision = changed.CanonicalRevision,
                    CanonicalGeneration = checked(state.CanonicalGeneration + 1),
                    Conflict = state.Conflict.Kind == ConflictKind.Revoked
                        ? state.Conflict
                        : ConflictState.None,
                    HasLocalDraft = false
                };
                requiresRefetch = true;
                break;

            case ActionRequested requested:
                (decision, executionPermit) = EvaluateAction(state, requested);
                requiresRefetch = decision is
                {
                    IsAllowed: false,
                    Code: ActionDecisionCode.StaleProjection
                        or ActionDecisionCode.StaleAuthority
                        or ActionDecisionCode.AuthorityDenied
                        or ActionDecisionCode.ScopeMismatch
                };
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(@event));
        }

        return new TransitionResult(
            next,
            CreateProjection(next),
            decision,
            executionPermit,
            requiresRefetch);
    }

    public ProjectionEnvelope CreateProjection(WorkSurfaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new ProjectionEnvelope(
            state.ActiveRenderer,
            Stamp(state),
            CreateRequiredSafetyContext(state),
            state);
    }

    public static ProjectionStamp Stamp(WorkSurfaceState state) =>
        new(
            state.ContextGeneration,
            state.AuthorityGeneration,
            state.CanonicalGeneration);

    private static (ActionDecision Decision, ExecutionPermit? Permit) EvaluateAction(
        WorkSurfaceState state,
        ActionRequested request)
    {
        if (!CoreActionContracts.TryResolve(request.ActionContract, out var semantics))
        {
            return (
                new ActionDecision(
                    ActionDecisionCode.UnknownActionContract,
                    false,
                    $"Unknown Core action contract '{request.ActionContract.Value}'."),
                null);
        }

        var currentStamp = Stamp(state);
        var dependencies = GenerationDependencyResolver.Resolve(semantics);

        if (!MatchesRequiredGenerations(request.SourceProjection, currentStamp, dependencies))
        {
            return (
                new ActionDecision(
                    ActionDecisionCode.StaleProjection,
                    false,
                    $"The action depends on generation domains {dependencies} that no longer match."),
                null);
        }

        if (semantics.RequiresCurrentAuthority)
        {
            if (!state.Scope.SameBoundary(request.CurrentAuthority.Scope))
            {
                return (
                    new ActionDecision(
                        ActionDecisionCode.ScopeMismatch,
                        false,
                        "The current authority snapshot belongs to a different scope."),
                    null);
            }

            if (request.CurrentAuthority.Generation != state.AuthorityGeneration)
            {
                return (
                    new ActionDecision(
                        ActionDecisionCode.StaleAuthority,
                        false,
                        "The interaction state and the current authority generation do not match."),
                    null);
            }

            if (request.CurrentAuthority.Status != AuthorityStatus.Allowed)
            {
                return (
                    new ActionDecision(
                        ActionDecisionCode.AuthorityDenied,
                        false,
                        "Current server authority does not allow the requested action."),
                    null);
            }
        }

        if (semantics.Kind != InteractionActionKind.Read &&
            (state.LastKnownAuthority != AuthorityStatus.Allowed ||
             state.Conflict.Kind == ConflictKind.Revoked))
        {
            return (
                new ActionDecision(
                    ActionDecisionCode.AuthorityDenied,
                    false,
                    "Interaction state is not currently authorized for a protected action."),
                null);
        }

        if (state.Temporal.IsReadOnlySnapshot &&
            semantics.Kind == InteractionActionKind.Mutate)
        {
            return (
                new ActionDecision(
                    ActionDecisionCode.TemporalMutationDenied,
                    false,
                    "Pinned or historical snapshots are read-only for ordinary mutation; use an explicit restore or branch action."),
                null);
        }

        if (state.Conflict.Kind == ConflictKind.Conflict &&
            semantics.Kind == InteractionActionKind.Mutate)
        {
            return (
                new ActionDecision(
                    ActionDecisionCode.ConflictResolutionRequired,
                    false,
                    "An unresolved conflict requires an explicit resolution action."),
                null);
        }

        var permit = semantics.Kind == InteractionActionKind.Read
            ? null
            : new ExecutionPermit(
                new ExecutionPrecondition(
                    request.ActionContract,
                    state.Scope,
                    state.AuthorityGeneration,
                    state.CanonicalRevision,
                    semantics.CanonicalConsistency == CanonicalConsistencyRequirement.CurrentProjection
                        ? state.CanonicalDependencies
                        : CanonicalDependencyDomain.None));

        return (ActionDecision.Allow(), permit);
    }

    internal static bool MatchesRequiredGenerations(
        ProjectionStamp source,
        ProjectionStamp current,
        GenerationDomain dependencies)
    {
        if (dependencies.HasFlag(GenerationDomain.Context) &&
            source.ContextGeneration != current.ContextGeneration)
        {
            return false;
        }

        if (dependencies.HasFlag(GenerationDomain.Authority) &&
            source.AuthorityGeneration != current.AuthorityGeneration)
        {
            return false;
        }

        if (dependencies.HasFlag(GenerationDomain.Canonical) &&
            source.CanonicalGeneration != current.CanonicalGeneration)
        {
            return false;
        }

        return true;
    }

    private static RequiredSafetyContext CreateRequiredSafetyContext(
        WorkSurfaceState state)
    {
        var recovery = state switch
        {
            { LastKnownAuthority: AuthorityStatus.Denied or AuthorityStatus.Revoked } =>
                RecoveryAction.Reauthorize,

            { Conflict.Kind: ConflictKind.Revoked } =>
                RecoveryAction.Reauthorize,

            { Conflict.Kind: ConflictKind.Conflict } =>
                RecoveryAction.ResolveConflict,

            { Temporal.IsReadOnlySnapshot: true } =>
                RecoveryAction.RestoreOrBranch,

            { Conflict.Kind: ConflictKind.RemoteChanged } =>
                RecoveryAction.Refetch,

            _ => RecoveryAction.None
        };

        return new RequiredSafetyContext(
            state.Temporal.Mode,
            state.Conflict.Kind,
            state.LastKnownAuthority,
            recovery);
    }
}
