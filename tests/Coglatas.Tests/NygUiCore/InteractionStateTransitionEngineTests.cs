using Nyg.Ui.Core.Interaction;

namespace Coglatas.Tests.NygUiCore;

public sealed class InteractionStateTransitionEngineTests
{
    private static readonly ContextScope ScopeA = new("tenant-a", "workspace-a", "project-a");
    private static readonly ContextScope ScopeB = new("tenant-a", "workspace-a", "project-b");

    private readonly InteractionStateTransitionEngine _engine = new();

    [Fact]
    public void RendererSwitch_PreservesSemanticStateAndGenerationStamp()
    {
        var state = CurrentState() with
        {
            Query = new QueryDescriptor("status:open"),
            Selection = new EntityReference("Task", "T-33"),
            InspectorTarget = new EntityReference("Task", "T-33"),
            Temporal = new TemporalContext(TemporalMode.ExplicitVersion, "v17"),
            Conflict = new ConflictState(ConflictKind.RemoteChanged, 699, 700)
        };
        var before = InteractionStateTransitionEngine.Stamp(state);

        var result = _engine.Process(state, new RendererChanged(new RendererId("Board")));

        Assert.Equal("Board", result.State.ActiveRenderer.Value);
        Assert.Equal(state.Query, result.State.Query);
        Assert.Equal(state.Selection, result.State.Selection);
        Assert.Equal(state.InspectorTarget, result.State.InspectorTarget);
        Assert.Equal(state.Temporal, result.State.Temporal);
        Assert.Equal(state.Conflict, result.State.Conflict);
        Assert.Equal(before, result.Projection.Stamp);
        Assert.Null(result.ExecutionPermit);
        Assert.Same(result.State, result.Projection.SharedState);
    }

    [Fact]
    public void ScopeChange_IncrementsContextGenerationAndInvalidatesOldProjection()
    {
        var state = CurrentState();
        var oldProjection = _engine.CreateProjection(state);

        var changed = _engine.Process(state, new ScopeChanged(ScopeB));

        Assert.Equal(state.ContextGeneration + 1, changed.State.ContextGeneration);
        Assert.Null(changed.State.Selection);
        Assert.Null(changed.State.InspectorTarget);
        Assert.Equal(QueryDescriptor.Empty, changed.State.Query);
        Assert.Equal(TemporalMode.Now, changed.State.Temporal.Mode);
        Assert.True(changed.RequiresRefetch);

        var action = _engine.Process(
            changed.State,
            new ActionRequested(
                CoreActionContracts.MutateCanonical,
                oldProjection.Stamp,
                new AuthoritySnapshot(
                    ScopeB,
                    AuthorityStatus.Allowed,
                    changed.State.AuthorityGeneration)));

        Assert.Equal(ActionDecisionCode.StaleProjection, action.ActionDecision.Code);
        Assert.False(action.ActionDecision.IsAllowed);
    }

    [Theory]
    [InlineData(TemporalMode.ExplicitVersion)]
    [InlineData(TemporalMode.Historical)]
    public void NonCurrentTemporalSnapshot_DeniesOrdinaryMutation_ButKeepsBranchSeparatelyEvaluable(
        TemporalMode mode)
    {
        var state = CurrentState() with
        {
            Temporal = new TemporalContext(mode, "v17")
        };
        var projection = _engine.CreateProjection(state);
        var authority = CurrentAuthority(state);

        var mutate = _engine.Process(
            state,
            new ActionRequested(
                CoreActionContracts.MutateCanonical,
                projection.Stamp,
                authority));

        Assert.Equal(ActionDecisionCode.TemporalMutationDenied, mutate.ActionDecision.Code);
        Assert.False(mutate.ActionDecision.IsAllowed);
        Assert.Null(mutate.ExecutionPermit);

        var branch = _engine.Process(
            state,
            new ActionRequested(
                CoreActionContracts.BranchSnapshot,
                projection.Stamp,
                authority));

        Assert.Equal(ActionDecisionCode.Allowed, branch.ActionDecision.Code);
        Assert.True(branch.ActionDecision.IsAllowed);
        Assert.NotNull(branch.ExecutionPermit);
    }

