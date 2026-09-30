import { computed, inject, Injectable, InjectionToken, signal } from '@angular/core';
import { Subscription } from 'rxjs';

import { AuthSessionFacade } from '../../core/auth/auth-session.facade';
import { FrontendFeatureFlagsService } from '../../core/feature-flags/frontend-feature-flags.service';
import {
  ProtectedStateClearReason,
  RealtimeFacade,
} from '../../core/realtime/realtime.facade';
import { DurableRealtimeEvent } from '../../core/realtime/realtime.models';
import { DraftStorageService } from './draft-storage.service';
import { ConversationInboxResponseDto, MessagingApi, ParticipantStateDto } from './messaging.api';
import {
  mapConversationListItem,
  mapConversationPage,
  mapMessage,
  mapMessageThread,
  mapThreadSummary
} from './messaging.mapper';
import {
  MessagingDraftScope,
  MessagingInboxCounts,
  MessagingInboxView,
  MessagingInboxViewModel,
  MessageFailureCode,
  MessagingMessageActionState,
  MessagingMessageViewModel,
  MessagingPageStatus,
  MessagingPageViewModel,
  MessagingRouteKind,
  MessagingThreadViewModel
} from './messaging.types';
import { MessageNavigationStateService } from './message-navigation-state.service';

export const COGLATAS_MESSAGING_PAGE_MOCK = new InjectionToken<MessagingPageViewModel>(
  'COGLATAS_MESSAGING_PAGE_MOCK'
);

const MESSAGING_REALTIME_OWNER = 'messaging-route';
const EMPTY_MESSAGE_ACTION: MessagingMessageActionState = {
  messageId: null,
  mode: 'idle',
  draft: '',
  pending: null
};
const EMPTY_THREAD: MessagingThreadViewModel = {
  status: 'closed',
  rootMessageId: null,
  replies: [],
  hasMore: false,
  maximumReplies: 100,
  draft: '',
  sending: false
};
const EMPTY_INBOX_COUNTS: MessagingInboxCounts = {
  all: 0,
  unread: 0,
  mentions: 0,
  later: 0
};

@Injectable({ providedIn: 'root' })
export class MessagingFacade {
  private readonly api = inject(MessagingApi);
  private readonly authSession = inject(AuthSessionFacade);
  private readonly flags = inject(FrontendFeatureFlagsService);
  private readonly realtime = inject(RealtimeFacade);
  private readonly draftStorage = inject(DraftStorageService);
  private readonly navigationState = inject(MessageNavigationStateService);
  private readonly mockPage = inject(COGLATAS_MESSAGING_PAGE_MOCK, { optional: true });
  private readonly initialPage = this.mockPage ?? emptyMessagingPage('channel', 'loading');
  private readonly pageState = signal<MessagingPageViewModel>(
    this.withStoredDraft(this.initialPage)
  );
  private readonly messageActionState = signal<MessagingMessageActionState>(EMPTY_MESSAGE_ACTION);
  private readonly threadState = signal<MessagingThreadViewModel>(EMPTY_THREAD);
  private readonly inboxState = signal<MessagingInboxViewModel>(emptyMessagingInbox());
  private readonly loadedConversationId = signal<string | null>(
    this.mockPage?.conversation.id ?? null
  );
  private loadedExpectedWorkspaceId: string | null = null;
  private loadedAnchorMessageId: string | null = null;
  private realtimeSubscriptionCleanup: (() => void) | undefined;
  private realtimeCatchUpCleanup: (() => void) | undefined;
  private readonly durableEvents: Subscription;
  private readonly protectedRequests = new Set<Subscription>();
  private requestGeneration = 0;
  private messageActionFeedbackId = 0;
  private threadRequestGeneration = 0;
  private threadAnchorReplyMessageId: string | null = null;
  private readonly threadRefreshGenerations = new Map<string, number>();

  readonly page = computed(() => this.withSortedMessages(this.pageState()));
  readonly messageAction = this.messageActionState.asReadonly();
  readonly thread = this.threadState.asReadonly();
  readonly inbox = this.inboxState.asReadonly();

  constructor() {
    this.durableEvents = this.realtime.durableEvents$.subscribe((event) => this.applyRealtimeEvent(event));
    if (!this.mockPage) {
      this.realtime.registerProtectedStateClearer?.(
        'messaging',
        (reason) => this.clearProtectedState(reason),
      );
    }
  }

  loadConversationListPage(routeKind: MessagingRouteKind = 'channel'): void {
    if (this.mockPage) {
      return;
    }

    const view = this.inboxState().view;
    const generation = this.beginRequestGeneration();
    this.releaseConversationRealtime();
    this.loadedConversationId.set(null);
    this.loadedAnchorMessageId = null;
    this.pageState.set(emptyMessagingPage(routeKind, 'loading'));
    this.inboxState.update((inbox) => ({ ...inbox, status: 'loading', requestedView: view, error: undefined }));
    this.realtimeCatchUpCleanup = this.realtime.registerCatchUp(
      MESSAGING_REALTIME_OWNER,
      () => this.catchUpConversationList(routeKind),
    );
    void this.loadConversationList(routeKind, true, generation, view);
  }

  selectInboxView(view: MessagingInboxView): void {
    if (this.mockPage || (this.inboxState().status === 'loading' && this.inboxState().requestedView === view)) {
      return;
    }

    const generation = this.beginRequestGeneration();
    this.inboxState.update((inbox) => ({
      ...inbox,
      status: 'loading',
      requestedView: view,
      laterPendingConversationId: undefined,
      error: undefined
    }));
    void this.loadConversationList(this.pageState().routeKind, true, generation, view, true);
  }

  setConversationLater(conversationId: string, isLater: boolean): void {
    const conversation = this.pageState().conversations.find((item) => item.id === conversationId);
    const inbox = this.inboxState();
    if (
      this.mockPage ||
      !conversation ||
      inbox.laterPendingConversationId ||
      conversation.isLater === isLater
    ) {
      return;
    }

    const generation = this.requestGeneration;
    this.inboxState.set({
      ...inbox,
      laterPendingConversationId: conversationId,
      error: undefined
    });
    const request = this.api.updateConversationLater(conversationId, isLater).subscribe({
      next: (response) => {
        if (generation !== this.requestGeneration) {
          return;
        }
        if (!isMatchingLaterState(response, conversationId, isLater)) {
          this.failLaterMutation(conversationId);
          return;
        }

        const currentView = this.inboxState().view;
        const refreshGeneration = this.beginRequestGeneration();
        this.inboxState.update((current) => ({
          ...current,
          status: 'loading',
          requestedView: currentView,
          laterPendingConversationId: conversationId,
          error: undefined
        }));
        void this.loadConversationList(
          this.pageState().routeKind,
          true,
          refreshGeneration,
          currentView,
          true
        );
      },
      error: () => {
        if (generation === this.requestGeneration) {
          this.failLaterMutation(conversationId);
        }
      }
    });
    this.trackProtectedRequest(request);
  }

