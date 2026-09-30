import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject, of, Subject } from 'rxjs';

import {
  COGLATAS_AUTH_SESSION_MOCK,
  DEFAULT_AUTH_SESSION
} from '../../core/auth/auth-session.facade';
import { RealtimeFacade } from '../../core/realtime/realtime.facade';
import { DurableRealtimeEvent } from '../../core/realtime/realtime.models';
import { FrontendFeatureFlagsService } from '../../core/feature-flags/frontend-feature-flags.service';
import { ActiveWorkspaceFacade } from '../../core/workspace/active-workspace.facade';
import { ChannelMessagingPageComponent } from './channel-messaging-page/channel-messaging-page.component';
import { DraftStorageService } from './draft-storage.service';
import { mapMessage } from './messaging.mapper';
import { COGLATAS_MESSAGING_PAGE_MOCK, MessagingFacade } from './messaging.facade';
import { MESSAGING_PAGE_SCENARIOS } from './messaging.mock';
import { MessagesPageComponent } from './messages-page/messages-page.component';

const currentUserId = DEFAULT_AUTH_SESSION.currentUser?.userId ?? 'mock-user-a';

async function configureHttpTest(
  imports: any[],
  conversationId = 'conversation-a',
  workspaceId: string | null = 'workspace-a',
): Promise<HttpTestingController> {
  const routeParams: Record<string, string> = { conversationId };
  if (workspaceId) {
    routeParams['workspaceId'] = workspaceId;
  }
  await TestBed.configureTestingModule({
    imports,
    providers: [
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      {
        provide: COGLATAS_AUTH_SESSION_MOCK,
        useValue: DEFAULT_AUTH_SESSION
      },
      {
        provide: ActivatedRoute,
        useValue: {
          paramMap: of(convertToParamMap(routeParams)),
          queryParamMap: of(convertToParamMap({}))
        }
      }
    ]
  }).compileComponents();

  return TestBed.inject(HttpTestingController);
}

function flushConversationOpen(
  httpMock: HttpTestingController,
  conversationId = 'conversation-a',
  includeOwnMessage = false,
  canCreateThread = false,
): void {
  httpMock.expectOne('/api/conversations').flush({
    items: [
      {
        id: conversationId,
        workspaceId: 'workspace-a',
        type: 'ProjectChannel',
        title: 'General',
        unreadCount: 0,
        createdAt: '2026-07-09T00:00:00Z'
      }
    ]
  });

  httpMock.expectOne(`/api/conversations/${conversationId}`).flush({
    id: conversationId,
    workspaceId: 'workspace-a',
    type: 'ProjectChannel',
    title: 'General',
    isLocked: false,
    isArchived: false,
    members: [
      {
        userId: currentUserId,
        displayName: 'Mock User A',
        canRead: true,
        canPost: true,
        canCreateThread,
        removedAt: null,
        leftAt: null
      }
    ],
    createdAt: '2026-07-09T00:00:00Z'
  });

  httpMock.expectOne(`/api/conversations/${conversationId}/messages`).flush({
    items: [
      {
        id: 'message-a',
        workspaceId: 'workspace-a',
        conversationId,
        authorUserId: 'other-user',
        authorDisplayName: 'Other User',
        body: 'Existing backend message',
        attachments: [],
        createdAt: '2026-07-09T01:00:00Z',
        isDeleted: false
      },
      ...(includeOwnMessage ? [{
        id: 'message-own',
        workspaceId: 'workspace-a',
        conversationId,
        authorUserId: currentUserId,
        authorDisplayName: 'Mock User A',
        body: 'My editable backend message',
        attachments: [],
        createdAt: '2026-07-09T01:01:00Z',
        isDeleted: false,
        version: 1
      }] : [])
    ]
  });
}

async function configureConversationCatchUp(): Promise<{
  catchUp: () => Promise<void>;
  clear: (reason: 'authorization' | 'workspace') => void;
  facade: MessagingFacade;
  httpMock: HttpTestingController;
}> {
  const events = new Subject<DurableRealtimeEvent>();
  let catchUp: (() => Promise<void> | void) | null = null,
    clear: ((reason: 'authorization' | 'workspace') => void) | null = null;
  await TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: COGLATAS_AUTH_SESSION_MOCK, useValue: DEFAULT_AUTH_SESSION },
      {
        provide: FrontendFeatureFlagsService,
        useValue: {
          optimisticMessagingEnabled: (): boolean => true,
          realtimeSignalREnabled: (): boolean => true,
        },
      },
      {
        provide: RealtimeFacade,
        useValue: {
          durableEvents$: events.asObservable(),
          registerCatchUp: (_owner: string, callback: () => Promise<void> | void): (() => void) => {
            catchUp = callback;
            return (): void => { catchUp = null; };
          },
          registerProtectedStateClearer: (
            _owner: string,
            callback: (reason: 'authorization' | 'workspace') => void,
          ): (() => void) => {
            clear = callback;
            return (): void => { clear = null; };
          },
          registerSubscription: (): (() => void) => vi.fn<() => void>(),
        },
      },
    ],
  }).compileComponents();
  return {
    catchUp: async (): Promise<void> => { await catchUp?.(); },
    clear: (reason): void => { clear?.(reason); },
    facade: TestBed.inject(MessagingFacade),
    httpMock: TestBed.inject(HttpTestingController),
  };
}

