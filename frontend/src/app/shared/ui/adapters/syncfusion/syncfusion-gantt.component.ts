import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import {
  EditService,
  GanttModule,
  SelectionService
} from '@syncfusion/ej2-angular-gantt';

import {
  CoglatasGanttContract,
  CoglatasGanttDateOnly,
  CoglatasGanttEditIntent,
  CoglatasGanttItem
} from '../../contracts/coglatas-complex-adapter.contracts';

interface SyncfusionGanttRow {
  readonly taskId: string;
  readonly title: string;
  readonly parentTaskId: string | null;
  readonly startDate: Date | null;
  readonly endDate: Date | null;
  readonly progress: number;
  readonly isMilestone: boolean;
  readonly isManual: true;
  readonly predecessor: string;
}

interface SyncfusionGanttTaskData {
  readonly taskId?: string | number;
  readonly startDate?: Date | null;
  readonly endDate?: Date | null;
  readonly progress?: number;
}

interface SyncfusionGanttRecord {
  readonly taskData?: SyncfusionGanttTaskData;
  readonly ganttProperties?: SyncfusionGanttTaskData;
}

interface SyncfusionTaskbarEvent {
  readonly data?: SyncfusionGanttRecord;
  readonly editingFields?: SyncfusionGanttTaskData;
  readonly taskBarEditAction?: string;
  cancel?: boolean;
}

interface SyncfusionActionEvent {
  readonly requestType?: string;
  readonly action?: string;
  cancel?: boolean;
}

const dateOnlyPattern = /^(\d{4})-(\d{2})-(\d{2})$/u;
export const SYNCFUSION_GANTT_THEME_ASSETS = [
  'assets/vendor/syncfusion/base/material3.css',
  'assets/vendor/syncfusion/treegrid/material3.css',
  'assets/vendor/syncfusion/layouts/material3.css',
  'assets/vendor/syncfusion/popups/material3.css',
  'assets/vendor/syncfusion/gantt/material3.css'
] as const;

/**
 * Syncfusion needs local Date objects. Decomposing DateOnly values prevents a
 * browser timezone from shifting the canonical calendar day.
 */
export function parseGanttDateOnly(value: CoglatasGanttDateOnly | null): Date | null {
  if (value === null) {return null;}
  const match = dateOnlyPattern.exec(value);
  if (!match) {return null;}

  const year = Number(match[1]);
  const monthIndex = Number(match[2]) - 1;
  const day = Number(match[3]);
  const date = new Date(year, monthIndex, day);
  if (year < 100) {date.setFullYear(year);}
  date.setHours(0, 0, 0, 0);

  return date.getFullYear() === year
    && date.getMonth() === monthIndex
    && date.getDate() === day
    ? date
    : null;
}