  loadConversation(
    conversationId: string | null,
    routeKind: 'channel',
    expectedWorkspaceId: string | null,
    anchorMessageId?: string | null,
  ): void;
  loadConversation(
    conversationId: string | null,
    routeKind: 'dm',
    expectedWorkspaceId?: null,
    anchorMessageId?: string | null,
  ): void;
  loadConversation(
    conversationId: string | null,
    routeKind: MessagingRouteKind,
    expectedWorkspaceId: string | null = null,
    anchorMessageId: string | null = null,
  ): void {
    if (this.mockPage) {
      return;
    }

    const routeWorkspaceId = routeKind === 'channel' ? nonEmptyString(expectedWorkspaceId) : null;
    if (!conversationId || (routeKind === 'channel' && !routeWorkspaceId)) {
      this.beginRequestGeneration();
      this.releaseConversationRealtime();
      this.loadedConversationId.set(null);
      this.loadedExpectedWorkspaceId = null;
      this.loadedAnchorMessageId = null;
      this.pageState.set(
        emptyMessagingPage(
          routeKind,
          routeKind === 'channel' ? 'permissionDenied' : 'empty',
          'Conversation route scope is missing.',
        )
      );
      return;
    }

    if (
      this.loadedConversationId() === conversationId &&
      this.loadedExpectedWorkspaceId === routeWorkspaceId &&
      this.loadedAnchorMessageId === nonEmptyString(anchorMessageId)
    ) {
      return;
    }

    const generation = this.beginRequestGeneration();
    this.releaseConversationRealtime();
    this.loadedConversationId.set(conversationId);
    this.loadedExpectedWorkspaceId = routeWorkspaceId;
    this.loadedAnchorMessageId = nonEmptyString(anchorMessageId);
    const currentPage = this.pageState();
    const existingConversations =
      currentPage.conversation.tenantId === this.currentTenantId()
        ? currentPage.conversations
        : [];
    this.pageState.set({
      ...emptyMessagingPage(routeKind, 'loading'),
      conversations: existingConversations
    });
    void this.loadConversationData(
      conversationId,
      routeKind,
      generation,
      routeWorkspaceId,
      true,
      this.loadedAnchorMessageId,
    );
  }

  private loadConversationData(
    conversationId: string,
    routeKind: MessagingRouteKind,
    generation: number,
    expectedWorkspaceId: string | null,
    registerRealtimeAfterValidation: boolean,
    anchorMessageId: string | null = null,
  ): Promise<void> {
    const listCompletion = this.loadConversationList(routeKind, false, generation);
    const detailCompletion = new Promise<void>((resolve) => {
      let messagesStarted = false;
      let settled = false;
      const settle = (): void => {
        if (!settled) {
          settled = true;
          resolve();
        }
      };
      const conversationRequest = this.api.getConversation(conversationId).subscribe({
        next: (conversation) => {
          if (!this.isCurrentRequest(generation, conversationId)) {
            settle();
            return;
          }
          if (
            expectedWorkspaceId !== null &&
            nonEmptyString(conversation.workspaceId) !== expectedWorkspaceId
          ) {
            this.rejectConversationOutsideRouteWorkspace(generation, conversationId, routeKind);
            settle();
            return;
          }
          if (registerRealtimeAfterValidation) {
            this.registerConversationRealtime(conversationId);
          }
          messagesStarted = true;
          const messagesRequest = this.api.listMessages(conversationId, undefined, anchorMessageId ?? undefined).subscribe({
            next: (response) => {
              if (!this.isCurrentRequest(generation, conversationId)) {
                return;
              }
              const page = mapConversationPage(conversation, response.items ?? [], {
                currentUserId: this.currentUserId(),
                currentTenantId: this.currentTenantId(),
                fallbackRouteKind: routeKind,
                existingConversations: this.pageState().conversations
              });
              this.pageState.set(this.withStoredDraft(page));
            },
            error: (error: { status?: number }) => {
              if (this.isCurrentRequest(generation, conversationId)) {
                this.rejectIncompleteConversationAggregate(
                  generation,
                  conversationId,
                  routeKind,
                  error.status,
                );
              }
              settle();
            },
            complete: settle,
          });
          messagesRequest.add(settle);
          this.trackProtectedRequest(messagesRequest);
        },
        error: (error: { status?: number }) => {
          if (this.isCurrentRequest(generation, conversationId)) {
            this.pageState.set(
              emptyMessagingPage(
                routeKind,
                error.status === 401 || error.status === 403 ? 'permissionDenied' : 'empty',
                error.status === 401 || error.status === 403
                  ? 'Authentication or conversation permission is required.'
                  : 'Conversation API request failed.'
              )
            );
          }
          settle();
        },
        complete: () => {
          if (!messagesStarted) {
            settle();
          }
        },
      });
      conversationRequest.add(() => {
        if (!messagesStarted) {
          settle();
        }
      });
      this.trackProtectedRequest(conversationRequest);
    });
    return Promise.all([listCompletion, detailCompletion]).then(() => undefined);
  }

  setDraft(value: string): void {
    const page = this.pageState();
    this.draftStorage.writeDraft(this.scopeFor(page), value);
    this.pageState.set({ ...page, draft: value });
  }

  openThread(messageId: string, triggerElementId: string, anchorReplyMessageId?: string): void {
    const page = this.pageState();
    const root = page.messages.find((message) => message.id === messageId);
    if (
      !root ||
      root.threadRootMessageId ||
      (root.isDeleted && (root.thread?.replyCount ?? 0) === 0) ||
      root.deliveryState !== 'confirmed' ||
      !page.conversation.capabilities.includes('readBody') ||
      !page.conversation.viewerIsParticipant ||
      page.conversation.viewerWasRemoved
    ) {
      return;
    }
    if (this.mockPage) {
      this.threadState.set({
        ...EMPTY_THREAD,
        status: 'error',
        rootMessageId: messageId,
        triggerElementId,
        error: 'Thread loading requires the backend API.'
      });
      return;
    }
    this.loadThread(messageId, triggerElementId, anchorReplyMessageId);
  }

  closeThread(returnFocus = true): void {
    const triggerElementId = this.threadState().triggerElementId;
    const closeGeneration = ++this.threadRequestGeneration;
    this.threadAnchorReplyMessageId = null;
    this.threadState.set(EMPTY_THREAD);
    if (returnFocus) {
      // On the dedicated mobile pane the conversation remains display:none
      // until Angular renders the closed state. The correct trigger therefore
      // exists during this microtask but cannot accept focus yet. Verify the
      // focus result and retry after at most two render frames, re-querying the
      // current DOM each time so a replaced trigger is never captured stale.
      queueMicrotask(() => this.restoreClosedThreadFocus(triggerElementId, closeGeneration, 2));
    }
  }

  setThreadDraft(value: string): void {
    this.threadState.update((thread) => thread.status === 'ready'
      ? { ...thread, draft: value, pendingClientRequestId: undefined, error: undefined }
      : thread);
  }

  sendThreadDraft(mentionedUserIds: readonly string[] = []): void {
    const page = this.pageState();
    const thread = this.threadState();
    const body = thread.draft.trim();
    if (
      this.mockPage ||
      thread.status !== 'ready' ||
      !thread.rootMessageId ||
      !body ||
      thread.sending ||
      !this.canReplyToThread(page, thread)
    ) {
      return;
    }

    const generation = this.threadRequestGeneration;
    const rootMessageId = thread.rootMessageId;
    const clientRequestId = thread.pendingClientRequestId ?? createClientRequestId();
    const normalizedMentionedUserIds = [...new Set(mentionedUserIds.filter((userId) => userId.length > 0))];
    this.threadState.set({
      ...thread,
      sending: true,
      pendingClientRequestId: clientRequestId,
      error: undefined
    });
    const request = this.api.sendThreadMessage(
      rootMessageId,
      body,
      clientRequestId,
      normalizedMentionedUserIds
    ).subscribe({
      next: (response) => {
        const active = this.threadState();
        if (
          generation !== this.threadRequestGeneration ||
          active.status !== 'ready' ||
          active.rootMessageId !== rootMessageId ||
          !response.message ||
          !response.summary
        ) {
          return;
        }
        const responseMessageId = nonEmptyString(response.message.id);
        const message = mapMessage(response.message, this.currentUserId());
        const summary = mapThreadSummary(response.summary);
        if (
          !responseMessageId ||
          message.id !== responseMessageId ||
          message.threadRootMessageId !== rootMessageId ||
          nonEmptyString(response.message.conversationId) !== page.conversation.id ||
          summary?.threadRootMessageId !== rootMessageId
        ) {
          this.failThreadLoad(rootMessageId, active.triggerElementId, undefined);
          return;
        }
        // A validation-400 revalidation may still be in flight when a same-key
        // retry succeeds. Invalidate that older projection without preventing
        // a subsequent ThreadChanged event from starting a newer refresh.
        this.threadRefreshGenerations.set(
          rootMessageId,
          (this.threadRefreshGenerations.get(rootMessageId) ?? 0) + 1
        );
        this.threadState.set({
          ...active,
          replies: reconcileMessage(active.replies, message),
          summary,
          draft: '',
          sending: false,
          pendingClientRequestId: undefined,
          error: undefined
        });
        this.updateRootThreadSummary(rootMessageId, summary);
      },
      error: (error: { status?: number }) => {
        const active = this.threadState();
        if (
          generation !== this.threadRequestGeneration ||
          active.status !== 'ready' ||
          active.rootMessageId !== rootMessageId
        ) {
          return;
        }
        if (isExplicitThreadAccessFailure(error.status)) {
          this.failThreadLoad(rootMessageId, active.triggerElementId, error.status);
          return;
        }
        const rejectedState: MessagingThreadViewModel = {
          ...active,
          sending: false,
          pendingClientRequestId: clientRequestId,
          error: error.status === 400
            ? 'Thread reply was rejected. Your draft is still available.'
            : 'Thread reply could not be sent. Your draft is still available.'
        };
        this.threadState.set(rejectedState);
        // The controller intentionally represents validation, safety, and
        // idempotency-target failures as 400. A POST 400 is therefore not an
        // authorization signal: retain the draft/retry identity and revalidate
        // the protected projection through its authoritative GET boundary.
        if (error.status === 400) {
          this.refreshThreadProjection(rootMessageId, true);
        }
      }
    });
    this.trackProtectedRequest(request);
  }

