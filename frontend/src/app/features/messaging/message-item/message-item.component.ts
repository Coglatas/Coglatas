import { AfterViewChecked, ChangeDetectionStrategy, Component, ElementRef, EventEmitter, Input, Output, ViewChild } from '@angular/core';
import {
  LucideEllipsis,
  LucideBookmarkPlus,
  LucideFlag,
  LucideMessageSquare,
  LucidePencil,
  LucideTrash
} from '@lucide/angular';

import { CoglatasDialogComponent } from '../../../shared/ui/coglatas-dialog/coglatas-dialog.component';
import { MessagingMessageActionState, MessagingMessageViewModel } from '../messaging.types';

@Component({
  changeDetection: ChangeDetectionStrategy.Eager,
  selector: 'app-message-item',
  standalone: true,
  imports: [CoglatasDialogComponent, LucideBookmarkPlus, LucideEllipsis, LucideFlag, LucideMessageSquare, LucidePencil, LucideTrash],
  template: `
    <article
      #messageArticle
      class="message"
      [attr.data-testid]="message.deliveryState === 'confirmed' ? 'confirmed-message' : 'pending-message'"
      [attr.id]="messageElementId"
      [class.message--own]="message.isOwnMessage"
      [class.message--focus-target]="focusTarget"
      [attr.tabindex]="focusTarget ? -1 : null"
    >
      <header class="message__meta">
        <span class="message__author">{{ message.authorLabel }}</span>
        <span class="message__role">{{ message.authorRoleLabel }}</span>
        <span class="message__time">{{ message.sentAtLabel }}</span>
        @if (message.editedAt) {
          <span class="message__edited" data-testid="message-edited-marker">Edited</span>
        }
      </header>

      @if (!message.isDeleted) {
        @if (isEditing) {
          <form class="message__edit-form" (submit)="saveEdit($event)">
            <label [attr.for]="editInputId">Edit message</label>
            <textarea
              #editInput
              [id]="editInputId"
              [value]="messageAction.draft"
              [disabled]="isBusy"
              [attr.data-testid]="'message-edit-input-' + message.id"
              (input)="changeEditDraft($event)"
            ></textarea>
            @if (messageAction.error) {
              <p class="message__action-error" role="alert">{{ messageAction.error }}</p>
            }
            <div class="message__edit-actions">
              <button type="button" [disabled]="isBusy" (click)="cancelEdit()">Cancel</button>
              <button type="submit" [disabled]="isBusy" [attr.data-testid]="'save-message-edit-' + message.id">
                {{ isBusy ? 'Saving...' : 'Save changes' }}
              </button>
            </div>
          </form>
        } @else {
          <p class="message__body" data-testid="message-body">{{ message.body }}</p>
        }
      } @else {
        <p class="message__tombstone" data-testid="message-tombstone">Message deleted</p>
      }

      @if (canShowActions || canShowThreadEntry) {
          <div class="message__actions" role="group" [attr.aria-label]="'Actions for message from ' + message.authorLabel">
            @if (canShowThreadEntry) {
              <button
                type="button"
                class="message__thread-entry"
                [attr.id]="threadButtonId"
                [attr.data-testid]="'open-message-thread-' + message.id"
                [attr.aria-label]="threadEntryAriaLabel"
                (click)="openThread.emit({ messageId: message.id, triggerElementId: threadButtonId })"
              >
                <svg lucideMessageSquare aria-hidden="true"></svg>
                <span aria-hidden="true">&#x21B3;</span>
                <span>{{ threadEntryLabel }}</span>
              </button>
            }
            @if (canShowActions) {
              <button
                type="button"
                class="message__action-button message__action-button--primary"
                [attr.data-testid]="'save-message-for-later-' + message.id"
                [attr.aria-label]="'Save message from ' + message.authorLabel + ' for later'"
                [disabled]="isBusy || hasActiveAction"
                (click)="saveForLater()"
              >
                <svg lucideBookmarkPlus aria-hidden="true"></svg>
                <span>Save for later</span>
              </button>
              <div class="message__overflow">
                <button
                  type="button"
                  class="message__action-button message__more-button"
                  [attr.id]="moreButtonId"
                  [attr.data-testid]="'message-more-actions-' + message.id"
                  [attr.aria-label]="'More actions for message from ' + message.authorLabel"
                  [attr.aria-expanded]="overflowOpen"
                  [attr.aria-controls]="overflowPanelId"
                  [disabled]="isBusy || hasActiveAction"
                  (click)="toggleOverflow()"
                >
                  <svg lucideEllipsis aria-hidden="true"></svg>
                  <span>More</span>
                </button>
                @if (overflowOpen && !hasActiveAction) {
                  <div class="message__overflow-panel" [attr.id]="overflowPanelId" data-testid="message-action-overflow">
                    @if (canEditMessage) {
                      <button
                        type="button"
                        class="message__action-button"
                        [attr.id]="editButtonId"
                        [attr.data-testid]="'edit-message-' + message.id"
                        [attr.aria-label]="'Edit message from ' + message.authorLabel"
                        (click)="openEdit()"
                      >
                        <svg lucidePencil aria-hidden="true"></svg>
                        <span>Edit</span>
                      </button>
                    }
                    @if (message.isOwnMessage) {
                      <button
                        type="button"
                        class="message__action-button message__action-button--danger"
                        [attr.data-testid]="'delete-message-' + message.id"
                        [attr.aria-label]="'Delete message from ' + message.authorLabel"
                        (click)="openDelete()"
                      >
                        <svg lucideTrash aria-hidden="true"></svg>
                        <span>Delete</span>
                      </button>
                    }
                    <button
                      type="button"
                      class="message__action-button"
                      [attr.data-testid]="'report-message-' + message.id"
                      [attr.aria-label]="'Report message from ' + message.authorLabel"
                      (click)="openReport()"
                    >
                      <svg lucideFlag aria-hidden="true"></svg>
                      <span>Report</span>
                    </button>
                  </div>
                }
              </div>
            }
          </div>
      }

      @if (message.readState?.ownReadLabel) {
        <p class="message__read" data-testid="own-read-marker">{{ message.readState?.ownReadLabel }}</p>
      }
      @if (message.readState?.otherReadSummaryLabel) {
        <p class="message__read" data-testid="other-read-summary">{{ message.readState?.otherReadSummaryLabel }}</p>
      }
      @if (canViewOthersPreciseReadTimestamps && message.readState?.otherReadPreciseTimestampLabel) {
        <p class="message__read" data-testid="other-read-precise">
          {{ message.readState?.otherReadPreciseTimestampLabel }}
        </p>
      }
    </article>

    <app-coglatas-dialog
      [open]="isDeleteConfirmation"
      title="Delete message?"
      description="This removes the message from the current conversation view."
      confirmLabel="Delete message"
      [busy]="isBusy"
      [destructive]="true"
      [focusReturnFallbackId]="moreButtonId"
      (confirm)="confirmDelete.emit(message.id)"
      (cancel)="cancelAction.emit()"
    >
      @if (messageAction.error) {
        <p class="message__action-error" role="alert">{{ messageAction.error }}</p>
      }
    </app-coglatas-dialog>

    <app-coglatas-dialog
      [open]="isReportConfirmation"
      title="Report message"
      description="The current service records this report request. It does not show an evidence package or case status."
      confirmLabel="Record report request"
      [busy]="isBusy"
      [focusReturnFallbackId]="moreButtonId"
      (confirm)="confirmReport.emit({ messageId: message.id, reasonCode: 'reported' })"
      (cancel)="cancelAction.emit()"
    >
      <p>This sends a general report request for this message.</p>
      @if (messageAction.error) {
        <p class="message__action-error" role="alert">{{ messageAction.error }}</p>
      }
    </app-coglatas-dialog>
  `,
  styleUrl: './message-item.component.scss',
})
export class MessageItemComponent implements AfterViewChecked {
  @ViewChild('messageArticle') private messageArticle?: ElementRef<HTMLElement>;
  @ViewChild('editInput') private editInput?: ElementRef<HTMLTextAreaElement>;
  @Input({ required: true }) message!: MessagingMessageViewModel;
  @Input({ required: true }) messageAction!: MessagingMessageActionState;
  @Input() canEditOwnMessages = false;
  @Input() canViewOthersPreciseReadTimestamps = false;
  @Input() canOpenThreads = false;
  @Input() focusTarget = false;