/** Convert vendor-local Date values back to canonical DateOnly text. */
export function formatGanttDateOnly(value: Date | null | undefined): CoglatasGanttDateOnly | null {
  if (!(value instanceof Date) || Number.isNaN(value.getTime())) {return null;}
  const year = String(value.getFullYear()).padStart(4, '0');
  const month = String(value.getMonth() + 1).padStart(2, '0');
  const day = String(value.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

@Component({
  selector: 'coglatas-syncfusion-gantt',
  standalone: true,
  imports: [GanttModule],
  providers: [EditService, SelectionService],
  template: `
    <section
      class="coglatas-syncfusion-gantt"
      [attr.aria-label]="contract.ariaLabel + ' timeline chart'"
      data-testid="coglatas-syncfusion-gantt">
      <p class="coglatas-syncfusion-gantt__notice">
        Timeline pointer editing is optional. The complete keyboard and form workflow follows the chart.
      </p>
      <ejs-gantt
        [attr.aria-label]="contract.ariaLabel + ' visual timeline'"
        [dataSource]="dataSource"
        [taskFields]="taskFields"
        [columns]="columns"
        [editSettings]="editSettings"
        [taskMode]="'Manual'"
        [readOnly]="contract.readOnly"
        [allowKeyboard]="true"
        [allowSelection]="true"
        [allowTaskbarDragAndDrop]="false"
        [allowRowDragAndDrop]="false"
        [allowParentDependency]="false"
        [allowUnscheduledTasks]="true"
        [autoCalculateDateScheduling]="false"
        [autoUpdatePredecessorOffset]="false"
        [enablePredecessorValidation]="false"
        [validateManualTasksOnLinking]="false"
        [updateOffsetOnTaskbarEdit]="false"
        [enableCriticalPath]="false"
        [renderBaseline]="false"
        [enableContextMenu]="false"
        [enableUndoRedo]="false"
        [enablePersistence]="false"
        [includeWeekend]="true"
        [highlightWeekends]="false"
        [height]="'28rem'"
        [width]="'100%'"
        (actionBegin)="handleActionBegin($event)"
        (actionComplete)="handleActionComplete($event)"
        (actionFailure)="handleFailure()"
        (taskbarEditing)="handleTaskbarEditing($event)"
        (taskbarEdited)="handleTaskbarEdited($event)"
      />
    </section>
  `,
  styleUrl: './syncfusion-gantt.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SyncfusionGanttComponent {
  @Input({ required: true }) contract!: CoglatasGanttContract<object>;
  @Output() readonly editRequested = new EventEmitter<CoglatasGanttEditIntent>();
  @Output() readonly interactionActiveChange = new EventEmitter<boolean>();
  @Output() readonly vendorFailed = new EventEmitter<void>();

  private interactionActive = false;

  readonly taskFields = {
    id: 'taskId',
    name: 'title',
    parentID: 'parentTaskId',
    startDate: 'startDate',
    endDate: 'endDate',
    progress: 'progress',
    milestone: 'isMilestone',
    manual: 'isManual',
    dependency: 'predecessor'
  };

  readonly columns = [
    { field: 'taskId', headerText: 'ID', visible: false },
    { field: 'title', headerText: 'Work item', width: 240 },
    { field: 'startDate', headerText: 'Start', width: 110, format: 'yyyy-MM-dd' },
    { field: 'endDate', headerText: 'End', width: 110, format: 'yyyy-MM-dd' },
    { field: 'progress', headerText: 'Progress', width: 95 }
  ];

  get editSettings(): {
    allowEditing: false;
    allowAdding: false;
    allowDeleting: false;
    allowTaskbarEditing: boolean;
  } {
    return {
      allowEditing: false,
      allowAdding: false,
      allowDeleting: false,
      allowTaskbarEditing: this.hasAnyPointerEdit
    };
  }

  get dataSource(): readonly SyncfusionGanttRow[] {
    const items = this.canonicalItems;
    const itemIds = new Set(items.map((item) => item.taskId));
    const taskIds = new Set(items.filter((item) => item.kind === 'task').map((item) => item.taskId));
    const predecessors = new Map<string, string[]>();
    for (const dependency of this.contract.dependencies ?? []) {
      if (dependency.type !== 'finishToStart'
        || !taskIds.has(dependency.predecessorTaskId)
        || !taskIds.has(dependency.successorTaskId)) {continue;}
      const values = predecessors.get(dependency.successorTaskId) ?? [];
      values.push(`${dependency.predecessorTaskId}FS`);
      predecessors.set(dependency.successorTaskId, values);
    }

    return items.map((item) => {
      const milestoneDate = item.kind === 'milestone'
        ? parseGanttDateOnly(item.milestoneDate)
        : null;
      return {
        taskId: item.taskId,
        title: item.title,
        parentTaskId: item.parentTaskId && itemIds.has(item.parentTaskId)
          ? item.parentTaskId
          : null,
        startDate: milestoneDate ?? parseGanttDateOnly(item.plannedStartDate),
        endDate: milestoneDate ?? parseGanttDateOnly(item.plannedEndDate),
        progress: item.progressPercent,
        isMilestone: item.kind === 'milestone',
        isManual: true,
        predecessor: (predecessors.get(item.taskId) ?? []).sort().join(',')
      };
    });
  }

  handleActionBegin(event: SyncfusionActionEvent): void {
    const operation = `${event.requestType ?? ''} ${event.action ?? ''}`.toLowerCase();
    if (/connector|dependency|predecessor/u.test(operation)) {
      event.cancel = true;
      this.endInteraction();
    }
  }

  handleActionComplete(event: SyncfusionActionEvent): void {
    const operation = `${event.requestType ?? ''} ${event.action ?? ''}`.toLowerCase();
    if (/cancel|failure/u.test(operation)) {this.endInteraction();}
  }

  handleTaskbarEditing(event: SyncfusionTaskbarEvent): void {
    const item = this.itemFor(event);
    const action = this.pointerAction(event.taskBarEditAction);
    if (!item || action === 'connector' || action === 'unsupported'
      || !this.canApplyPointerAction(item, action)) {
      event.cancel = true;
      this.endInteraction();
      return;
    }

    this.beginInteraction();
  }

  handleTaskbarEdited(event: SyncfusionTaskbarEvent): void {
    try {
      const item = this.itemFor(event);
      const action = this.pointerAction(event.taskBarEditAction);
      if (!item || action === 'connector' || action === 'unsupported'
        || !this.canApplyPointerAction(item, action)) {return;}

      const values = event.editingFields ?? event.data?.ganttProperties;
      if (action === 'progress') {
        const progress = Number(values?.progress);
        if (!Number.isFinite(progress)) {return;}
        this.editRequested.emit({
          kind: 'progress',
          taskId: item.taskId,
          progressPercent: Math.min(100, Math.max(0, Math.round(progress))),
          expectedVersion: item.version,
          source: 'pointer'
        });
        return;
      }

      const start = formatGanttDateOnly(values?.startDate);
      const end = formatGanttDateOnly(values?.endDate);
      if (item.kind === 'milestone' && start === null) {return;}
      this.editRequested.emit({
        kind: 'schedule',
        taskId: item.taskId,
        plannedStartDate: item.kind === 'task' ? start : null,
        plannedEndDate: item.kind === 'task' ? end : null,
        milestoneDate: item.kind === 'milestone' ? start : null,
        expectedVersion: item.version,
        source: 'pointer'
      });
    } finally {
      this.endInteraction();
    }
  }

  handleFailure(): void {
    this.endInteraction();
    this.vendorFailed.emit();
  }

  private get canonicalItems(): readonly CoglatasGanttItem[] {
    const unique = new Map<string, CoglatasGanttItem>();
    for (const item of [
      ...(this.contract.scheduledItems ?? []),
      ...(this.contract.unscheduledItems ?? []),
      ...(this.contract.canonicalMilestones ?? [])
    ]) {unique.set(item.taskId, item);}
    return [...unique.values()];
  }

  private get hasAnyPointerEdit(): boolean {
    return this.canonicalItems.some((item) =>
      this.canApplyPointerAction(item, 'schedule')
      || this.canApplyPointerAction(item, 'progress'));
  }

  private itemFor(event: SyncfusionTaskbarEvent): CoglatasGanttItem | undefined {
    const taskId = event.data?.taskData?.taskId
      ?? event.data?.ganttProperties?.taskId;
    return taskId === undefined
      ? undefined
      : this.canonicalItems.find((item) => item.taskId === String(taskId));
  }

  private pointerAction(value: string | undefined): 'schedule' | 'progress' | 'connector' | 'unsupported' {
    const action = value?.toLowerCase() ?? '';
    if (action.includes('connector')) {return 'connector';}
    if (action.includes('progress')) {return 'progress';}
    if (action.includes('drag') || action.includes('resiz')) {return 'schedule';}
    return 'unsupported';
  }

  private canApplyPointerAction(item: CoglatasGanttItem, action: 'schedule' | 'progress'): boolean {
    if (this.contract.readOnly
      || this.contract.busyItemId === item.taskId
      || this.isDerivedParent(item)) {return false;}
    if (action === 'progress') {
      return (this.contract.permissions?.canEditProgress ?? false)
        && item.kind === 'task'
        && item.scheduleEditPermissions.canEditProgress;
    }
    const hasCompletePointerSchedule =
      item.kind === 'milestone'
        ? item.milestoneDate !== null
        : item.plannedStartDate !== null && item.plannedEndDate !== null;

    return hasCompletePointerSchedule
      && (this.contract.permissions?.canEditSchedule ?? false)
      && item.scheduleEditPermissions.canEditSchedule;
  }

  private isDerivedParent(item: CoglatasGanttItem): boolean {
    return item.progressIsDerived
      || this.canonicalItems.some((candidate) => candidate.parentTaskId === item.taskId);
  }

  private beginInteraction(): void {
    if (this.interactionActive) {return;}
    this.interactionActive = true;
    this.interactionActiveChange.emit(true);
  }

  private endInteraction(): void {
    if (!this.interactionActive) {return;}
    this.interactionActive = false;
    this.interactionActiveChange.emit(false);
  }
}