  manualRefresh(): void {
    if (!this.mockPage) {
      const page = this.pageState();
      const conversationId = page.conversation.id || this.loadedConversationId();
      if (conversationId) {
        this.loadedConversationId.set(null);
        if (page.routeKind === 'channel') {
          this.loadConversation(
            conversationId,
            'channel',
            this.loadedExpectedWorkspaceId ?? page.conversation.workspaceId ?? null,
            this.loadedAnchorMessageId,
          );
        } else {
          this.loadConversation(conversationId, 'dm', null, this.loadedAnchorMessageId);
        }
      } else {
        this.loadConversationListPage(page.routeKind);
      }
      return;
    }

    this.pageState.update((page) => ({
      ...page,
      inlineError: 'Manual refresh requires the backend API.'
    }));
  }

  acknowledgeNewMessages(): void {
    this.pageState.update((page) => ({ ...page, hasNewMessagesWhileReading: false }));
  }

  loadOlder(): void {
    this.pageState.update((page) => ({
      ...page,
      inlineError: 'Message paging is disabled for MVP0.'
    }));
  }

  loadNewer(): void {
    this.pageState.update((page) => ({
      ...page,
      inlineError: 'Message paging is disabled for MVP0.'
    }));
  }

  sendDraft(mentionedUserIds: readonly string[] = []): void {
    const page = this.pageState();
    const body = page.draft.trim();
    if (!body || page.sending || !this.canPost(page)) {
      return;
    }

    if (this.mockPage) {
      this.pageState.update((current) => ({
        ...current,
        inlineError: 'Message sending requires the backend API.',
        sendState: { status: 'failed', message: 'Message sending requires the backend API.' }
      }));
      return;
    }

    const normalizedMentionedUserIds = [...new Set(mentionedUserIds.filter((userId) => userId.length > 0))];
    const clientRequestId = createClientRequestId();
    const generation = this.requestGeneration;
    const conversationId = page.conversation.id;
    const useOptimistic = this.flags.optimisticMessagingEnabled();
    const pendingMessage: MessagingMessageViewModel = {
      id: `pending-${clientRequestId}`,
      clientRequestId,
      authorUserId: this.currentUserId(),
      authorLabel: this.currentUserDisplayName(),
      authorRoleLabel: 'member',
      isOwnMessage: true,
      body,
      isDeleted: false,
      sentAtLabel: 'Sending',
      deliveryState: 'sending',
      retryAllowed: false,
      mentionedUserIds: normalizedMentionedUserIds
    };

    this.pageState.set({
      ...page,
      sending: true,
      sendState: { status: 'sending', clientRequestId },
      inlineError: undefined,
      messages: useOptimistic ? [...page.messages, pendingMessage] : page.messages,
      status: page.status === 'empty' ? 'ready' : page.status
    });

    const request = this.api.sendMessage(page.conversation.id, body, clientRequestId, normalizedMentionedUserIds).subscribe({
      next: (message) => {
        if (!this.isCurrentRequest(generation, conversationId)) {
          return;
        }
        const confirmedMessage = {
          ...mapMessage(message, this.currentUserId()),
          clientRequestId,
          mentionedUserIds: normalizedMentionedUserIds
        };
        this.draftStorage.clearDraft(this.scopeFor(page));
        this.pageState.update((current) => ({
          ...current,
          draft: '',
          sending: false,
          sendState: { status: 'sent', messageId: confirmedMessage.id },
          status: current.status === 'empty' ? 'ready' : current.status,
          messages: reconcileMessage(current.messages, confirmedMessage)
        }));
      },
      error: (error: { status?: number }) => {
        if (!this.isCurrentRequest(generation, conversationId)) {
          return;
        }
        const message = sendFailureMessage(error.status);
        this.pageState.update((current) => ({
          ...current,
          sending: false,
          sendState: { status: 'failed', message },
          inlineError: message,
          messages: current.messages.map((existing) => existing.id === pendingMessage.id
            ? { ...existing, deliveryState: 'failed', failureCode: failureCode(error.status), safeFailureReason: message, retryAllowed: true }
            : existing)
        }));
      }
    });
    this.trackProtectedRequest(request);
  }

  retryMessage(messageId: string): void {
    const page = this.pageState();
    const failed = page.messages.find((message) => message.id === messageId && message.deliveryState === 'failed');
    if (!failed?.clientRequestId || !this.canPost(page) || this.mockPage) {
      return;
    }
    const generation = this.requestGeneration;
    const conversationId = page.conversation.id;
    this.pageState.update((current) => ({
      ...current,
      sending: true,
      sendState: { status: 'sending', clientRequestId: failed.clientRequestId },
      messages: current.messages.map((message) => message.id === messageId ? { ...message, deliveryState: 'sending', failureCode: undefined, safeFailureReason: undefined, retryAllowed: false } : message)
    }));
    const request = this.api.sendMessage(page.conversation.id, failed.body, failed.clientRequestId, failed.mentionedUserIds ?? []).subscribe({
      next: (message) => {
        if (!this.isCurrentRequest(generation, conversationId)) {
          return;
        }
        const confirmed = {
          ...mapMessage(message, this.currentUserId()),
          clientRequestId: failed.clientRequestId,
          mentionedUserIds: failed.mentionedUserIds
        };
        this.pageState.update((current) => ({ ...current, sending: false, sendState: { status: 'sent', messageId: confirmed.id }, messages: reconcileMessage(current.messages, confirmed) }));
      },
      error: (error: { status?: number }) => {
        if (!this.isCurrentRequest(generation, conversationId)) {
          return;
        }
        const safeMessage = sendFailureMessage(error.status);
        this.pageState.update((current) => ({ ...current, sending: false, sendState: { status: 'failed', message: safeMessage, requestId: failed.clientRequestId }, messages: current.messages.map((message) => message.id === messageId ? { ...message, deliveryState: 'failed', failureCode: failureCode(error.status), safeFailureReason: safeMessage, retryAllowed: true } : message) }));
      }
    });
    this.trackProtectedRequest(request);
  }

  beginMessageEdit(messageId: string): void {
    const message = this.messageActionTarget(messageId, true);
    if (!message || !this.canPost(this.pageState()) || this.messageActionState().mode !== 'idle' || this.mockPage) {
      return;
    }

    this.messageActionState.set({
      messageId,
      mode: 'editing',
      draft: message.body,
      pending: null
    });
  }

  updateMessageEditDraft(messageId: string, draft: string): void {
    const action = this.messageActionState();
    if (action.messageId !== messageId || action.mode !== 'editing' || action.pending) {
      return;
    }
    this.messageActionState.set({ ...action, draft, error: undefined });
  }