  @Output() readonly startEdit = new EventEmitter<string>();
  @Output() readonly editDraftChange = new EventEmitter<{ readonly messageId: string; readonly draft: string }>();
  @Output() readonly saveEditRequested = new EventEmitter<string>();
  @Output() readonly cancelAction = new EventEmitter<void>();
  @Output() readonly requestDelete = new EventEmitter<string>();
  @Output() readonly confirmDelete = new EventEmitter<string>();
  @Output() readonly requestReport = new EventEmitter<string>();
  @Output() readonly saveForLaterRequested = new EventEmitter<string>();
  @Output() readonly confirmReport = new EventEmitter<{ readonly messageId: string; readonly reasonCode: string }>();
  @Output() readonly openThread = new EventEmitter<{
    readonly messageId: string;
    readonly triggerElementId: string;
  }>();

  overflowOpen = false;
  private focusedEditorKey: string | null = null;
  private focusedTargetId: string | null = null;

  get messageElementId(): string {
    return `message-${this.safeId}`;
  }

  get editButtonId(): string {
    return `edit-message-${this.safeId}`;
  }

  get moreButtonId(): string {
    return `message-more-actions-${this.safeId}`;
  }

  get editInputId(): string {
    return `edit-message-input-${this.safeId}`;
  }

  get overflowPanelId(): string {
    return `message-action-overflow-${this.safeId}`;
  }

