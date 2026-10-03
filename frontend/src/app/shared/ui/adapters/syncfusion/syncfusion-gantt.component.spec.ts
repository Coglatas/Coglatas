import {
  CoglatasGanttContract,
  CoglatasGanttEditIntent,
  CoglatasGanttItem
} from '../../contracts/coglatas-complex-adapter.contracts';

vi.mock('@syncfusion/ej2-angular-gantt', async () => {
  const { Injectable, NgModule } = await import('@angular/core');
  class GanttModule {}
  class GanttComponent {}
  class EditService {}
  class SelectionService {}
  NgModule({})(GanttModule);
  Injectable()(EditService);
  Injectable()(SelectionService);
  return { EditService, GanttComponent, GanttModule, SelectionService };
});

import {
  SYNCFUSION_GANTT_THEME_ASSETS,
  SyncfusionGanttComponent,
  formatGanttDateOnly,
  parseGanttDateOnly
} from './syncfusion-gantt.component';

describe('SyncfusionGanttComponent adapter boundary', () => {
  it('loads lazy Gantt theme assets in official dependency order', () => {
    expect(SYNCFUSION_GANTT_THEME_ASSETS).toEqual([
      'assets/vendor/syncfusion/base/material3.css',
      'assets/vendor/syncfusion/treegrid/material3.css',
      'assets/vendor/syncfusion/layouts/material3.css',
      'assets/vendor/syncfusion/popups/material3.css',
      'assets/vendor/syncfusion/gantt/material3.css'
    ]);
  });

  it('keeps theme, density, focus, and reduced-motion policy scoped to the adapter', () => {
    const styles = (SyncfusionGanttComponent as unknown as {
      ɵcmp: { styles: readonly string[] };
    }).ɵcmp.styles.join('\n').replaceAll('%NS%', '');

    expect(styles).toContain('.coglatas-syncfusion-gantt');
    expect(styles).toContain('--coglatas-color-bg-surface');
    expect(styles).toContain('--coglatas-input-padding-inline');
    expect(styles).toContain('--coglatas-focus-outline');
    expect(styles).toContain('prefers-reduced-motion');
  });

  it('round-trips canonical DateOnly values through local date components', () => {
    const value = parseGanttDateOnly('2026-03-08');

    expect(value?.getFullYear()).toBe(2026);
    expect(value?.getMonth()).toBe(2);
    expect(value?.getDate()).toBe(8);
    expect(formatGanttDateOnly(value)).toBe('2026-03-08');
    expect(parseGanttDateOnly('2026-02-30')).toBeNull();
    expect(parseGanttDateOnly('2026-03-08T00:00:00Z')).toBeNull();
  });

  it('maps only canonical Finish-to-Start dependencies into manual vendor rows', () => {
    const component = new SyncfusionGanttComponent();
    component.contract = ganttContract();

    const dataSource = component.dataSource;
    expect(dataSource).toEqual([
      expect.objectContaining({
        taskId: 'task-parent',
        parentTaskId: null,
        isManual: true,
        predecessor: ''
      }),
      expect.objectContaining({
        taskId: 'task-leaf',
        parentTaskId: 'task-parent',
        startDate: expect.any(Date),
        endDate: expect.any(Date),
        predecessor: 'task-parentFS'
      }),
      expect.objectContaining({
        taskId: 'milestone-1',
        isMilestone: true,
        predecessor: ''
      })
    ]);
    const milestone = dataSource.find((item) => item.taskId === 'milestone-1')!;
    expect(formatGanttDateOnly(milestone.startDate)).toBe('2026-07-15');
    expect(formatGanttDateOnly(milestone.endDate)).toBe('2026-07-15');
  });

  it('keeps unscheduled canonical Tasks in the vendor projection with null dates', () => {
    const component = new SyncfusionGanttComponent();
    const contract = ganttContract();
    const unscheduled = item({
      taskId: 'task-unscheduled',
      title: 'Unscheduled',
      plannedStartDate: null,
      plannedEndDate: null,
      version: 11
    });
    component.contract = {
      ...contract,
      scheduledItems: [],
      unscheduledItems: [unscheduled],
      canonicalMilestones: [],
      dependencies: []
    };

    expect(component.dataSource).toEqual([
      expect.objectContaining({
        taskId: 'task-unscheduled',
        startDate: null,
        endDate: null,
        isMilestone: false,
        isManual: true
      })
    ]);
  });

  it('emits canonical pointer schedule and progress intents without vendor types', () => {
    const component = new SyncfusionGanttComponent();
    component.contract = ganttContract();
    const edits: CoglatasGanttEditIntent[] = [];
    const interactions: boolean[] = [];
    component.editRequested.subscribe((intent) => edits.push(intent));
    component.interactionActiveChange.subscribe((active) => interactions.push(active));

    const scheduleEvent = taskbarEvent('task-leaf', 'ChildDrag', {
      startDate: new Date(2026, 6, 7),
      endDate: new Date(2026, 6, 10)
    });
    component.handleTaskbarEditing(scheduleEvent);
    component.handleTaskbarEdited(scheduleEvent);

    const progressEvent = taskbarEvent('task-leaf', 'ProgressResizing', { progress: 62.6 });
    component.handleTaskbarEditing(progressEvent);
    component.handleTaskbarEdited(progressEvent);

    expect(edits).toEqual([
      {
        kind: 'schedule',
        taskId: 'task-leaf',
        plannedStartDate: '2026-07-07',
        plannedEndDate: '2026-07-10',
        milestoneDate: null,
        expectedVersion: 8,
        source: 'pointer'
      },
      {
        kind: 'progress',
        taskId: 'task-leaf',
        progressPercent: 63,
        expectedVersion: 8,
        source: 'pointer'
      }
    ]);
    expect(interactions).toEqual([true, false, true, false]);
  });

  it('cancels parent, unauthorized, connector, and unsupported pointer edits', () => {
    const component = new SyncfusionGanttComponent();
    component.contract = ganttContract();
    const edits: CoglatasGanttEditIntent[] = [];
    component.editRequested.subscribe((intent) => edits.push(intent));

    const parent = taskbarEvent('task-parent', 'ParentDrag', {
      startDate: new Date(2026, 6, 1),
      endDate: new Date(2026, 6, 5)
    });
    const connector = taskbarEvent('task-leaf', 'ConnectorPointRightDrag', {});
    const milestoneProgress = taskbarEvent('milestone-1', 'ProgressResizing', { progress: 50 });
    const unsupported = taskbarEvent('task-leaf', 'UnknownEdit', {});
    component.handleTaskbarEditing(parent);
    component.handleTaskbarEditing(connector);
    component.handleTaskbarEditing(milestoneProgress);
    component.handleTaskbarEdited(milestoneProgress);
    component.handleTaskbarEditing(unsupported);

    const denied = ganttContract();
    component.contract = {
      ...denied,
      permissions: { ...denied.permissions!, canEditSchedule: false }
    };
    const unauthorized = taskbarEvent('task-leaf', 'RightResizing', {
      startDate: new Date(2026, 6, 1),
      endDate: new Date(2026, 6, 5)
    });
    component.handleTaskbarEditing(unauthorized);

    expect(parent.cancel).toBe(true);
    expect(connector.cancel).toBe(true);
    expect(milestoneProgress.cancel).toBe(true);
    expect(unsupported.cancel).toBe(true);
    expect(unauthorized.cancel).toBe(true);
    expect(edits).toEqual([]);
  });

  it('cancels pointer schedule edits for a partially scheduled task', () => {
    const component = new SyncfusionGanttComponent();
    const contract = ganttContract();
    component.contract = {
      ...contract,
      scheduledItems: contract.scheduledItems.map((candidate) =>
        candidate.taskId === 'task-leaf'
          ? { ...candidate, plannedEndDate: null }
          : candidate)
    };
    const edits: CoglatasGanttEditIntent[] = [];
    component.editRequested.subscribe((intent) => edits.push(intent));
    const partialTask = taskbarEvent('task-leaf', 'ChildDrag', {
      startDate: new Date(2026, 6, 7),
      endDate: new Date(2026, 6, 10)
    });

    component.handleTaskbarEditing(partialTask);
    component.handleTaskbarEdited(partialTask);

    expect(partialTask.cancel).toBe(true);
    expect(edits).toEqual([]);
  });
});