  saveMessageEdit(messageId: string): void {
    const action = this.messageActionState();
    const message = this.messageActionTarget(messageId, true);
    const body = action.draft.trim();
    if (
      action.messageId !== messageId ||
      action.mode !== 'editing' ||
      action.pending ||
      !message ||
      !this.canPost(this.pageState()) ||
      this.mockPage
    ) {
      return;
    }
    if (!body) {
      this.messageActionState.set({ ...action, error: 'Enter a message before saving.' });
      return;
    }

    const generation = this.requestGeneration;
    const conversationId = this.pageState().conversation.id;
    this.messageActionState.set({ ...action, pending: 'edit', error: undefined });
    const request = this.api.updateMessage(messageId, body).subscribe({
      next: (updated) => {
        if (!this.isCurrentRequest(generation, conversationId)) {
          return;
        }
        const activeAction = this.messageActionState();
        const currentMessage = this.pageState().messages.find((current) => current.id === messageId);
        if (
          activeAction.messageId !== messageId ||
          activeAction.mode !== 'editing' ||
          activeAction.pending !== 'edit' ||
          !currentMessage
        ) {
          return;
        }
        const mapped = mapMessage(updated, this.currentUserId());
        if (
          currentMessage.version !== undefined &&
          (mapped.version === undefined || mapped.version < currentMessage.version)
        ) {
          this.setMessageActionFeedback('Message changed. Refresh the conversation before trying again.', true);
          return;
        }
        this.pageState.update((page) => ({
          ...page,
          messages: page.messages.map((current) => current.id === messageId
            ? { ...mapped, readState: current.readState, mentionedUserIds: current.mentionedUserIds }
            : current)
        }));
        this.setMessageActionFeedback('Message updated.', true);
      },
      error: (error: { status?: number; httpStatus?: number }) => {
        if (this.isCurrentRequest(generation, conversationId)) {
          this.messageActionState.update((current) => current.messageId === messageId && current.mode === 'editing'
            ? { ...current, pending: null, error: messageActionFailureMessage(httpStatus(error)) }
            : current);
        }
      }
    });
    this.trackProtectedRequest(request);
  }

  requestMessageDelete(messageId: string): void {
    const message = this.messageActionTarget(messageId, true);
    if (!message || this.messageActionState().mode !== 'idle' || this.mockPage) {
      return;
    }
    this.messageActionState.set({
      messageId,
      mode: 'confirmDelete',
      draft: '',
      pending: null
    });
  }

  confirmMessageDelete(messageId: string): void {
    const action = this.messageActionState();
    const message = this.messageActionTarget(messageId, true);
    if (
      action.messageId !== messageId ||
      action.mode !== 'confirmDelete' ||
      action.pending ||
      !message ||
      this.mockPage
    ) {
      return;
    }

    const generation = this.requestGeneration;
    const conversationId = this.pageState().conversation.id;
    this.messageActionState.set({ ...action, pending: 'delete', error: undefined });
    const request = this.api.deleteMessage(messageId).subscribe({
      next: () => {
        if (!this.isCurrentRequest(generation, conversationId)) {
          return;
        }
        this.reconcileDeletedTimelineMessage(messageId);
        this.setMessageActionFeedback('Message deleted.', true);
      },
      error: (error: { status?: number; httpStatus?: number }) => {
        if (this.isCurrentRequest(generation, conversationId)) {
          this.messageActionState.update((current) => current.messageId === messageId && current.mode === 'confirmDelete'
            ? { ...current, pending: null, error: messageActionFailureMessage(httpStatus(error)) }
            : current);
        }
      }
    });
    this.trackProtectedRequest(request);
  }

  requestMessageReport(messageId: string): void {
    const message = this.messageActionTarget(messageId);
    if (!message || this.messageActionState().mode !== 'idle' || this.mockPage) {
      return;
    }
    this.messageActionState.set({
      messageId,
      mode: 'confirmReport',
      draft: '',
      pending: null
    });
  }

  saveMessageForLater(messageId: string): void {
    const message = this.messageActionTarget(messageId);
    if (!message || this.messageActionState().mode !== 'idle' || this.messageActionState().pending || this.mockPage) {
      return;
    }

    const generation = this.requestGeneration;
    const conversationId = this.pageState().conversation.id;
    this.messageActionState.set({
      messageId,
      mode: 'idle',
      draft: '',
      pending: 'saveForLater'
    });
    const request = this.api.saveMessageFollowUp(messageId).subscribe({
      next: (response) => {
        if (!this.isCurrentRequest(generation, conversationId)) {
          return;
        }
        if (nonEmptyString(response.messageId) !== messageId || response.isSaved !== true) {
          this.setMessageActionFeedback('The saved-message response was invalid. Refresh and try again.', true);
          return;
        }
        this.setMessageActionFeedback('Message saved for later.');
      },
      error: (error: { status?: number; httpStatus?: number }) => {
        if (!this.isCurrentRequest(generation, conversationId)) {
          return;
        }
        if (isProtectedLoadFailure(httpStatus(error))) {
          this.manualRefresh();
          return;
        }
        this.setMessageActionFeedback('The message could not be saved. Try again.', true);
      }
    });
    this.trackProtectedRequest(request);
  }

  confirmMessageReport(messageId: string, reasonCode: string): void {
    const action = this.messageActionState();
    const message = this.messageActionTarget(messageId);
    if (
      action.messageId !== messageId ||
      action.mode !== 'confirmReport' ||
      action.pending ||
      !message ||
      !reasonCode ||
      this.mockPage
    ) {
      return;
    }

    const generation = this.requestGeneration;
    const conversationId = this.pageState().conversation.id;
    this.messageActionState.set({ ...action, pending: 'report', error: undefined });
    const request = this.api.reportMessage(messageId, reasonCode).subscribe({
      next: () => {
        if (!this.isCurrentRequest(generation, conversationId)) {
          return;
        }
        this.setMessageActionFeedback('Report request recorded.');
      },
      error: (error: { status?: number; httpStatus?: number }) => {
        if (this.isCurrentRequest(generation, conversationId)) {
          this.messageActionState.update((current) => current.messageId === messageId && current.mode === 'confirmReport'
            ? { ...current, pending: null, error: messageActionFailureMessage(httpStatus(error)) }
            : current);
        }
      }
    });
    this.trackProtectedRequest(request);
  }

  cancelMessageAction(): void {
    if (!this.messageActionState().pending) {
      this.messageActionState.set(EMPTY_MESSAGE_ACTION);
    }
  }

  clearDraftsForSessionBoundary(): void {
    this.draftStorage.clearAllDrafts();
    this.pageState.update((page) => ({ ...page, draft: '' }));
  }

  private loadConversationList(
    routeKind: MessagingRouteKind,
    listOnly: boolean,
    generation: number,
    view: MessagingInboxView = this.inboxState().view,
    preserveRowsOnError = false,
  ): Promise<void> {
    return new Promise<void>((resolve) => {
      const request = this.api.listConversations(view).subscribe({
      next: (response) => {
        if (generation !== this.requestGeneration) {
          return;
        }
        const projection = mapInboxProjection(response);
        if (projection && projection.view !== view) {
          this.failInboxLoad(preserveRowsOnError);
          return;
        }
        const conversations = (response.items ?? []).map((conversation) =>
          mapConversationListItem(conversation)
        );
        this.pageState.update((page) => ({
          ...page,
          routeKind,
          conversations,
          status: listOnly ? 'empty' : page.status,
          title: listOnly ? 'Messages' : page.title,
          inlineError: undefined
        }));
        this.inboxState.set(projection
          ? { ...projection, status: 'ready' }
          : {
              view,
              counts: EMPTY_INBOX_COUNTS,
              status: 'unavailable',
              error: 'Conversation categories are unavailable until the server returns authoritative counts.'
            });
      },
      error: (error: { status?: number }) => {
        if (generation !== this.requestGeneration) {
          return;
        }
        if (preserveRowsOnError) {
          this.failInboxLoad(true);
        } else {
          this.pageState.update((page) => ({
            ...page,
            conversations: [],
            status: listOnly
              ? error.status === 401 || error.status === 403
                ? 'permissionDenied'
                : 'manualRefreshError'
              : page.status,
            inlineError: 'Conversation list API request failed.'
          }));
          this.inboxState.update((inbox) => ({
            ...inbox,
            status: 'error',
            requestedView: undefined,
            laterPendingConversationId: undefined,
            error: 'Conversation categories could not be loaded.'
          }));
        }
        },
        complete: resolve,
      });
      request.add(resolve);
      this.trackProtectedRequest(request);
    });
  }