    [Fact]
    public void GenerationDependencies_AreDerivedFromCoreOwnedActionSemantics()
    {
        Assert.True(CoreActionContracts.TryResolve(
            CoreActionContracts.ReadProjection,
            out var projectionRead));
        Assert.True(CoreActionContracts.TryResolve(
            CoreActionContracts.ReadCanonical,
            out var canonicalRead));
        Assert.True(CoreActionContracts.TryResolve(
            CoreActionContracts.MutateCanonical,
            out var mutation));

        Assert.Equal(
            GenerationDomain.Context | GenerationDomain.Authority,
            GenerationDependencyResolver.Resolve(projectionRead));

        Assert.Equal(
            GenerationDomain.All,
            GenerationDependencyResolver.Resolve(canonicalRead));

        Assert.Equal(
            GenerationDomain.All,
            GenerationDependencyResolver.Resolve(mutation));
    }

    [Fact]
    public void UnknownActionContract_IsRejectedBeforeFreshnessOrAuthorityEvaluation()
    {
        var state = CurrentState();
        var projection = _engine.CreateProjection(state);

        var action = _engine.Process(
            state,
            new ActionRequested(
                new ActionContractId("renderer.supplied.unknown"),
                projection.Stamp,
                CurrentAuthority(state)));

        Assert.Equal(ActionDecisionCode.UnknownActionContract, action.ActionDecision.Code);
        Assert.False(action.ActionDecision.IsAllowed);
        Assert.Null(action.ExecutionPermit);
    }

    [Fact]
    public void IrrelevantCanonicalChange_AdvancesCrWithoutAdvancingGd()
    {
        var state = CurrentState(
            CanonicalDependencyDomain.QueryMembership |
            CanonicalDependencyDomain.SelectedEntity);
        var oldProjection = _engine.CreateProjection(state);

        var remote = _engine.Process(
            state,
            new RemoteUpdated(
                701,
                CanonicalDependencyDomain.WorkflowMetadata));

        Assert.Equal(701, remote.State.CanonicalRevision);
        Assert.Equal(state.CanonicalGeneration, remote.State.CanonicalGeneration);
        Assert.Equal(state.Conflict, remote.State.Conflict);
        Assert.False(remote.RequiresRefetch);
        Assert.Equal(oldProjection.Stamp, remote.Projection.Stamp);

        var mutation = _engine.Process(
            remote.State,
            new ActionRequested(
                CoreActionContracts.MutateCanonical,
                oldProjection.Stamp,
                CurrentAuthority(remote.State)));

        Assert.True(mutation.ActionDecision.IsAllowed);
        Assert.NotNull(mutation.ExecutionPermit);
    }

    [Fact]
    public void RelevantCanonicalChange_AdvancesCrAndGdAndStalesCanonicalSensitiveAction()
    {
        var state = CurrentState(
            CanonicalDependencyDomain.QueryMembership |
            CanonicalDependencyDomain.SelectedEntity);
        var oldProjection = _engine.CreateProjection(state);

        var remote = _engine.Process(
            state,
            new RemoteUpdated(
                701,
                CanonicalDependencyDomain.SelectedEntity));

        Assert.Equal(701, remote.State.CanonicalRevision);
        Assert.Equal(state.CanonicalGeneration + 1, remote.State.CanonicalGeneration);
        Assert.Equal(ConflictKind.RemoteChanged, remote.State.Conflict.Kind);
        Assert.True(remote.RequiresRefetch);

        var mutation = _engine.Process(
            remote.State,
            new ActionRequested(
                CoreActionContracts.MutateCanonical,
                oldProjection.Stamp,
                CurrentAuthority(remote.State)));

        Assert.Equal(ActionDecisionCode.StaleProjection, mutation.ActionDecision.Code);
        Assert.False(mutation.ActionDecision.IsAllowed);
    }