async function configureRealtimeActionFacade(events: Subject<DurableRealtimeEvent>): Promise<HttpTestingController> {
  await TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: COGLATAS_AUTH_SESSION_MOCK, useValue: DEFAULT_AUTH_SESSION },
      {
        provide: FrontendFeatureFlagsService,
        useValue: {
          realtimeSignalREnabled: () => false,
          optimisticMessagingEnabled: () => true,
        },
      },
      {
        provide: RealtimeFacade,
        useValue: {
          connectionState: signal('Connected'),
          durableEvents$: events.asObservable(),
          registerProtectedStateClearer: () => () => undefined,
        },
      },
    ],
  }).compileComponents();
  return TestBed.inject(HttpTestingController);
}

async function configureRealtimeActionPage(events: Subject<DurableRealtimeEvent>): Promise<HttpTestingController> {
  await TestBed.configureTestingModule({
    imports: [ChannelMessagingPageComponent],
    providers: [
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: COGLATAS_AUTH_SESSION_MOCK, useValue: DEFAULT_AUTH_SESSION },
      {
        provide: ActivatedRoute,
        useValue: {
          paramMap: of(convertToParamMap({ workspaceId: 'workspace-a', conversationId: 'conversation-a' }))
        }
      },
      {
        provide: FrontendFeatureFlagsService,
        useValue: {
          realtimeSignalREnabled: () => false,
          optimisticMessagingEnabled: () => true,
        },
      },
      {
        provide: RealtimeFacade,
        useValue: {
          connectionState: signal('Connected'),
          durableEvents$: events.asObservable(),
          registerProtectedStateClearer: () => () => undefined,
        },
      },
    ],
  }).compileComponents();
  return TestBed.inject(HttpTestingController);
}

function messageRealtimeEvent(
  eventType: 'Messaging.MessageUpdated.v1' | 'Messaging.MessageDeleted.v1',
  payload: Record<string, unknown>,
): DurableRealtimeEvent {
  return {
    eventId: `event-${eventType}`,
    eventType,
    payloadSchemaVersion: 1,
    occurredAt: '2026-07-09T01:03:00Z',
    tenantId: 'tenant-a',
    aggregateType: 'Message',
    aggregateId: 'message-own',
    aggregateVersion: typeof payload['messageVersion'] === 'number' ? payload['messageVersion'] : null,
    actor: { actorType: 'User', actorId: 'other-user' },
    correlationId: null,
    causationId: null,
    payload,
  };
}