  private failInboxLoad(preserveRows: boolean): void {
    this.inboxState.update((inbox) => ({
      ...inbox,
      status: 'error',
      requestedView: undefined,
      laterPendingConversationId: undefined,
      error: 'Conversation categories could not be loaded. Try again.'
    }));
    if (!preserveRows) {
      this.pageState.update((page) => ({ ...page, conversations: [] }));
    }
  }

  private failLaterMutation(conversationId: string): void {
    const inbox = this.inboxState();
    if (inbox.laterPendingConversationId !== conversationId) {
      return;
    }

    // Participant-state failures can represent access revocation through the
    // legacy 400 boundary. Clear every protected row before revalidating the
    // selected inbox projection instead of retaining stale metadata.
    const routeKind = this.pageState().routeKind;
    const generation = this.beginRequestGeneration();
    this.pageState.set(emptyMessagingPage(routeKind, 'loading'));
    this.inboxState.set({
      ...inbox,
      status: 'loading',
      requestedView: inbox.view,
      laterPendingConversationId: undefined,
      error: 'The Later state could not be changed. Refresh and try again.'
    });
    void this.loadConversationList(routeKind, true, generation, inbox.view);
  }

  private loadThread(
    rootMessageId: string,
    triggerElementId: string,
    anchorReplyMessageId?: string
  ): void {
    const page = this.pageState();
    const generation = ++this.threadRequestGeneration;
    this.threadAnchorReplyMessageId = anchorReplyMessageId ?? null;
    const refreshGeneration = (this.threadRefreshGenerations.get(rootMessageId) ?? 0) + 1;
    this.threadRefreshGenerations.set(rootMessageId, refreshGeneration);
    this.threadState.set({
      ...EMPTY_THREAD,
      status: 'loading',
      rootMessageId,
      triggerElementId
    });
    const request = this.api.getMessageThread(rootMessageId, anchorReplyMessageId).subscribe({
      next: (response) => {
        if (
          generation !== this.threadRequestGeneration ||
          this.threadRefreshGenerations.get(rootMessageId) !== refreshGeneration ||
          this.pageState().conversation.id !== page.conversation.id
        ) {
          return;
        }
        const mapped = mapMessageThread(response, this.currentUserId(), triggerElementId);
        if (
          mapped?.rootMessageId !== rootMessageId ||
          nonEmptyString(response.rootMessage?.conversationId) !== page.conversation.id ||
          (anchorReplyMessageId &&
            !mapped.replies.some((reply) =>
              reply.id === anchorReplyMessageId && !reply.isDeleted))
        ) {
          this.failThreadLoad(rootMessageId, triggerElementId, undefined);
          return;
        }
        this.threadState.set(anchorReplyMessageId
          ? { ...mapped, anchorReplyMessageId }
          : mapped);
        this.updateRootThreadSummary(rootMessageId, mapped.summary);
      },
      error: (error: { status?: number }) => {
        if (
          generation === this.threadRequestGeneration &&
          this.threadRefreshGenerations.get(rootMessageId) === refreshGeneration
        ) {
          this.failThreadLoad(rootMessageId, triggerElementId, error.status);
        }
      }
    });
    this.trackProtectedRequest(request);
  }

  private refreshThreadProjection(rootMessageId: string, preserveComposerError = false): void {
    if (this.mockPage || !this.pageState().conversation.id) {
      return;
    }
    const pageConversationId = this.pageState().conversation.id;
    const anchorReplyMessageId = this.threadState().rootMessageId === rootMessageId
      ? this.threadAnchorReplyMessageId ?? undefined
      : undefined;
    const refreshGeneration = (this.threadRefreshGenerations.get(rootMessageId) ?? 0) + 1;
    this.threadRefreshGenerations.set(rootMessageId, refreshGeneration);
    const request = this.api.getMessageThread(rootMessageId, anchorReplyMessageId).subscribe({
      next: (response) => {
        if (
          this.threadRefreshGenerations.get(rootMessageId) !== refreshGeneration ||
          this.pageState().conversation.id !== pageConversationId
        ) {
          return;
        }
        const active = this.threadState();
        const timelineRootIsDeleted = this.pageState().messages.some((message) =>
          message.id === rootMessageId && message.isDeleted
        );
        const mapped = mapMessageThread(response, this.currentUserId(), active.rootMessageId === rootMessageId
          ? active.triggerElementId
          : undefined);
        if (
          mapped?.rootMessageId !== rootMessageId ||
          nonEmptyString(response.rootMessage?.conversationId) !== pageConversationId ||
          (anchorReplyMessageId &&
            !mapped.replies.some((reply) =>
              reply.id === anchorReplyMessageId && !reply.isDeleted))
        ) {
          if (timelineRootIsDeleted) {
            this.removeTimelineMessage(rootMessageId);
          }
          if (active.rootMessageId === rootMessageId) {
            this.failThreadLoad(rootMessageId, active.triggerElementId, undefined);
          } else {
            this.updateRootThreadSummary(rootMessageId, undefined);
          }
          return;
        }
        if (timelineRootIsDeleted && mapped.summary.replyCount === 0) {
          this.removeTimelineMessage(rootMessageId);
        } else {
          this.updateRootThreadSummary(rootMessageId, mapped.summary);
        }
        if (active.rootMessageId === rootMessageId && active.status !== 'closed') {
          this.threadState.set({
            ...mapped,
            anchorReplyMessageId,
            rootMessage: timelineRootIsDeleted && mapped.rootMessage
              ? deletedMessageTombstone(mapped.rootMessage)
              : mapped.rootMessage,
            draft: active.draft,
            sending: active.sending,
            pendingClientRequestId: active.pendingClientRequestId,
            error: preserveComposerError || active.sending ? active.error : undefined
          });
        }
      },
      error: (error: { status?: number }) => {
        if (
          this.threadRefreshGenerations.get(rootMessageId) !== refreshGeneration ||
          this.pageState().conversation.id !== pageConversationId
        ) {
          return;
        }
        const active = this.threadState();
        const timelineRootIsDeleted = this.pageState().messages.some((message) =>
          message.id === rootMessageId && message.isDeleted
        );
        if (timelineRootIsDeleted && isProtectedLoadFailure(error.status)) {
          this.removeTimelineMessage(rootMessageId);
        }
        if (active.rootMessageId === rootMessageId) {
          this.failThreadLoad(rootMessageId, active.triggerElementId, error.status);
        } else {
          this.updateRootThreadSummary(rootMessageId, undefined);
        }
      }
    });
    this.trackProtectedRequest(request);
  }

  private failThreadLoad(
    rootMessageId: string,
    triggerElementId: string | undefined,
    status: number | undefined
  ): void {
    this.threadRequestGeneration++;
    this.threadAnchorReplyMessageId = null;
    this.updateRootThreadSummary(rootMessageId, undefined);
    this.threadState.set({
      ...EMPTY_THREAD,
      status: isProtectedLoadFailure(status) ? 'permissionDenied' : 'error',
      rootMessageId,
      triggerElementId,
      error: isProtectedLoadFailure(status)
        ? 'This thread is no longer available.'
        : 'Thread data could not be loaded.'
    });
  }

  private updateRootThreadSummary(
    rootMessageId: string,
    summary: MessagingMessageViewModel['thread']
  ): void {
    this.pageState.update((page) => ({
      ...page,
      messages: page.messages.map((message) => message.id === rootMessageId
        ? { ...message, thread: summary }
        : message)
    }));
  }