    [Fact]
    public void RelevantCanonicalChange_LeavesProjectionReadEvaluableButStalesCanonicalRead()
    {
        var state = CurrentState(CanonicalDependencyDomain.SelectedEntity);
        var oldProjection = _engine.CreateProjection(state);

        var remote = _engine.Process(
            state,
            new RemoteUpdated(
                701,
                CanonicalDependencyDomain.SelectedEntity));

        var projectionRead = _engine.Process(
            remote.State,
            new ActionRequested(
                CoreActionContracts.ReadProjection,
                oldProjection.Stamp,
                CurrentAuthority(remote.State)));

        Assert.True(projectionRead.ActionDecision.IsAllowed);

        var canonicalRead = _engine.Process(
            remote.State,
            new ActionRequested(
                CoreActionContracts.ReadCanonical,
                oldProjection.Stamp,
                CurrentAuthority(remote.State)));

        Assert.Equal(ActionDecisionCode.StaleProjection, canonicalRead.ActionDecision.Code);
        Assert.False(canonicalRead.ActionDecision.IsAllowed);
    }

    [Fact]
    public void RemoteUpdate_WithLocalDraft_CreatesConflictOnlyWhenProjectionIsAffected()
    {
        var state = CurrentState(CanonicalDependencyDomain.SelectedEntity);
        var localDraft = _engine.Process(state, new LocalDraftChanged(true));

        var unrelated = _engine.Process(
            localDraft.State,
            new RemoteUpdated(
                701,
                CanonicalDependencyDomain.WorkflowMetadata));

        Assert.Equal(ConflictKind.None, unrelated.State.Conflict.Kind);
        Assert.Equal(state.CanonicalGeneration, unrelated.State.CanonicalGeneration);

        var relevant = _engine.Process(
            unrelated.State,
            new RemoteUpdated(
                702,
                CanonicalDependencyDomain.SelectedEntity));

        Assert.Equal(ConflictKind.Conflict, relevant.State.Conflict.Kind);
        Assert.Equal(701, relevant.State.Conflict.BaseRevision);
        Assert.Equal(702, relevant.State.Conflict.RemoteRevision);
        Assert.Equal(state.CanonicalGeneration + 1, relevant.State.CanonicalGeneration);
    }

    [Fact]
    public void ReorderedCanonicalEvent_CannotMoveRevisionOrGenerationBackwards()
    {
        var state = CurrentState(CanonicalDependencyDomain.SelectedEntity);
        var newer = _engine.Process(
            state,
            new RemoteUpdated(
                705,
                CanonicalDependencyDomain.SelectedEntity));
        var generationAfterNewer = newer.State.CanonicalGeneration;

        var reordered = _engine.Process(
            newer.State,
            new RemoteUpdated(
                704,
                CanonicalDependencyDomain.SelectedEntity));
        var duplicate = _engine.Process(
            reordered.State,
            new RemoteUpdated(
                705,
                CanonicalDependencyDomain.SelectedEntity));

        Assert.Equal(705, reordered.State.CanonicalRevision);
        Assert.Equal(generationAfterNewer, reordered.State.CanonicalGeneration);
        Assert.Equal(reordered.State, duplicate.State);
        Assert.False(reordered.RequiresRefetch);
        Assert.False(duplicate.RequiresRefetch);
    }

    [Fact]
    public void CanonicalSynchronized_CanCoverRelevantRevisionEvenAfterLaterIrrelevantCr()
    {
        var state = CurrentState(CanonicalDependencyDomain.SelectedEntity);

        var relevant = _engine.Process(
            state,
            new RemoteUpdated(
                701,
                CanonicalDependencyDomain.SelectedEntity));

        var unrelatedLater = _engine.Process(
            relevant.State,
            new RemoteUpdated(
                702,
                CanonicalDependencyDomain.WorkflowMetadata));

        Assert.Equal(702, unrelatedLater.State.CanonicalRevision);
        Assert.Equal(701, unrelatedLater.State.Conflict.RemoteRevision);

        var synced = _engine.Process(
            unrelatedLater.State,
            new CanonicalSynchronized(701));

        Assert.Equal(ConflictKind.None, synced.State.Conflict.Kind);
    }

    [Fact]
    public void ConflictResolution_RequiresNewerCanonicalRevision()
    {
        var state = CurrentState(CanonicalDependencyDomain.SelectedEntity);
        var draft = _engine.Process(state, new LocalDraftChanged(true));
        var remote = _engine.Process(
            draft.State,
            new RemoteUpdated(
                701,
                CanonicalDependencyDomain.SelectedEntity));

        var staleResolution = _engine.Process(remote.State, new ConflictResolved(701));
        Assert.Equal(remote.State, staleResolution.State);

        var resolved = _engine.Process(remote.State, new ConflictResolved(702));
        Assert.Equal(ConflictKind.None, resolved.State.Conflict.Kind);
        Assert.False(resolved.State.HasLocalDraft);
        Assert.Equal(702, resolved.State.CanonicalRevision);
        Assert.Equal(remote.State.CanonicalGeneration + 1, resolved.State.CanonicalGeneration);
    }