function ganttContract(): CoglatasGanttContract<object> {
  const parent = item({
    taskId: 'task-parent',
    title: 'Parent',
    progressIsDerived: true,
    plannedStartDate: '2026-07-01',
    plannedEndDate: '2026-07-10',
    version: 5,
    permissions: false
  });
  const leaf = item({
    taskId: 'task-leaf',
    title: 'Leaf',
    parentTaskId: parent.taskId,
    plannedStartDate: '2026-07-02',
    plannedEndDate: '2026-07-04',
    version: 8
  });
  const milestone = item({
    taskId: 'milestone-1',
    title: 'Release',
    kind: 'milestone',
    milestoneDate: '2026-07-15',
    plannedStartDate: null,
    plannedEndDate: null,
    version: 3
  });
  return {
    ariaLabel: 'Canonical schedule',
    presentation: 'desktop',
    state: 'ready',
    tasks: [],
    taskIdentity: () => '',
    taskLabel: () => '',
    milestones: [],
    timezone: 'Asia/Tokyo',
    readOnly: false,
    scheduledItems: [parent, leaf],
    unscheduledItems: [],
    canonicalMilestones: [milestone],
    dependencies: [
      {
        dependencyId: 'dependency-fs',
        predecessorTaskId: parent.taskId,
        successorTaskId: leaf.taskId,
        type: 'finishToStart',
        editable: true,
        version: 1,
        warnings: []
      },
      {
        dependencyId: 'dependency-legacy',
        predecessorTaskId: leaf.taskId,
        successorTaskId: milestone.taskId,
        type: 'startToStart',
        editable: false,
        version: 2,
        warnings: []
      }
    ],
    warnings: [],
    permissions: editablePermissions()
  };
}