  private reconcileDeletedTimelineMessage(messageId: string, version?: number): void {
    const page = this.pageState();
    const target = page.messages.find((message) => message.id === messageId);
    const activeThread = this.threadState();
    const hasPinnedActiveRoot = activeThread.rootMessageId === messageId && !!activeThread.rootMessage;
    if (target?.threadRootMessageId || (!target && !hasPinnedActiveRoot)) {
      return;
    }

    const pageConversationId = page.conversation.id;
    const refreshGeneration = (this.threadRefreshGenerations.get(messageId) ?? 0) + 1;
    this.threadRefreshGenerations.set(messageId, refreshGeneration);
    this.pageState.update((current) => ({
      ...current,
      messages: current.messages.map((message) => message.id === messageId
        ? deletedMessageTombstone(message, version)
        : message)
    }));
    this.threadState.update((thread) => thread.rootMessageId === messageId && thread.rootMessage
      ? {
          ...thread,
          rootMessage: deletedMessageTombstone(thread.rootMessage, version),
          sending: false
        }
      : thread);

    if (this.mockPage || !pageConversationId) {
      if ((target?.thread?.replyCount ?? 0) === 0) {
        this.removeTimelineMessage(messageId);
      }
      return;
    }

    const anchorReplyMessageId = activeThread.rootMessageId === messageId
      ? this.threadAnchorReplyMessageId ?? undefined
      : undefined;
    const request = this.api.getMessageThread(messageId, anchorReplyMessageId).subscribe({
      next: (response) => {
        if (
          this.threadRefreshGenerations.get(messageId) !== refreshGeneration ||
          this.pageState().conversation.id !== pageConversationId
        ) {
          return;
        }
        const active = this.threadState();
        const mapped = mapMessageThread(response, this.currentUserId(), active.rootMessageId === messageId
          ? active.triggerElementId
          : undefined);
        if (
          mapped?.rootMessageId !== messageId ||
          nonEmptyString(response.rootMessage?.conversationId) !== pageConversationId ||
          !mapped.rootMessage ||
          (anchorReplyMessageId &&
            !mapped.replies.some((reply) =>
              reply.id === anchorReplyMessageId && !reply.isDeleted))
        ) {
          this.removeTimelineMessage(messageId);
          if (active.rootMessageId === messageId) {
            this.failThreadLoad(messageId, active.triggerElementId, undefined);
          }
          return;
        }

        const authoritativeRoot = deletedMessageTombstone(
          mapped.rootMessage,
          maximumMessageVersion(mapped.rootMessage.version, version)
        );
        const deletedProjection = {
          ...mapped,
          anchorReplyMessageId,
          rootMessage: deletedMessageTombstone(
            authoritativeRoot,
            maximumMessageVersion(active.rootMessage?.version, authoritativeRoot.version)
          ),
          draft: active.rootMessageId === messageId ? active.draft : mapped.draft,
          sending: false,
          pendingClientRequestId: active.rootMessageId === messageId
            ? active.pendingClientRequestId
            : mapped.pendingClientRequestId
        };
        if (mapped.summary.replyCount > 0) {
          this.pageState.update((current) => ({
            ...current,
            messages: current.messages.map((message) => message.id === messageId
              ? {
                  ...authoritativeRoot,
                  readState: message.readState,
                  thread: mapped.summary,
                  version: maximumMessageVersion(message.version, authoritativeRoot.version)
                }
              : message)
          }));
        } else {
          this.removeTimelineMessage(messageId);
        }
        if (active.rootMessageId === messageId && active.status !== 'closed') {
          this.threadState.set(deletedProjection);
        }
      },
      error: (error: { status?: number; httpStatus?: number }) => {
        if (
          this.threadRefreshGenerations.get(messageId) !== refreshGeneration ||
          this.pageState().conversation.id !== pageConversationId
        ) {
          return;
        }
        if (isProtectedLoadFailure(httpStatus(error))) {
          const active = this.threadState();
          this.removeTimelineMessage(messageId);
          if (active.rootMessageId === messageId) {
            this.failThreadLoad(messageId, active.triggerElementId, httpStatus(error));
          }
          return;
        }
        // A transient failure cannot prove that the deleted root has no durable
        // replies. Retain only the neutral tombstone until catch-up or reload.
        this.pageState.update((current) => ({ ...current, realtimeDegraded: true }));
      }
    });
    this.trackProtectedRequest(request);
  }

  private removeTimelineMessage(messageId: string): void {
    this.pageState.update((page) => {
      const messages = page.messages.filter((message) => message.id !== messageId);
      return {
        ...page,
        messages,
        status: messages.length === 0 ? 'empty' : page.status
      };
    });
  }

  private canReplyToThread(page: MessagingPageViewModel, thread: MessagingThreadViewModel): boolean {
    return this.canPost(page) &&
      thread.rootMessage?.isDeleted !== true &&
      ((thread.summary?.replyCount ?? 0) > 0 || page.conversation.capabilities.includes('createThread'));
  }

  private canPost(page: MessagingPageViewModel): boolean {
    return (
      page.conversation.viewerIsParticipant &&
      !page.conversation.viewerWasRemoved &&
      page.conversation.capabilities.includes('postMessage')
    );
  }

  private messageActionTarget(messageId: string, ownMessageOnly = false): MessagingMessageViewModel | null {
    const message = this.pageState().messages.find((current) => current.id === messageId);
    if (
      message?.deliveryState !== 'confirmed' ||
      message.isDeleted ||
      (ownMessageOnly && !message.isOwnMessage)
    ) {
      return null;
    }
    return message;
  }

  private setMessageActionFeedback(message: string, focusTimeline = false): void {
    this.messageActionFeedbackId += 1;
    this.messageActionState.set({
      ...EMPTY_MESSAGE_ACTION,
      feedback: {
        id: this.messageActionFeedbackId,
        message,
        focusTimeline
      }
    });
  }

  private scopeFor(page: MessagingPageViewModel): MessagingDraftScope {
    return {
      tenantId: page.conversation.tenantId,
      userId: this.currentUserId(),
      workspaceId: page.conversation.workspaceId,
      conversationId: page.conversation.id
    };
  }

  private withStoredDraft(page: MessagingPageViewModel): MessagingPageViewModel {
    return { ...page, draft: this.draftStorage.readDraft(this.scopeFor(page)) || page.draft };
  }

  private withSortedMessages(page: MessagingPageViewModel): MessagingPageViewModel {
    return {
      ...page,
      messages: [...page.messages].sort((left, right) => (left.createdAt ?? '').localeCompare(right.createdAt ?? ''))
    };
  }

  private registerConversationRealtime(conversationId: string): void {
    this.releaseConversationRealtime();
    if (!this.flags.realtimeSignalREnabled()) {
      return;
    }
    this.realtimeSubscriptionCleanup = this.realtime.registerSubscription(MESSAGING_REALTIME_OWNER, { subscriptionType: 'conversation', resourceId: conversationId });
    this.realtimeCatchUpCleanup = this.realtime.registerCatchUp(MESSAGING_REALTIME_OWNER, () => this.catchUpConversation(conversationId));
  }

  private async catchUpConversation(conversationId: string): Promise<void> {
    if (this.loadedConversationId() !== conversationId) {
      return;
    }
    const currentGeneration = this.requestGeneration;
    // Routine transport catch-up must not abort an already-dispatched command.
    // Security and route boundaries still cancel requests synchronously.
    if (this.protectedRequests.size > 0) {
      await this.waitForProtectedRequests(currentGeneration, conversationId);
    }
    if (!this.isCurrentRequest(currentGeneration, conversationId)) {
      return;
    }
    const generation = this.beginRequestGeneration(),
      routeKind = this.pageState().routeKind;
    this.pageState.set(emptyMessagingPage(routeKind, 'loading'));
    return this.loadConversationData(
      conversationId,
      routeKind,
      generation,
      this.loadedExpectedWorkspaceId,
      false,
      this.loadedAnchorMessageId,
    );
  }

  private async waitForProtectedRequests(generation: number, conversationId: string): Promise<void> {
    if (!this.isCurrentRequest(generation, conversationId) || this.protectedRequests.size === 0) {
      return;
    }
    await Promise.all([...this.protectedRequests].map((request) =>
      new Promise<void>((resolve) => { request.add(resolve); })));
    await this.waitForProtectedRequests(generation, conversationId);
  }