    [Fact]
    public void AuthorityRevocation_ChangesGenerationClearsProtectedTargetsAndStalesOldProjection()
    {
        var state = CurrentState() with
        {
            Selection = new EntityReference("Task", "T-7"),
            InspectorTarget = new EntityReference("Task", "T-7")
        };
        var oldProjection = _engine.CreateProjection(state);

        var revoked = _engine.Process(
            state,
            new AuthorityChanged(AuthorityStatus.Revoked, state.AuthorityGeneration + 1));

        Assert.Equal(AuthorityStatus.Revoked, revoked.State.LastKnownAuthority);
        Assert.Equal(ConflictKind.Revoked, revoked.State.Conflict.Kind);
        Assert.Null(revoked.State.Selection);
        Assert.Null(revoked.State.InspectorTarget);
        Assert.True(revoked.RequiresRefetch);

        var action = _engine.Process(
            revoked.State,
            new ActionRequested(
                CoreActionContracts.ReadProjection,
                oldProjection.Stamp,
                new AuthoritySnapshot(
                    ScopeA,
                    AuthorityStatus.Revoked,
                    revoked.State.AuthorityGeneration)));

        Assert.Equal(ActionDecisionCode.StaleProjection, action.ActionDecision.Code);
    }

    [Fact]
    public void SameOrOlderAuthorityGeneration_CannotRewriteAuthorityState()
    {
        var state = CurrentState();

        var sameGeneration = _engine.Process(
            state,
            new AuthorityChanged(AuthorityStatus.Revoked, state.AuthorityGeneration));

        Assert.Equal(state, sameGeneration.State);

        var newer = _engine.Process(
            state,
            new AuthorityChanged(AuthorityStatus.Revoked, state.AuthorityGeneration + 2));

        var older = _engine.Process(
            newer.State,
            new AuthorityChanged(AuthorityStatus.Allowed, state.AuthorityGeneration + 1));

        Assert.Equal(newer.State, older.State);
        Assert.Equal(AuthorityStatus.Revoked, older.State.LastKnownAuthority);
    }

    [Fact]
    public void CurrentAllowedMutation_EmitsExecutionPermit()
    {
        var state = CurrentState(
            CanonicalDependencyDomain.QueryMembership |
            CanonicalDependencyDomain.SelectedEntity);
        var projection = _engine.CreateProjection(state);

        var action = _engine.Process(
            state,
            new ActionRequested(
                CoreActionContracts.MutateCanonical,
                projection.Stamp,
                CurrentAuthority(state)));

        Assert.True(action.ActionDecision.IsAllowed);
        Assert.Equal(ActionDecisionCode.Allowed, action.ActionDecision.Code);
        Assert.NotNull(action.ExecutionPermit);
        Assert.Equal(state.AuthorityGeneration, action.ExecutionPermit!.Precondition.ExpectedAuthorityGeneration);
        Assert.Equal(state.CanonicalRevision, action.ExecutionPermit.Precondition.ObservedCanonicalRevision);
        Assert.Equal(state.CanonicalDependencies, action.ExecutionPermit.Precondition.RequiredCanonicalDomains);
    }

    [Fact]
    public void AtomicCommit_AllowsLaterCanonicalChangesOutsideRequiredDomains()
    {
        var state = CurrentState(CanonicalDependencyDomain.SelectedEntity);
        var projection = _engine.CreateProjection(state);
        var allowed = _engine.Process(
            state,
            new ActionRequested(
                CoreActionContracts.MutateCanonical,
                projection.Stamp,
                CurrentAuthority(state)));

        var decision = AtomicCommitPreconditionEvaluator.Evaluate(
            allowed.ExecutionPermit!.Precondition,
            new AuthoritativeCommitSnapshot(
                ScopeA,
                AuthorityStatus.Allowed,
                state.AuthorityGeneration,
                CurrentCanonicalRevision: 702,
                CanonicalChangesSinceObservation:
                [
                    new CanonicalChangeRecord(701, CanonicalDependencyDomain.WorkflowMetadata),
                    new CanonicalChangeRecord(702, CanonicalDependencyDomain.Auxiliary)
                ]));

        Assert.True(decision.IsAllowed);
        Assert.Equal(CommitDecisionCode.Allowed, decision.Code);
    }