describe('Messaging MVP0 backend wiring', () => {
  afterEach(() => {
    sessionStorage.clear();
    TestBed.inject(HttpTestingController, null)?.verify();
    TestBed.resetTestingModule();
  });

  it('renders a reachable conversation list from the backend', async () => {
    const httpMock = await configureHttpTest([MessagesPageComponent]);
    const fixture = TestBed.createComponent(MessagesPageComponent);

    const request = httpMock.expectOne('/api/conversations');
    expect(request.request.withCredentials).toBe(true);
    request.flush({
      items: [
        {
          id: 'conversation-a',
          workspaceId: 'workspace-a',
          type: 'ProjectChannel',
          title: 'General',
          lastMessage: { body: 'Last channel message' },
          unreadCount: 2,
          createdAt: '2026-07-09T00:00:00Z'
        },
        {
          id: 'dm-a',
          workspaceId: 'workspace-a',
          type: 'DirectMessage',
          title: null,
          unreadCount: 0,
          createdAt: '2026-07-09T00:00:00Z'
        }
      ]
    });

    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelectorAll('[data-testid="conversation-list-item"]').length).toBe(2);
    expect(root.querySelector('a[href="/workspaces/workspace-a/channels/conversation-a"]')).not.toBeNull();
    expect(root.querySelector('a[href="/dm/dm-a"]')).not.toBeNull();
    expect(root.textContent).toContain('General');
  });

  it('renders an empty state with a new message button when no conversations exist', async () => {
    const httpMock = await configureHttpTest([MessagesPageComponent]);
    const fixture = TestBed.createComponent(MessagesPageComponent);

    httpMock.expectOne('/api/conversations').flush({ items: [] });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="messages-list-empty"]')?.textContent).toContain('まだ会話はありません');
    expect(root.querySelector('[data-testid="new-message-button"]')).not.toBeNull();
  });

  it('opens the new message dialog and creates a direct conversation from a selected recipient', async () => {
    const httpMock = await configureHttpTest([MessagesPageComponent]);
    const router = TestBed.inject(Router);
    const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    const fixture = TestBed.createComponent(MessagesPageComponent);

    httpMock.expectOne('/api/conversations').flush({ items: [] });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="new-message-button"]')?.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="new-message-dialog"]')).not.toBeNull();

    const input = root.querySelector<HTMLInputElement>('[data-testid="recipient-search"]');
    input!.value = 'Staff';
    input!.dispatchEvent(new Event('input'));

    const searchRequest = httpMock.expectOne('/api/conversations/recipients?query=Staff');
    expect(searchRequest.request.withCredentials).toBe(true);
    searchRequest.flush([{ userId: 'user-staff', displayName: 'Staff User' }]);
    fixture.detectChanges();

    root.querySelector<HTMLButtonElement>('[data-testid="recipient-option"]')?.click();
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="create-conversation-submit"]')?.click();

    const createRequest = httpMock.expectOne('/api/conversations/direct');
    expect(createRequest.request.withCredentials).toBe(true);
    expect(createRequest.request.body).toEqual({ recipientUserId: 'user-staff' });
    createRequest.flush({
      id: 'dm-created',
      workspaceId: 'workspace-a',
      type: 'DirectMessage',
      title: 'Staff User',
      isLocked: false,
      isArchived: false,
      members: [],
      createdAt: '2026-07-09T00:00:00Z'
    });

    expect(navigateSpy).toHaveBeenCalledWith('/dm/dm-created');
  });

  it('keeps the dialog open and shows an error when direct conversation creation fails', async () => {
    const httpMock = await configureHttpTest([MessagesPageComponent]);
    const router = TestBed.inject(Router);
    const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    const fixture = TestBed.createComponent(MessagesPageComponent);

    httpMock.expectOne('/api/conversations').flush({ items: [] });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    root.querySelector<HTMLButtonElement>('[data-testid="new-message-button"]')?.click();
    fixture.detectChanges();
    const input = root.querySelector<HTMLInputElement>('[data-testid="recipient-search"]');
    input!.value = 'Staff';
    input!.dispatchEvent(new Event('input'));
    httpMock.expectOne('/api/conversations/recipients?query=Staff').flush([
      { userId: 'user-staff', displayName: 'Staff User' }
    ]);
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="recipient-option"]')?.click();
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="create-conversation-submit"]')?.click();

    httpMock.expectOne('/api/conversations/direct').flush(
      { error: 'failed' },
      { status: 500, statusText: 'Server Error' }
    );
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="new-message-dialog"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="create-conversation-error"]')?.textContent).toContain(
      '会話を作成できませんでした'
    );
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('does not display conversation list API failure as an empty state', async () => {
    const httpMock = await configureHttpTest([MessagesPageComponent]);
    const fixture = TestBed.createComponent(MessagesPageComponent);

    httpMock.expectOne('/api/conversations').flush(
      { error: 'failed' },
      { status: 500, statusText: 'Server Error' }
    );
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="messages-list-failed"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="messages-list-empty"]')).toBeNull();
  });

  it('opens an existing conversation and renders backend messages', async () => {
    const httpMock = await configureHttpTest([ChannelMessagingPageComponent]);
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock);

    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="conversation-surface"]')).not.toBeNull();
    expect(root.textContent).toContain('General');
    expect(root.textContent).toContain('Existing backend message');
  });

  it('uses the current server-supported message action contracts and redacts a generic denial', async () => {
    const httpMock = await configureHttpTest([ChannelMessagingPageComponent]);
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock, 'conversation-a', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const more = root.querySelector<HTMLButtonElement>('[data-testid="message-more-actions-message-own"]');
    more!.click();
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="edit-message-message-own"]')!.click();
    fixture.detectChanges();

    const editInput = root.querySelector<HTMLTextAreaElement>('[data-testid="message-edit-input-message-own"]');
    editInput!.value = 'Updated through PATCH';
    editInput.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="save-message-edit-message-own"]')!.click();

    const editRequest = httpMock.expectOne('/api/messages/message-own');
    expect(editRequest.request.method).toBe('PATCH');
    expect(editRequest.request.withCredentials).toBe(true);
    expect(editRequest.request.body).toEqual({ body: 'Updated through PATCH' });
    editRequest.flush({
      id: 'message-own',
      workspaceId: 'workspace-a',
      conversationId: 'conversation-a',
      authorUserId: currentUserId,
      authorDisplayName: 'Mock User A',
      body: 'Updated through PATCH',
      createdAt: '2026-07-09T01:01:00Z',
      editedAt: '2026-07-09T01:02:00Z',
      isDeleted: false,
      version: 2
    });
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="message-edited-marker"]')?.textContent).toContain('Edited');
    expect(root.querySelector('[data-testid="message-action-status"]')?.textContent).toContain('Message updated.');

    root.querySelector<HTMLButtonElement>('[data-testid="message-more-actions-message-own"]')!.click();
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="report-message-message-own"]')!.click();
    fixture.detectChanges();
    const reportDialog = root.querySelector<HTMLElement>('[role="dialog"]');
    expect(reportDialog?.textContent).toContain('Report message');
    const reportConfirm = [...root.querySelectorAll<HTMLButtonElement>('button')]
      .find((button) => button.textContent?.includes('Record report request'));
    reportConfirm!.click();

    const deniedReport = httpMock.expectOne('/api/messages/message-own/report');
    expect(deniedReport.request.method).toBe('POST');
    expect(deniedReport.request.withCredentials).toBe(true);
    expect(deniedReport.request.body).toEqual({ reasonCode: 'reported' });
    deniedReport.flush(
      { error: 'SECRET_ACTION_DENIAL_DO_NOT_RENDER' },
      { status: 400, statusText: 'Bad Request' }
    );
    fixture.detectChanges();

    expect(root.textContent).not.toContain('SECRET_ACTION_DENIAL_DO_NOT_RENDER');
    expect(root.querySelector('[role="dialog"]')?.textContent)
      .toContain('This message action is no longer available.');

    reportConfirm!.click();
    const acceptedReport = httpMock.expectOne('/api/messages/message-own/report');
    acceptedReport.flush({ status: 'OK' });
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="message-action-status"]')?.textContent)
      .toContain('Report request recorded.');
  });

  it('does not delete before confirmation and removes the current list row after success', async () => {
    const httpMock = await configureHttpTest([ChannelMessagingPageComponent]);
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock, 'conversation-a', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="message-more-actions-message-own"]')!.click();
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="delete-message-message-own"]')!.click();
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')?.textContent).toContain('Delete message?');
    httpMock.expectNone('/api/messages/message-own');
    const deleteConfirm = [...root.querySelectorAll<HTMLButtonElement>('button')]
      .find((button) => button.textContent?.includes('Delete message'));
    deleteConfirm!.click();

    const deleteRequest = httpMock.expectOne('/api/messages/message-own');
    expect(deleteRequest.request.method).toBe('DELETE');
    expect(deleteRequest.request.withCredentials).toBe(true);
    deleteRequest.flush({ status: 'OK' });
    httpMock.expectOne('/api/messages/message-own/thread').flush(deletedZeroReplyThread('message-own'));
    fixture.detectChanges();

    expect(root.querySelector('#message-message-own')).toBeNull();
    expect(root.querySelector('[data-testid="message-action-status"]')?.textContent).toContain('Message deleted.');
  });

  it('keeps Reply, private Save, and More as direct actions and saves without changing read state', async () => {
    const httpMock = await configureHttpTest([ChannelMessagingPageComponent]);
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const actionGroup = root.querySelector<HTMLElement>('#message-message-a .message__actions')!;
    const reply = root.querySelector<HTMLButtonElement>('[data-testid="open-message-thread-message-a"]')!;
    const save = root.querySelector<HTMLButtonElement>('[data-testid="save-message-for-later-message-a"]')!;
    const more = root.querySelector<HTMLButtonElement>('[data-testid="message-more-actions-message-a"]')!;

    expect(reply.getAttribute('aria-label')).toBe('Reply in thread for message from Other User');
    expect(save.getAttribute('aria-label')).toBe('Save message from Other User for later');
    expect(more.getAttribute('aria-label')).toBe('More actions for message from Other User');
    expect(save.closest('.message__overflow')).toBeNull();
    expect(actionGroup.querySelector('[data-testid="message-action-overflow"]')).toBeNull();
    expect([...actionGroup.querySelectorAll(':scope > button, :scope > .message__overflow > button')]
      .map((button) => button.getAttribute('data-testid')))
      .toEqual([
        'open-message-thread-message-a',
        'save-message-for-later-message-a',
        'message-more-actions-message-a'
      ]);

    save.click();

    const saveRequest = httpMock.expectOne('/api/me/message-follow-ups/message-a');
    expect(saveRequest.request.method).toBe('PUT');
    expect(saveRequest.request.withCredentials).toBe(true);
    expect(saveRequest.request.body).toEqual({});
    saveRequest.flush({
      messageId: 'message-a',
      isSaved: true,
      savedAt: '2026-08-29T11:00:00Z'
    });
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="message-action-status"]')?.textContent)
      .toContain('Message saved for later.');
    httpMock.expectNone('/api/conversations/conversation-a/read');
  });

  it('does not replace an active edit with an action from another message', async () => {
    const httpMock = await configureHttpTest([ChannelMessagingPageComponent]);
    TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock, 'conversation-a', true);
    const facade = TestBed.inject(MessagingFacade);

    facade.beginMessageEdit('message-own');
    facade.requestMessageReport('message-a');
    facade.requestMessageDelete('message-own');

    expect(facade.messageAction()).toMatchObject({
      messageId: 'message-own',
      mode: 'editing',
      draft: 'My editable backend message',
      pending: null
    });
  });

  it('cancels in-flight message mutations and resets action state at a protected Workspace boundary', async () => {
    const httpMock = await configureHttpTest([ChannelMessagingPageComponent]);
    TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock, 'conversation-a', true);
    const facade = TestBed.inject(MessagingFacade);
    const realtime = TestBed.inject(RealtimeFacade);

    facade.beginMessageEdit('message-own');
    facade.updateMessageEditDraft('message-own', 'Boundary PATCH');
    facade.saveMessageEdit('message-own');
    const patch = httpMock.expectOne('/api/messages/message-own');
    realtime.clearForWorkspaceBoundary();
    expect(patch.cancelled).toBe(true);
    expect(facade.messageAction().mode).toBe('idle');

    facade.loadConversation('conversation-a', 'channel', 'workspace-a');
    flushConversationOpen(httpMock, 'conversation-a', true);
    facade.requestMessageDelete('message-own');
    facade.confirmMessageDelete('message-own');
    const deleteRequest = httpMock.expectOne('/api/messages/message-own');
    realtime.clearForWorkspaceBoundary();
    expect(deleteRequest.cancelled).toBe(true);
    expect(facade.messageAction().mode).toBe('idle');

    facade.loadConversation('conversation-a', 'channel', 'workspace-a');
    flushConversationOpen(httpMock, 'conversation-a', true);
    facade.requestMessageReport('message-a');
    facade.confirmMessageReport('message-a', 'reported');
    const reportRequest = httpMock.expectOne('/api/messages/message-a/report');
    realtime.clearForWorkspaceBoundary();
    expect(reportRequest.cancelled).toBe(true);
    expect(facade.messageAction().mode).toBe('idle');
  });

  it('does not revive a row when a late PATCH success follows realtime deletion', async () => {
    const events = new Subject<DurableRealtimeEvent>();
    const httpMock = await configureRealtimeActionFacade(events);
    const facade = TestBed.inject(MessagingFacade);
    facade.loadConversation('conversation-a', 'channel', 'workspace-a');
    flushConversationOpen(httpMock, 'conversation-a', true);

    facade.beginMessageEdit('message-own');
    facade.updateMessageEditDraft('message-own', 'Late PATCH body');
    facade.saveMessageEdit('message-own');
    const patch = httpMock.expectOne('/api/messages/message-own');

    events.next(messageRealtimeEvent('Messaging.MessageDeleted.v1', {
      conversationId: 'conversation-a',
      messageId: 'message-own',
      messageVersion: 3,
    }));
    const deletionRevalidation = httpMock.expectOne('/api/messages/message-own/thread');
    patch.flush({
      id: 'message-own',
      workspaceId: 'workspace-a',
      conversationId: 'conversation-a',
      authorUserId: currentUserId,
      authorDisplayName: 'Mock User A',
      body: 'Late PATCH body',
      createdAt: '2026-07-09T01:01:00Z',
      editedAt: '2026-07-09T01:02:00Z',
      isDeleted: false,
      version: 2,
    });
    deletionRevalidation.flush(deletedZeroReplyThread('message-own'));

    expect(facade.page().messages.find((message) => message.id === 'message-own')).toBeUndefined();
    expect(facade.messageAction().feedback).toMatchObject({ message: 'Message was removed.', focusTimeline: true });
  });

  it('preserves a newer realtime update when a PATCH response is older', async () => {
    const events = new Subject<DurableRealtimeEvent>();
    const httpMock = await configureRealtimeActionFacade(events);
    const facade = TestBed.inject(MessagingFacade);
    facade.loadConversation('conversation-a', 'channel', 'workspace-a');
    flushConversationOpen(httpMock, 'conversation-a', true);

    facade.beginMessageEdit('message-own');
    facade.updateMessageEditDraft('message-own', 'Late PATCH body');
    facade.saveMessageEdit('message-own');
    const patch = httpMock.expectOne('/api/messages/message-own');

    events.next(messageRealtimeEvent('Messaging.MessageUpdated.v1', {
      conversationId: 'conversation-a',
      messageId: 'message-own',
      messageVersion: 3,
      body: 'Newer realtime body',
      updatedAt: '2026-07-09T01:03:00Z',
    }));
    patch.flush({
      id: 'message-own',
      workspaceId: 'workspace-a',
      conversationId: 'conversation-a',
      authorUserId: currentUserId,
      authorDisplayName: 'Mock User A',
      body: 'Late PATCH body',
      createdAt: '2026-07-09T01:01:00Z',
      editedAt: '2026-07-09T01:02:00Z',
      isDeleted: false,
      version: 2,
    });

    expect(facade.page().messages.find((message) => message.id === 'message-own')).toMatchObject({
      body: 'Newer realtime body',
      editedAt: '2026-07-09T01:03:00Z',
      version: 3,
    });
    expect(facade.messageAction().feedback).toMatchObject({
      message: 'Message changed. Refresh the conversation before trying again.',
      focusTimeline: true,
    });
  });

  it('moves focus to the timeline when realtime deletion removes an active editor', async () => {
    const events = new Subject<DurableRealtimeEvent>();
    const httpMock = await configureRealtimeActionPage(events);
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock, 'conversation-a', true);
    const facade = TestBed.inject(MessagingFacade);
    facade.beginMessageEdit('message-own');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const editor = root.querySelector<HTMLTextAreaElement>('[data-testid="message-edit-input-message-own"]');
    editor!.focus();
    expect(document.activeElement).toBe(editor);

    events.next(messageRealtimeEvent('Messaging.MessageDeleted.v1', {
      conversationId: 'conversation-a',
      messageId: 'message-own',
      messageVersion: 3,
    }));
    httpMock.expectOne('/api/messages/message-own/thread').flush(deletedZeroReplyThread('message-own'));
    fixture.detectChanges();
    await Promise.resolve();

    expect(document.activeElement).toBe(root.querySelector('#message-timeline'));
  });

  it('keeps deletion focus in the thread, then falls back to the timeline when its trigger is removed', async () => {
    const events = new Subject<DurableRealtimeEvent>();
    const httpMock = await configureRealtimeActionPage(events);
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock, 'conversation-a', true, true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const trigger = root.querySelector<HTMLButtonElement>('[data-testid="open-message-thread-message-own"]')!;
    trigger.click();
    httpMock.expectOne('/api/messages/message-own/thread').flush(liveZeroReplyThread('message-own'));
    fixture.detectChanges();
    await Promise.resolve();

    const textarea = root.querySelector<HTMLTextAreaElement>('[data-testid="thread-reply-draft"]')!;
    textarea.focus();
    expect(document.activeElement).toBe(textarea);

    events.next(messageRealtimeEvent('Messaging.MessageDeleted.v1', {
      conversationId: 'conversation-a',
      messageId: 'message-own',
      messageVersion: 3,
    }));
    httpMock.expectOne('/api/messages/message-own/thread').flush(deletedZeroReplyThread('message-own'));
    fixture.detectChanges();
    await Promise.resolve();

    expect(root.querySelector('[data-testid="open-message-thread-message-own"]')).toBeNull();
    const back = root.querySelector<HTMLButtonElement>('[data-testid="thread-back"]')!;
    expect(document.activeElement).toBe(back);
    back.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    fixture.detectChanges();
    await Promise.resolve();

    expect(root.querySelector('[data-testid="thread-preview"]')).toBeNull();
    expect(document.activeElement).toBe(root.querySelector('#message-timeline'));
  });

  it('opens a legacy notification conversation route in the canonical active Workspace', async () => {
    const httpMock = await configureHttpTest(
      [ChannelMessagingPageComponent],
      'conversation-a',
      null,
    );
    TestBed.inject(ActiveWorkspaceFacade).setActiveWorkspace({
      id: 'workspace-a',
      label: 'Workspace A',
    });
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);

    flushConversationOpen(httpMock);
    fixture.detectChanges();

    expect(fixture.componentInstance.page().conversation).toMatchObject({
      id: 'conversation-a',
      workspaceId: 'workspace-a',
    });
    expect(fixture.componentInstance.page().messages).toHaveLength(1);
  });

  it('fails closed for a legacy conversation route without an active Workspace', async () => {
    const httpMock = await configureHttpTest(
      [ChannelMessagingPageComponent],
      'conversation-a',
      null,
    );
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);

    httpMock.expectNone('/api/conversations');
    httpMock.expectNone('/api/conversations/conversation-a');
    expect(fixture.componentInstance.page()).toMatchObject({
      status: 'permissionDenied',
      conversations: [],
      messages: [],
      conversation: { id: '' },
    });
  });

  it('rejects a canonical channel DTO from another Workspace before messages or realtime', async () => {
    const events = new Subject<DurableRealtimeEvent>();
    const registerSubscription = vi.fn(() => () => undefined);
    const registerCatchUp = vi.fn(() => () => undefined);
    await TestBed.configureTestingModule({
      imports: [ChannelMessagingPageComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: COGLATAS_AUTH_SESSION_MOCK, useValue: DEFAULT_AUTH_SESSION },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: of(convertToParamMap({
              workspaceId: 'workspace-a',
              conversationId: 'conversation-a',
            })),
          },
        },
        {
          provide: FrontendFeatureFlagsService,
          useValue: {
            realtimeSignalREnabled: () => true,
            optimisticMessagingEnabled: () => true,
          },
        },
        {
          provide: RealtimeFacade,
          useValue: {
            connectionState: signal('Connected'),
            durableEvents$: events.asObservable(),
            registerProtectedStateClearer: () => () => undefined,
            registerSubscription,
            registerCatchUp,
          },
        },
      ],
    }).compileComponents();
    const httpMock = TestBed.inject(HttpTestingController);
    TestBed.createComponent(ChannelMessagingPageComponent);

    httpMock.expectOne('/api/conversations').flush({ items: [] });
    httpMock.expectOne('/api/conversations/conversation-a').flush({
      id: 'conversation-a',
      workspaceId: 'workspace-b',
      type: 'ProjectChannel',
      title: 'Other Workspace channel',
      isLocked: false,
      isArchived: false,
      members: [],
      createdAt: '2026-07-09T00:00:00Z',
    });

    httpMock.expectNone('/api/conversations/conversation-a/messages');
    expect(registerSubscription).not.toHaveBeenCalled();
    expect(registerCatchUp).not.toHaveBeenCalled();
    expect(TestBed.inject(MessagingFacade).page()).toMatchObject({
      status: 'permissionDenied',
      conversations: [],
      messages: [],
      conversation: { id: '' },
    });
  });

  it('waits for NavigationEnd to commit a cross-Workspace channel scope before loading', async () => {
    const routeParams = new BehaviorSubject(convertToParamMap({
      workspaceId: 'workspace-a',
      conversationId: 'conversation-a',
    }));
    await TestBed.configureTestingModule({
      imports: [ChannelMessagingPageComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: COGLATAS_AUTH_SESSION_MOCK, useValue: DEFAULT_AUTH_SESSION },
        {
          provide: ActivatedRoute,
          useValue: { paramMap: routeParams.asObservable() },
        },
      ],
    }).compileComponents();
    const httpMock = TestBed.inject(HttpTestingController);
    const activeWorkspace = TestBed.inject(ActiveWorkspaceFacade);
    activeWorkspace.setActiveWorkspace({ id: 'workspace-b', label: 'Workspace B' });

    TestBed.createComponent(ChannelMessagingPageComponent);
    httpMock.expectNone('/api/conversations');
    httpMock.expectNone('/api/conversations/conversation-a');

    activeWorkspace.setActiveWorkspace({ id: 'workspace-a', label: 'Workspace A' });
    TestBed.flushEffects();
    flushConversationOpen(httpMock);
  });

  it('keeps the same-tenant conversation rail populated while a different detail route loads', async () => {
    const httpMock = await configureHttpTest([ChannelMessagingPageComponent]);
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock);
    const facade = TestBed.inject(MessagingFacade);

    expect(facade.page().conversations.map((conversation) => conversation.id)).toEqual(['conversation-a']);

    facade.loadConversation('conversation-b', 'channel', 'workspace-a');

    expect(facade.page().status).toBe('loading');
    expect(facade.page().conversations.map((conversation) => conversation.id)).toEqual(['conversation-a']);

    flushConversationOpen(httpMock, 'conversation-b');
    fixture.detectChanges();
  });

  it('clears protected conversation state and pending requests at a Workspace boundary while retaining the partitioned draft', async () => {
    const httpMock = await configureHttpTest([ChannelMessagingPageComponent]);
    TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock);
    const facade = TestBed.inject(MessagingFacade);
    const realtime = TestBed.inject(RealtimeFacade);
    const draftStorage = TestBed.inject(DraftStorageService);
    const pageBeforeBoundary = facade.page();
    const draftKey = draftStorage.keyFor({
      tenantId: pageBeforeBoundary.conversation.tenantId,
      userId: currentUserId,
      workspaceId: pageBeforeBoundary.conversation.workspaceId,
      conversationId: pageBeforeBoundary.conversation.id,
    });
    facade.setDraft('Workspace-partitioned draft');
    sessionStorage.setItem('coglatas.messaging.list-scroll-y.v1', '320');
    sessionStorage.setItem('coglatas.messaging.list-scroll-restore-pending.v1', '1');

    facade.loadConversation('conversation-b', 'channel', 'workspace-a');
    const pending = httpMock.match((request) =>
      request.url === '/api/conversations' || request.url === '/api/conversations/conversation-b'
    );
    expect(pending).toHaveLength(2);

    realtime.clearForWorkspaceBoundary();

    expect(pending.every((request) => request.cancelled)).toBe(true);
    expect(facade.page().conversation.id).toBe('');
    expect(facade.page().conversations).toEqual([]);
    expect(facade.page().messages).toEqual([]);
    expect(facade.page().draft).toBe('');
    expect(sessionStorage.getItem(draftKey)).toBe('Workspace-partitioned draft');
    expect(sessionStorage.getItem('coglatas.messaging.list-scroll-y.v1')).toBeNull();
    expect(sessionStorage.getItem('coglatas.messaging.list-scroll-restore-pending.v1')).toBeNull();
  });

  it('keeps realtime catch-up pending until the full authoritative conversation reload settles', async () => {
    const { catchUp, facade, httpMock } = await configureConversationCatchUp();
    facade.loadConversation('conversation-a', 'channel', 'workspace-a');
    flushConversationOpen(httpMock);
    expect(catchUp).not.toBeNull();

    const catchUpCompletion = catchUp?.();
    expect(catchUpCompletion).toBeInstanceOf(Promise);
    let settled = false;
    void Promise.resolve(catchUpCompletion).then(() => { settled = true; });

    httpMock.expectOne('/api/conversations').flush({ items: [] });
    httpMock.expectOne('/api/conversations/conversation-a').flush({
      id: 'conversation-a',
      workspaceId: 'workspace-a',
      type: 'ProjectChannel',
      title: 'General',
      isLocked: false,
      isArchived: false,
      members: [{
        userId: currentUserId,
        displayName: 'Mock User A',
        canRead: true,
        canPost: true,
        removedAt: null,
        leftAt: null,
      }],
      createdAt: '2026-07-09T00:00:00Z',
    });
    await Promise.resolve();
    expect(settled).toBe(false);

    httpMock.expectOne('/api/conversations/conversation-a/messages').flush({ items: [] });
    await catchUpCompletion;
    expect(settled).toBe(true);
  });

  it.each(['success', 'failure', 'authorization', 'workspace'] as const)(
    'settles a dispatched send before routine catch-up and preserves immediate %s boundary handling',
    async (outcome) => {
      const { catchUp, clear, facade, httpMock } = await configureConversationCatchUp();
      facade.loadConversation('conversation-a', 'channel', 'workspace-a');
      flushConversationOpen(httpMock);
      facade.setDraft('Send during transport registration');
      facade.sendDraft();
      const send = httpMock.expectOne('/api/conversations/conversation-a/messages');

      const completion = catchUp?.();
      expect(send.cancelled).toBe(false);
      expect(facade.page().sending).toBe(true);
      httpMock.expectNone('/api/conversations/conversation-a');

      if (outcome === 'authorization' || outcome === 'workspace') {
        clear?.(outcome);
        expect(send.cancelled).toBe(true);
        expect(facade.page().conversation.id).toBe('');
        expect(facade.page().messages).toEqual([]);
        await completion;
        httpMock.expectNone('/api/conversations/conversation-a');
      } else {
        if (outcome === 'failure') {
          send.flush({ error: 'Unavailable' }, { status: 500, statusText: 'Server Error' });
        } else {
          send.flush({
            attachments: [],
            authorDisplayName: 'Mock User A',
            authorUserId: currentUserId,
            body: 'Send during transport registration',
            conversationId: 'conversation-a',
            createdAt: '2026-07-09T01:05:00Z',
            id: 'message-created',
            isDeleted: false,
            workspaceId: 'workspace-a',
          });
        }
        await Promise.resolve();
        expect(facade.page().sendState.status).toBe(outcome === 'failure' ? 'failed' : 'sent');
        flushConversationOpen(httpMock);
        await completion;
        expect(facade.page().sending).toBe(false);
        expect(facade.page().draft).toBe(outcome === 'failure'
          ? 'Send during transport registration'
          : '');
      }
      httpMock.verify();
    },
  );

  it('discards conversation metadata when access is denied between detail and messages', async () => {
    const { catchUp, facade, httpMock } = await configureConversationCatchUp();
    facade.loadConversation('conversation-a', 'channel', 'workspace-a');
    flushConversationOpen(httpMock);

    const completion = catchUp?.();
    httpMock.expectOne('/api/conversations').flush({ items: [] });
    httpMock.expectOne('/api/conversations/conversation-a').flush({
      id: 'conversation-a',
      workspaceId: 'workspace-a',
      type: 'ProjectChannel',
      title: 'Sensitive conversation title',
      isLocked: false,
      isArchived: false,
      members: [{
        userId: 'sensitive-member',
        displayName: 'Sensitive member name',
        canRead: true,
        canPost: false,
        removedAt: null,
        leftAt: null,
      }],
      createdAt: '2026-07-09T00:00:00Z',
    });
    httpMock.expectOne('/api/conversations/conversation-a/messages').flush(
      { error: 'ConversationAccessDenied' },
      { status: 400, statusText: 'Bad Request' },
    );
    await completion;

    expect(facade.page()).toMatchObject({
      status: 'permissionDenied',
      conversations: [],
      messages: [],
      conversation: { id: '', title: 'Conversation', mentionCandidates: [] },
    });
    expect(JSON.stringify(facade.page())).not.toContain('Sensitive');
  });

  it('clears user-partitioned drafts at a session boundary', async () => {
    const events = new Subject<DurableRealtimeEvent>();
    let clearProtectedState: ((reason: 'session' | 'tenant' | 'authorization' | 'workspace') => void) | null = null;
    await TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: COGLATAS_AUTH_SESSION_MOCK, useValue: DEFAULT_AUTH_SESSION },
        {
          provide: RealtimeFacade,
          useValue: {
            durableEvents$: events.asObservable(),
            registerProtectedStateClearer: (
              _owner: string,
              callback: (reason: 'session' | 'tenant' | 'authorization' | 'workspace') => void,
            ) => {
              clearProtectedState = callback;
              return () => { clearProtectedState = null; };
            },
          },
        },
      ],
    }).compileComponents();
    TestBed.inject(MessagingFacade);
    const drafts = TestBed.inject(DraftStorageService);
    const scope = {
      tenantId: 'tenant-a',
      userId: currentUserId,
      workspaceId: 'workspace-a',
      conversationId: 'conversation-a',
    };
    drafts.writeDraft(scope, 'Previous user private draft');

    clearProtectedState?.('session');

    expect(drafts.readDraft(scope)).toBe('');
  });

  it('maps own messages from the current user id', () => {
    const ownMessage = mapMessage(
      {
        id: 'message-own',
        authorUserId: currentUserId,
        authorDisplayName: 'Mock User A',
        body: 'Mine',
        createdAt: '2026-07-09T01:00:00Z',
        isDeleted: false
      },
      currentUserId
    );
    const otherMessage = mapMessage(
      {
        id: 'message-other',
        authorUserId: 'other-user',
        authorDisplayName: 'Other',
        body: 'Theirs',
        createdAt: '2026-07-09T01:01:00Z',
        isDeleted: false
      },
      currentUserId
    );

    expect(ownMessage.isOwnMessage).toBe(true);
    expect(otherMessage.isOwnMessage).toBe(false);
  });

  it('shows a pending own message during send and confirms it only after backend success', async () => {
    const httpMock = await configureHttpTest([ChannelMessagingPageComponent]);
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock);
    const facade = TestBed.inject(MessagingFacade);

    facade.setDraft('Backend-bound message');
    facade.sendDraft();
    fixture.detectChanges();

    let root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="pending-message"]')?.textContent).toContain('Backend-bound message');
    expect(texts(root, '[data-testid="confirmed-message"]').some((text) => text.includes('Backend-bound message'))).toBe(
      false
    );

    const sendRequest = httpMock.expectOne('/api/conversations/conversation-a/messages');
    expect(sendRequest.request.withCredentials).toBe(true);
    expect(sendRequest.request.body).toMatchObject({ body: 'Backend-bound message' });
    expect(sendRequest.request.body.clientRequestId).toMatch(/^[0-9a-f-]{36}$/i);
    sendRequest.flush({
      id: 'message-created',
      workspaceId: 'workspace-a',
      conversationId: 'conversation-a',
      authorUserId: currentUserId,
      authorDisplayName: 'Mock User A',
      body: 'Backend-bound message',
      attachments: [],
      createdAt: '2026-07-09T01:05:00Z',
      isDeleted: false
    });

    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="pending-message"]')).toBeNull();
    expect(texts(root, '[data-testid="confirmed-message"]').some((text) => text.includes('Backend-bound message'))).toBe(
      true
    );
    expect(root.querySelector('.message--own')?.textContent).toContain('Backend-bound message');
    expect(root.querySelector<HTMLTextAreaElement>('[data-testid="message-draft"]')?.value).toBe('');
    expect(facade.page().sendState).toEqual({ status: 'sent', messageId: 'message-created' });
  });

  it('preserves the draft and does not show confirmed success when send fails', async () => {
    const httpMock = await configureHttpTest([ChannelMessagingPageComponent]);
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock);
    const facade = TestBed.inject(MessagingFacade);

    facade.setDraft('Do not clear me');
    facade.sendDraft();
    httpMock.expectOne('/api/conversations/conversation-a/messages').flush(
      { message: 'failed' },
      { status: 500, statusText: 'Server Error' }
    );

    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(texts(root, '[data-testid="confirmed-message"]').some((text) => text.includes('Do not clear me'))).toBe(false);
    expect(root.querySelector('[data-testid="pending-message"]')).toBeNull();
    expect(root.querySelector<HTMLTextAreaElement>('[data-testid="message-draft"]')?.value).toBe('Do not clear me');
    expect(root.textContent).toContain('Message API request failed.');
    expect(facade.page().sendState.status).toBe('failed');
  });

  it('renders attachment copy as disabled and hides unsupported paging controls', async () => {
    const httpMock = await configureHttpTest([ChannelMessagingPageComponent]);
    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);
    flushConversationOpen(httpMock);

    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="attachment-disabled"]')?.textContent).toContain(
      'Attachments are disabled'
    );
    expect(root.querySelector('[data-testid="load-older"]')).toBeNull();
    expect(root.querySelector('[data-testid="load-newer"]')).toBeNull();
  });

  it('does not expose fake retry for failed mock messages', async () => {
    await TestBed.configureTestingModule({
      imports: [ChannelMessagingPageComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: COGLATAS_AUTH_SESSION_MOCK,
          useValue: DEFAULT_AUTH_SESSION
        },
        { provide: COGLATAS_MESSAGING_PAGE_MOCK, useValue: MESSAGING_PAGE_SCENARIOS.failedOutgoingRetry },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: of(convertToParamMap({ workspaceId: 'workspace-a', conversationId: 'channel-general' }))
          }
        }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ChannelMessagingPageComponent);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="failed-message"]')).not.toBeNull();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="retry-failed-message"]')).toBeNull();
  });
});

function texts(root: HTMLElement, selector: string): readonly string[] {
  return Array.from(root.querySelectorAll(selector)).map((element) => element.textContent ?? '');
}

function deletedZeroReplyThread(messageId: string): Record<string, unknown> {
  return {
    rootMessage: {
      id: messageId,
      workspaceId: 'workspace-a',
      conversationId: 'conversation-a',
      authorUserId: currentUserId,
      authorDisplayName: 'Mock User A',
      body: '',
      attachments: [],
      createdAt: '2026-07-09T01:01:00Z',
      isDeleted: true,
      version: 3
    },
    replies: [],
    summary: {
      threadRootMessageId: messageId,
      replyCount: 0,
      latestReplyAt: null,
      participantDisplayNames: []
    },
    hasMore: false,
    maximumReplies: 100
  };
}

function liveZeroReplyThread(messageId: string): Record<string, unknown> {
  const thread = deletedZeroReplyThread(messageId);
  return {
    ...thread,
    rootMessage: {
      ...(thread['rootMessage'] as Record<string, unknown>),
      body: 'My editable backend message',
      isDeleted: false,
      version: 1
    }
  };
}