  private catchUpConversationList(routeKind: MessagingRouteKind): Promise<void> | void {
    if (this.loadedConversationId() !== null) {
      return;
    }
    const generation = this.beginRequestGeneration();
    this.pageState.set(emptyMessagingPage(routeKind, 'loading'));
    const view = this.inboxState().view;
    this.inboxState.update((inbox) => ({ ...inbox, status: 'loading', requestedView: view, error: undefined }));
    return this.loadConversationList(routeKind, true, generation, view);
  }

  private applyRealtimeEvent(event: DurableRealtimeEvent): void {
    const page = this.pageState();
    if (!page.conversation.id) {
      return;
    }
    if (event.eventType === 'Messaging.ConversationUnreadChanged.v1') {
      const payload = event.payload as { conversationId?: unknown; userId?: unknown };
      if (payload.conversationId === page.conversation.id && payload.userId === this.currentUserId()) {
        this.pageState.update((current) => ({ ...current, realtimeDegraded: false }));
      }
      return;
    }
    const payload = event.payload as {
      conversationId?: unknown;
      message?: unknown;
      messageId?: unknown;
      messageVersion?: unknown;
      body?: unknown;
      updatedAt?: unknown;
      deletionMode?: unknown;
      threadRootMessageId?: unknown;
      requiresRefetch?: unknown;
    };
    if (payload.conversationId !== page.conversation.id && (payload.message as { conversationId?: unknown } | undefined)?.conversationId !== page.conversation.id) {
      return;
    }
    if (event.eventType === 'Messaging.ThreadChanged.v1') {
      const rootMessageId = nonEmptyString(payload.threadRootMessageId);
      if (rootMessageId) {
        // The event is intentionally metadata-only and carries no participant
        // names. Always reconcile from the authorized bounded HTTP projection;
        // per-root generations prevent late responses from replacing newer data.
        this.refreshThreadProjection(rootMessageId);
      }
      return;
    }
    if (event.eventType === 'Messaging.MessageCreated.v1' && payload.message && typeof payload.message === 'object') {
      const message = mapRealtimeMessage(payload.message as Record<string, unknown>, this.currentUserId());
      if (this.hasDeletedMessageIdentity(message)) {
        // Deletion is terminal even when an older create event arrives late or
        // reconciles through the same client-request identity.
        return;
      }
      if (message.threadRootMessageId) {
        // A reply belongs only to the thread timeline. HTTP reconciliation also
        // updates the root count and participant summary for out-of-order events.
        this.refreshThreadProjection(message.threadRootMessageId);
        return;
      }
      this.pageState.update((current) => ({ ...current, messages: reconcileMessage(current.messages, message), hasNewMessagesWhileReading: !message.isOwnMessage, realtimeDegraded: false }));
      return;
    }
    if (event.eventType === 'Messaging.MessageUpdated.v1' || event.eventType === 'Messaging.MessageDeleted.v1') {
      const messageId = typeof payload.messageId === 'string' ? payload.messageId : '';
      const version = typeof payload.messageVersion === 'number' ? payload.messageVersion : undefined;
      const threadRootMessageId = nonEmptyString(payload.threadRootMessageId);
      if (event.eventType === 'Messaging.MessageUpdated.v1') {
        const thread = this.threadState();
        const alreadyDeleted = this.pageState().messages.some((message) =>
          message.id === messageId && message.isDeleted) ||
          (thread.rootMessage?.id === messageId && thread.rootMessage.isDeleted) ||
          thread.replies.some((reply) => reply.id === messageId && reply.isDeleted);
        if (alreadyDeleted) {
          // Deletion is terminal. Late or corrupt update events must never
          // restore a body after an authoritative tombstone was observed.
          return;
        }
      }
      if (threadRootMessageId) {
        this.refreshThreadProjection(threadRootMessageId);
        return;
      }
      if (event.eventType === 'Messaging.MessageDeleted.v1') {
        this.reconcileDeletedTimelineMessage(messageId, version);
        if (this.messageActionState().messageId === messageId) {
          this.setMessageActionFeedback('Message was removed.', true);
        }
        return;
      }
      this.pageState.update((current) => ({
        ...current,
        messages: current.messages.map((message) => message.id === messageId && !message.isDeleted && (version === undefined || (message.version ?? 0) < version)
          ? {
              ...message,
              body: typeof payload.body === 'string' ? payload.body : message.body,
              editedAt: typeof payload.updatedAt === 'string' ? payload.updatedAt : message.editedAt,
              version
            }
          : message)
      }));
      if (this.threadState().rootMessageId === messageId) {
        this.refreshThreadProjection(messageId);
      }
    }
  }

  private currentUserId(): string {
    return this.authSession.currentUser()?.userId ?? '';
  }

  private restoreClosedThreadFocus(
    triggerElementId: string | undefined,
    closeGeneration: number,
    remainingAnimationFrames: number
  ): void {
    if (
      closeGeneration !== this.threadRequestGeneration ||
      this.threadState().status !== 'closed'
    ) {
      return;
    }

    const trigger = triggerElementId ? document.getElementById(triggerElementId) : null;
    if (this.tryFocusElement(trigger)) {
      return;
    }
    if (this.tryFocusElement(document.getElementById('message-timeline'))) {
      return;
    }
    if (remainingAnimationFrames <= 0 || typeof window.requestAnimationFrame !== 'function') {
      return;
    }

    window.requestAnimationFrame(() => this.restoreClosedThreadFocus(
      triggerElementId,
      closeGeneration,
      remainingAnimationFrames - 1
    ));
  }

  private tryFocusElement(element: HTMLElement | null): boolean {
    element?.focus();
    return !!element && document.activeElement === element;
  }

  private hasDeletedMessageIdentity(incoming: MessagingMessageViewModel): boolean {
    const thread = this.threadState();
    return [
      ...this.pageState().messages,
      ...(thread.rootMessage ? [thread.rootMessage] : []),
      ...thread.replies
    ].some((message) => message.isDeleted && sameMessageIdentity(message, incoming));
  }

  private currentUserDisplayName(): string {
    return this.authSession.currentUser()?.displayName || 'You';
  }

  private currentTenantId(): string {
    return this.authSession.currentTenant()?.tenantId ?? '';
  }

  private beginRequestGeneration(): number {
    this.requestGeneration++;
    this.threadRequestGeneration++;
    this.threadAnchorReplyMessageId = null;
    this.threadRefreshGenerations.clear();
    this.cancelProtectedRequests();
    this.messageActionState.set(EMPTY_MESSAGE_ACTION);
    this.threadState.set(EMPTY_THREAD);
    return this.requestGeneration;
  }

  private isCurrentRequest(generation: number, conversationId: string): boolean {
    return generation === this.requestGeneration && this.loadedConversationId() === conversationId;
  }

  private trackProtectedRequest(request: Subscription): void {
    this.protectedRequests.add(request);
    request.add(() => this.protectedRequests.delete(request));
  }

  private cancelProtectedRequests(): void {
    for (const request of [...this.protectedRequests]) {
      request.unsubscribe();
    }
    this.protectedRequests.clear();
  }

  private releaseConversationRealtime(): void {
    this.realtimeSubscriptionCleanup?.();
    this.realtimeCatchUpCleanup?.();
    this.realtimeSubscriptionCleanup = undefined;
    this.realtimeCatchUpCleanup = undefined;
  }

  private rejectConversationOutsideRouteWorkspace(
    generation: number,
    conversationId: string,
    routeKind: MessagingRouteKind,
  ): void {
    if (!this.isCurrentRequest(generation, conversationId)) {
      return;
    }
    this.beginRequestGeneration();
    this.releaseConversationRealtime();
    this.loadedConversationId.set(null);
    this.loadedExpectedWorkspaceId = null;
    this.loadedAnchorMessageId = null;
    this.pageState.set(
      emptyMessagingPage(
        routeKind,
        'permissionDenied',
        'Conversation is not available in this Workspace.',
      ),
    );
  }