    [Fact]
    public void AtomicCommit_RejectsRelevantCanonicalRaceAfterInteractionDecision()
    {
        var state = CurrentState(CanonicalDependencyDomain.SelectedEntity);
        var projection = _engine.CreateProjection(state);
        var allowed = _engine.Process(
            state,
            new ActionRequested(
                CoreActionContracts.MutateCanonical,
                projection.Stamp,
                CurrentAuthority(state)));

        var decision = AtomicCommitPreconditionEvaluator.Evaluate(
            allowed.ExecutionPermit!.Precondition,
            new AuthoritativeCommitSnapshot(
                ScopeA,
                AuthorityStatus.Allowed,
                state.AuthorityGeneration,
                CurrentCanonicalRevision: 702,
                CanonicalChangesSinceObservation:
                [
                    new CanonicalChangeRecord(701, CanonicalDependencyDomain.WorkflowMetadata),
                    new CanonicalChangeRecord(702, CanonicalDependencyDomain.SelectedEntity)
                ]));

        Assert.False(decision.IsAllowed);
        Assert.Equal(CommitDecisionCode.StaleCanonical, decision.Code);
    }

    [Fact]
    public void AtomicCommit_RejectsAuthorityRaceAfterInteractionDecision()
    {
        var state = CurrentState(CanonicalDependencyDomain.SelectedEntity);
        var projection = _engine.CreateProjection(state);
        var allowed = _engine.Process(
            state,
            new ActionRequested(
                CoreActionContracts.MutateCanonical,
                projection.Stamp,
                CurrentAuthority(state)));

        var decision = AtomicCommitPreconditionEvaluator.Evaluate(
            allowed.ExecutionPermit!.Precondition,
            new AuthoritativeCommitSnapshot(
                ScopeA,
                AuthorityStatus.Revoked,
                state.AuthorityGeneration + 1,
                CurrentCanonicalRevision: state.CanonicalRevision,
                CanonicalChangesSinceObservation: []));

        Assert.False(decision.IsAllowed);
        Assert.Equal(CommitDecisionCode.StaleAuthority, decision.Code);
    }

    [Fact]
    public void RevokedState_SurvivesCanonicalEventsAndBlocksProtectedAction()
    {
        var state = CurrentState(CanonicalDependencyDomain.SelectedEntity);
        var revoked = _engine.Process(
            state,
            new AuthorityChanged(
                AuthorityStatus.Revoked,
                state.AuthorityGeneration + 1));

        var remote = _engine.Process(
            revoked.State,
            new RemoteUpdated(
                701,
                CanonicalDependencyDomain.SelectedEntity));

        Assert.Equal(ConflictKind.Revoked, remote.State.Conflict.Kind);

        var resolved = _engine.Process(remote.State, new ConflictResolved(702));

        Assert.Equal(ConflictKind.Revoked, resolved.State.Conflict.Kind);

        var action = _engine.Process(
            resolved.State,
            new ActionRequested(
                CoreActionContracts.ResolveConflict,
                resolved.Projection.Stamp,
                new AuthoritySnapshot(
                    ScopeA,
                    AuthorityStatus.Allowed,
                    resolved.State.AuthorityGeneration)));

        Assert.Equal(ActionDecisionCode.AuthorityDenied, action.ActionDecision.Code);
        Assert.False(action.ActionDecision.IsAllowed);
    }