  get threadButtonId(): string {
    return `open-message-thread-${this.safeId}`;
  }

  get canShowThreadEntry(): boolean {
    return this.canOpenThreads &&
      this.message.deliveryState === 'confirmed' &&
      !this.message.threadRootMessageId &&
      (!this.message.isDeleted || (this.message.thread?.replyCount ?? 0) > 0);
  }

  get threadEntryLabel(): string {
    const count = this.message.thread?.replyCount ?? 0;
    return count > 0 ? `${count} ${count === 1 ? 'reply' : 'replies'}` : 'Reply in thread';
  }

  get threadEntryAriaLabel(): string {
    return `${this.threadEntryLabel} for message from ${this.message.authorLabel}`;
  }

  get isEditing(): boolean {
    return this.messageAction.messageId === this.message.id && this.messageAction.mode === 'editing';
  }

  get isDeleteConfirmation(): boolean {
    return this.messageAction.messageId === this.message.id && this.messageAction.mode === 'confirmDelete';
  }

  get isReportConfirmation(): boolean {
    return this.messageAction.messageId === this.message.id && this.messageAction.mode === 'confirmReport';
  }

  get isBusy(): boolean {
    return this.messageAction.messageId === this.message.id && this.messageAction.pending !== null;
  }

  get hasActiveAction(): boolean {
    return this.messageAction.mode !== 'idle';
  }

  get canShowActions(): boolean {
    return this.message.deliveryState === 'confirmed' && !this.message.isDeleted && !this.isEditing;
  }

  get canEditMessage(): boolean {
    return this.message.isOwnMessage && this.canEditOwnMessages;
  }

  toggleOverflow(): void {
    this.overflowOpen = !this.overflowOpen;
  }

  openEdit(): void {
    this.overflowOpen = false;
    this.startEdit.emit(this.message.id);
  }

  changeEditDraft(event: Event): void {
    this.editDraftChange.emit({
      messageId: this.message.id,
      draft: (event.target as HTMLTextAreaElement).value
    });
  }

  saveEdit(event: SubmitEvent): void {
    event.preventDefault();
    this.saveEditRequested.emit(this.message.id);
  }

  cancelEdit(): void {
    this.cancelAction.emit();
    queueMicrotask(() => document.getElementById(this.moreButtonId)?.focus());
  }

  openDelete(): void {
    this.overflowOpen = false;
    this.requestDelete.emit(this.message.id);
  }

  openReport(): void {
    this.overflowOpen = false;
    this.requestReport.emit(this.message.id);
  }

  saveForLater(): void {
    this.overflowOpen = false;
    this.saveForLaterRequested.emit(this.message.id);
  }

  private get safeId(): string {
    return this.message.id.replace(/[^a-zA-Z0-9_-]/g, '-');
  }

  ngAfterViewChecked(): void {
    const targetId = this.focusTarget ? this.message.id : null;
    if (targetId && targetId !== this.focusedTargetId) {
      this.focusedTargetId = targetId;
      queueMicrotask(() => this.messageArticle?.nativeElement.focus());
    } else if (!targetId) {
      this.focusedTargetId = null;
    }
    const editorKey = this.isEditing && !this.isBusy ? `${this.message.id}:${this.messageAction.mode}` : null;
    if (!editorKey || editorKey === this.focusedEditorKey) {
      if (!editorKey) {
        this.focusedEditorKey = null;
      }
      return;
    }
    this.focusedEditorKey = editorKey;
    queueMicrotask(() => this.editInput?.nativeElement.focus());
  }
}