  private rejectIncompleteConversationAggregate(
    generation: number,
    conversationId: string,
    routeKind: MessagingRouteKind,
    status: number | undefined,
  ): void {
    if (!this.isCurrentRequest(generation, conversationId)) {
      return;
    }

    // Conversation detail alone is not a safe projection: access can be
    // revoked between the detail and message reads (the backend may surface
    // that denial as 400). Discard every protected field unless the complete
    // aggregate loaded successfully.
    this.beginRequestGeneration();
    this.releaseConversationRealtime();
    const accessDenied = status === 400 || status === 401 || status === 403 || status === 404;
    this.pageState.set(
      emptyMessagingPage(
        routeKind,
        accessDenied ? 'permissionDenied' : 'manualRefreshError',
        accessDenied
          ? 'Authentication or conversation permission is required.'
          : 'Message API request failed.',
      ),
    );
  }

  private clearProtectedState(reason: ProtectedStateClearReason): void {
    const preserveRouteIntent = reason === 'authorization';
    const routeKind = this.pageState().routeKind;
    this.beginRequestGeneration();
    if (!preserveRouteIntent) {
      this.releaseConversationRealtime();
      this.loadedConversationId.set(null);
      this.loadedExpectedWorkspaceId = null;
      this.loadedAnchorMessageId = null;
      this.navigationState.clearForWorkspaceBoundary();
    }
    if (reason === 'session' || reason === 'tenant') {
      this.clearDraftsForSessionBoundary();
    }
    this.pageState.set(emptyMessagingPage(
      routeKind,
      'loading',
      'Waiting for an active Workspace selection.',
    ));
    this.inboxState.set(emptyMessagingInbox());
  }
}

function emptyMessagingInbox(): MessagingInboxViewModel {
  return {
    view: 'All',
    counts: EMPTY_INBOX_COUNTS,
    status: 'loading'
  };
}

function mapInboxProjection(
  response: ConversationInboxResponseDto
): Omit<MessagingInboxViewModel, 'status'> | null {
  if (!isInboxView(response.view) || !response.counts) {
    return null;
  }
  const all = nonNegativeInteger(response.counts.all);
  const unread = nonNegativeInteger(response.counts.unread);
  const mentions = nonNegativeInteger(response.counts.mentions);
  const later = nonNegativeInteger(response.counts.later);
  if (all === null || unread === null || mentions === null || later === null) {
    return null;
  }
  return {
    view: response.view,
    counts: { all, unread, mentions, later }
  };
}

function isMatchingLaterState(
  response: ParticipantStateDto,
  conversationId: string,
  isLater: boolean
): boolean {
  return nonEmptyString(response.conversationId) === conversationId && response.isLater === isLater;
}

function isInboxView(value: unknown): value is MessagingInboxView {
  return value === 'All' || value === 'Unread' || value === 'Mentions' || value === 'Later';
}

function nonNegativeInteger(value: unknown): number | null {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null;
}

function emptyMessagingPage(
  routeKind: MessagingRouteKind,
  status: MessagingPageStatus,
  inlineError?: string
): MessagingPageViewModel {
  return {
    routeKind,
    status,
    title: routeKind === 'dm' ? 'Direct message' : 'Conversation',
    conversation: {
      id: '',
      kind: routeKind,
      tenantId: '',
      title: routeKind === 'dm' ? 'Direct message' : 'Conversation',
      subtitle: 'Live API data',
      viewerIsParticipant: false,
      viewerWasRemoved: false,
      capabilities: [],
      composerDisabledReason: 'Conversation API data is not loaded.',
      mentionCandidates: [],
      attachment: {
        mode: 'disabled',
        label: 'Attachments are disabled for MVP0 messaging.'
      }
    },
    conversations: [],
    messages: [],
    draft: '',
    sending: false,
    sendState: { status: 'idle' },
    hasNewMessagesWhileReading: false,
    inlineError,
    readCursorBehavior: 'conversationOpenFallback',
    pagingWindow: {
      visibleMessageIds: [],
      preloadBefore: 0,
      preloadAfter: 0
    }
  };
}

function nonEmptyString(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

function sendFailureMessage(status: number | undefined): string {
  return status === 401 || status === 403
    ? 'Authentication or posting permission is required.'
    : 'Message API request failed.';
}

function messageActionFailureMessage(status: number | undefined): string {
  if (status === 401) {
    return 'Your session has ended. Sign in again before trying this message action.';
  }
  if (status === 400 || status === 403 || status === 404) {
    return 'This message action is no longer available. Refresh the conversation and try again.';
  }
  return 'The message action could not be completed. Try again.';
}

function httpStatus(error: { readonly status?: number; readonly httpStatus?: number }): number | undefined {
  return error.httpStatus ?? error.status;
}

function failureCode(status: number | undefined): MessageFailureCode {
  if (status === 401) {
    return 'sessionExpired';
  }
  if (status === 403) {
    return 'permissionDenied';
  }
  return status && status >= 400 && status < 500 ? 'validation' : 'network';
}

function createClientRequestId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `00000000-0000-4000-8000-${Date.now().toString(16).padStart(12, '0').slice(-12)}`;
}

function deletedMessageTombstone(
  message: MessagingMessageViewModel,
  version?: number
): MessagingMessageViewModel {
  return {
    ...message,
    body: '',
    isDeleted: true,
    editedAt: undefined,
    mentionedUserIds: undefined,
    version: version ?? message.version
  };
}

function maximumMessageVersion(...versions: readonly (number | undefined)[]): number | undefined {
  const knownVersions = versions.filter((version): version is number => version !== undefined);
  return knownVersions.length > 0 ? Math.max(...knownVersions) : undefined;
}

function reconcileMessage(
  messages: readonly MessagingMessageViewModel[],
  incoming: MessagingMessageViewModel
): readonly MessagingMessageViewModel[] {
  const matchingIndex = messages.findIndex((message) => sameMessageIdentity(message, incoming));
  if (matchingIndex < 0) {
    return [...messages, incoming];
  }
  return messages.map((message, index) => index === matchingIndex ? incoming : message);
}

function sameMessageIdentity(
  existing: MessagingMessageViewModel,
  incoming: MessagingMessageViewModel
): boolean {
  return existing.id === incoming.id ||
    (!!incoming.clientRequestId &&
      existing.clientRequestId === incoming.clientRequestId &&
      !!incoming.authorUserId &&
      existing.authorUserId === incoming.authorUserId);
}

function mapRealtimeMessage(value: Record<string, unknown>, currentUserId: string): MessagingMessageViewModel {
  const sender = value['sender'] as Record<string, unknown> | undefined;
  const id = typeof value['id'] === 'string' ? value['id'] : `event-${Date.now()}`;
  const authorUserId = typeof sender?.['userId'] === 'string' ? sender['userId'] : '';
  return {
    id,
    clientRequestId: typeof value['clientRequestId'] === 'string' ? value['clientRequestId'] : undefined,
    authorUserId: authorUserId || undefined,
    authorLabel: typeof sender?.['displayName'] === 'string' ? sender['displayName'] : 'Unknown user',
    authorRoleLabel: 'member',
    isOwnMessage: authorUserId !== '' && authorUserId === currentUserId,
    body: typeof value['body'] === 'string' ? value['body'] : '',
    isDeleted: false,
    createdAt: typeof value['createdAt'] === 'string' ? value['createdAt'] : undefined,
    version: typeof value['version'] === 'number' ? value['version'] : undefined,
    sentAtLabel: typeof value['createdAt'] === 'string' ? new Date(value['createdAt']).toLocaleString() : '',
    deliveryState: 'confirmed',
    retryAllowed: false,
    threadRootMessageId: nonEmptyString(value['threadRootMessageId']) ?? undefined
  };
}

function isProtectedLoadFailure(status: number | undefined): boolean {
  return status === 400 || status === 401 || status === 403 || status === 404;
}

function isExplicitThreadAccessFailure(status: number | undefined): boolean {
  return status === 401 || status === 403 || status === 404;
}