function item(overrides: {
  taskId: string;
  title: string;
  kind?: 'task' | 'milestone';
  parentTaskId?: string | null;
  progressIsDerived?: boolean;
  plannedStartDate?: string | null;
  plannedEndDate?: string | null;
  milestoneDate?: string | null;
  version: number;
  permissions?: boolean;
}): CoglatasGanttItem {
  const permissions = overrides.permissions === false
    ? {
        canEditSchedule: false,
        canEditProgress: false,
        canManageDependencies: false,
        canClearSchedule: false,
        canOpen: true
      }
    : editablePermissions();
  return {
    taskId: overrides.taskId,
    kind: overrides.kind ?? 'task',
    parentTaskId: overrides.parentTaskId ?? null,
    milestoneId: null,
    title: overrides.title,
    plannedStartDate: overrides.plannedStartDate ?? null,
    plannedEndDate: overrides.plannedEndDate ?? null,
    milestoneDate: overrides.milestoneDate ?? null,
    progressPercent: 40,
    progressIsDerived: overrides.progressIsDerived ?? false,
    workflowStageId: 'stage-todo',
    workflowStageName: 'Todo',
    stageCategory: 'todo',
    priority: 'high',
    isBlocked: false,
    primaryAssignee: null,
    version: overrides.version,
    scheduleEditPermissions: permissions,
    warnings: []
  };
}

function editablePermissions(): {
  canEditSchedule: true;
  canEditProgress: true;
  canManageDependencies: true;
  canClearSchedule: true;
  canOpen: true;
} {
  return {
    canEditSchedule: true,
    canEditProgress: true,
    canManageDependencies: true,
    canClearSchedule: true,
    canOpen: true
  };
}

function taskbarEvent(
  taskId: string,
  taskBarEditAction: string,
  editingFields: { startDate?: Date; endDate?: Date; progress?: number }
): {
  data: { taskData: { taskId: string } };
  editingFields: { startDate?: Date; endDate?: Date; progress?: number };
  taskBarEditAction: string;
  cancel?: boolean;
} {
  return {
    data: { taskData: { taskId } },
    editingFields,
    taskBarEditAction
  };
}