    [Fact]
    public void ScopeChange_RequiresCoreReauthorizationBeforeProtectedAction_ButReadCanProceed()
    {
        var state = CurrentState();
        var changed = _engine.Process(state, new ScopeChanged(ScopeB));
        var allowedSnapshot = new AuthoritySnapshot(
            ScopeB,
            AuthorityStatus.Allowed,
            changed.State.AuthorityGeneration);

        var mutate = _engine.Process(
            changed.State,
            new ActionRequested(
                CoreActionContracts.MutateCanonical,
                changed.Projection.Stamp,
                allowedSnapshot));

        Assert.Equal(ActionDecisionCode.AuthorityDenied, mutate.ActionDecision.Code);
        Assert.False(mutate.ActionDecision.IsAllowed);

        var read = _engine.Process(
            changed.State,
            new ActionRequested(
                CoreActionContracts.ReadProjection,
                changed.Projection.Stamp,
                allowedSnapshot));

        Assert.Equal(ActionDecisionCode.Allowed, read.ActionDecision.Code);
        Assert.True(read.ActionDecision.IsAllowed);
    }

    [Fact]
    public void TemporalChange_InvalidatesOldProjectionAndEqualChangeIsIdempotent()
    {
        var state = CurrentState();
        var oldProjection = _engine.CreateProjection(state);
        var historical = new TemporalContext(TemporalMode.Historical, "v42");

        var changed = _engine.Process(state, new TemporalChanged(historical));

        Assert.Equal(state.ContextGeneration + 1, changed.State.ContextGeneration);
        Assert.Equal(historical, changed.State.Temporal);
        Assert.True(changed.RequiresRefetch);

        var staleAction = _engine.Process(
            changed.State,
            new ActionRequested(
                CoreActionContracts.MutateCanonical,
                oldProjection.Stamp,
                CurrentAuthority(changed.State)));

        Assert.Equal(ActionDecisionCode.StaleProjection, staleAction.ActionDecision.Code);
        Assert.False(staleAction.ActionDecision.IsAllowed);

        var same = _engine.Process(changed.State, new TemporalChanged(historical));

        Assert.Equal(changed.State, same.State);
        Assert.False(same.RequiresRefetch);
    }

    [Fact]
    public void PublicContractValues_AreObservableByRendererAdapters()
    {
        var query = new QueryDescriptor("status:open");
        Assert.Equal("status:open", query.Value);

        var entity = new EntityReference("Task", "T-42");
        Assert.Equal("Task", entity.Kind);
        Assert.Equal("T-42", entity.Id);

        var safety = new RequiredSafetyContext(
            TemporalMode.Historical,
            ConflictKind.Conflict,
            AuthorityStatus.Denied,
            RecoveryAction.ResolveConflict);
        Assert.Equal(TemporalMode.Historical, safety.TemporalMode);
        Assert.Equal(ConflictKind.Conflict, safety.ConflictKind);
        Assert.Equal(AuthorityStatus.Denied, safety.AuthorityStatus);
        Assert.Equal(RecoveryAction.ResolveConflict, safety.RecoveryAction);

        var state = CurrentState();
        var envelope = new ProjectionEnvelope(
            new RendererId("Board"),
            new ProjectionStamp(1, 2, 3),
            safety,
            state);
        Assert.Equal(new RendererId("Board"), envelope.Renderer);
        Assert.Equal(safety, envelope.RequiredSafety);

        var actionDecision = new ActionDecision(
            ActionDecisionCode.AuthorityDenied,
            false,
            "renderer-visible action reason");
        Assert.Equal("renderer-visible action reason", actionDecision.Reason);

        var commitDecision = new CommitDecision(
            CommitDecisionCode.StaleCanonical,
            false,
            "renderer-visible commit reason");
        Assert.Equal("renderer-visible commit reason", commitDecision.Reason);

        Assert.True(CoreActionContracts.TryResolve(
            CoreActionContracts.RestoreSnapshot,
            out var restore));
        Assert.Equal(InteractionActionKind.Restore, restore.Kind);
    }

    private static WorkSurfaceState CurrentState(
        CanonicalDependencyDomain dependencies = CanonicalDependencyDomain.All) =>
        WorkSurfaceState.Create(
            ScopeA,
            new RendererId("Table"),
            authorityGeneration: 4,
            canonicalGeneration: 71,
            canonicalRevision: 700,
            canonicalDependencies: dependencies) with
        {
            LastKnownAuthority = AuthorityStatus.Allowed
        };

    private static AuthoritySnapshot CurrentAuthority(WorkSurfaceState state) =>
        new(
            state.Scope,
            AuthorityStatus.Allowed,
            state.AuthorityGeneration);
}
