import { Component, EnvironmentProviders, EventEmitter, Input, Output, Provider } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';

import { COGLATAS_AUTH_SESSION_MOCK, DEFAULT_AUTH_SESSION } from '../../core/auth/auth-session.facade';
import { RealtimeFacade } from '../../core/realtime/realtime.facade';
import {
  AppDataGridActionEvent,
  AppDataGridColumnDef,
  AppDataGridRowActivationEvent,
  clampAppDataGridPageSize
} from '../../shared/grid/app-data-grid/app-data-grid.types';
import { AppDataGridComponent } from '../../shared/grid/app-data-grid/app-data-grid.component';
import { routes } from '../../app.routes';
import { DEFAULT_NAVIGATION_ITEMS } from '../../layout/app-shell/app-shell.facade';
import { AdminFacade, COGLATAS_ADMIN_AUDIT_MOCK, COGLATAS_EXPORT_DIAGNOSTICS_MOCK } from './admin.facade';
import {
  AUDIT_LOG_SCENARIOS,
  AUDIT_RAW_METADATA_PROBE,
  EXPORT_DIAGNOSTICS_SCENARIOS
} from './admin.mock';
import { AuditGridRow, AuditLogScenario, ExportJobGridRow } from './admin.types';
import { AuditLogPageComponent } from './audit-log-page/audit-log-page.component';
import { ExportDiagnosticsPageComponent } from './export-diagnostics-page/export-diagnostics-page.component';

@Component({
  selector: 'app-data-grid',
  standalone: true,
  template: `
    <section data-testid="app-data-grid">
      <p data-testid="stub-page-size">{{ defaultPageSize }}/{{ maximumPageSize }}</p>
      <p data-testid="stub-row-height">{{ rowHeight }}</p>
      <p data-testid="stub-sticky-header">{{ stickyHeader }}</p>
      @for (column of columns; track column.headerName) {
        <span data-testid="grid-column">{{ column.field }}</span>
      }
      @for (row of rows; track row.id) {
        <article data-testid="audit-row">
          @for (column of columns; track column.headerName) {
            <span [attr.data-testid]="'audit-cell-' + column.field">{{ valueFor(row, column.field) }}</span>
          }
          <button type="button" data-testid="open-audit-detail" [attr.data-grid-row-id]="row.id" (click)="open(row)">Open detail</button>
          <button type="button" data-testid="activate-audit-row" [attr.data-grid-row-id]="row.id" (click)="activate(row, $any($event.currentTarget))">Activate row</button>
        </article>
      }
    </section>
  `
})
class StubAuditDataGridComponent {
  @Input() rows: readonly AuditGridRow[] = [];
  @Input() columns: readonly AppDataGridColumnDef<AuditGridRow>[] = [];
  @Input() defaultPageSize = 0;
  @Input() maximumPageSize = 0;
  @Input() rowHeight?: number;
  @Input() stickyHeader = false;
  @Input() rowIdField = 'id';
  @Input() ariaLabel = '';
  @Output() actionInvoked = new EventEmitter<AppDataGridActionEvent<AuditGridRow>>();
  @Output() rowActivated = new EventEmitter<AppDataGridRowActivationEvent<AuditGridRow>>();

  valueFor(row: AuditGridRow, field: (keyof AuditGridRow & string) | undefined): unknown {
    return field ? row[field] : '';
  }

  open(row: AuditGridRow): void {
    this.actionInvoked.emit({ actionId: 'openAuditDetail', row });
  }

  activate(row: AuditGridRow, trigger: HTMLElement): void {
    this.rowActivated.emit({ row, trigger });
  }
}

@Component({
  selector: 'app-data-grid',
  standalone: true,
  template: `
    <section data-testid="app-data-grid">
      <p data-testid="stub-page-size">{{ defaultPageSize }}/{{ maximumPageSize }}</p>
      @for (column of columns; track column.headerName) {
        <span data-testid="grid-column">{{ column.field }}</span>
      }
      @for (row of rows; track row.id) {
        <article data-testid="export-row">
          @for (column of columns; track column.headerName) {
            <span [attr.data-testid]="'export-cell-' + column.field">{{ valueFor(row, column.field) }}</span>
          }
          <button type="button" data-testid="open-export-detail" (click)="open(row)">Open detail</button>
        </article>
      }
    </section>
  `
})
class StubExportDataGridComponent {
  @Input() rows: readonly ExportJobGridRow[] = [];
  @Input() columns: readonly AppDataGridColumnDef<ExportJobGridRow>[] = [];
  @Input() defaultPageSize = 0;
  @Input() maximumPageSize = 0;
  @Input() rowIdField = 'id';
  @Input() ariaLabel = '';
  @Output() actionInvoked = new EventEmitter<AppDataGridActionEvent<ExportJobGridRow>>();

  valueFor(row: ExportJobGridRow, field: (keyof ExportJobGridRow & string) | undefined): unknown {
    return field ? row[field] : '';
  }

  open(row: ExportJobGridRow): void {
    this.actionInvoked.emit({ actionId: 'openExportJobDetail', row });
  }
}

const angularCmpKey = '\u0275cmp';

const dependencyNames = (component: unknown): string[] => {
  const cmp = (component as Record<string, { dependencies?: unknown[] }>)[angularCmpKey];
  const dependencies = (cmp?.dependencies ?? []) as Array<{ name?: string; type?: { name?: string } }>;
  return dependencies.map((dependency) => dependency.type?.name ?? dependency.name ?? '');
};

const textContent = <T>(fixture: ComponentFixture<T>): string => (fixture.nativeElement as HTMLElement).textContent ?? '';

const query = <T extends HTMLElement, C = unknown>(fixture: ComponentFixture<C>, selector: string): T | null =>
  (fixture.nativeElement as HTMLElement).querySelector<T>(selector);

const queryAll = <T extends HTMLElement, C = unknown>(fixture: ComponentFixture<C>, selector: string): T[] =>
  Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<T>(selector));

interface AuditRouteHarness {
  readonly queryParamMap: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  readonly router: { readonly calls: unknown[][]; navigate: (...args: unknown[]) => Promise<boolean> };
}

const createAuditRouteHarness = (initial?: string | Record<string, string>): AuditRouteHarness => {
  const params = typeof initial === 'string' ? { event: initial } : initial ?? {};
  const queryParamMap = new BehaviorSubject(convertToParamMap(params));
  const router = {
    calls: [] as unknown[][],
    navigate(...args: unknown[]): Promise<boolean> {
      this.calls.push(args);
      return Promise.resolve(true);
    },
  };

  return { queryParamMap, router };
};

const renderAudit = async (
  scenario: AuditLogScenario = AUDIT_LOG_SCENARIOS.default,
  routeHarness?: AuditRouteHarness,
) => {
  const providers: Provider[] = [{ provide: COGLATAS_ADMIN_AUDIT_MOCK, useValue: scenario }];
  if (routeHarness) {
    providers.push(
      {
        provide: ActivatedRoute,
        useValue: {
          queryParamMap: routeHarness.queryParamMap.asObservable(),
          get snapshot() {
            return { queryParamMap: routeHarness.queryParamMap.value };
          },
        },
      },
      { provide: Router, useValue: routeHarness.router },
    );
  }

  await TestBed.configureTestingModule({
    imports: [AuditLogPageComponent],
    providers,
  })
    .overrideComponent(AuditLogPageComponent, {
      remove: { imports: [AppDataGridComponent] },
      add: { imports: [StubAuditDataGridComponent] }
    })
    .compileComponents();

  const fixture = TestBed.createComponent(AuditLogPageComponent);
  fixture.detectChanges();
  return fixture;
};

const renderLiveAudit = async (routeHarness?: AuditRouteHarness) => {
  const providers: Array<Provider | EnvironmentProviders> = [provideHttpClient(), provideHttpClientTesting()];
  if (routeHarness) {
    providers.push(
      {
        provide: ActivatedRoute,
        useValue: {
          queryParamMap: routeHarness.queryParamMap.asObservable(),
          get snapshot() { return { queryParamMap: routeHarness.queryParamMap.value }; },
        },
      },
      { provide: Router, useValue: routeHarness.router },
    );
  }
  await TestBed.configureTestingModule({
    imports: [AuditLogPageComponent],
    providers,
  })
    .overrideComponent(AuditLogPageComponent, {
      remove: { imports: [AppDataGridComponent] },
      add: { imports: [StubAuditDataGridComponent] },
    })
    .compileComponents();

  const fixture = TestBed.createComponent(AuditLogPageComponent);
  fixture.detectChanges();
  return { fixture, httpMock: TestBed.inject(HttpTestingController) };
};

const settleAuditRender = async <T>(fixture: ComponentFixture<T>): Promise<void> => {
  TestBed.flushEffects();
  fixture.detectChanges();
  await fixture.whenRenderingDone();
};

const renderExport = async (
  scenario: (typeof EXPORT_DIAGNOSTICS_SCENARIOS)[keyof typeof EXPORT_DIAGNOSTICS_SCENARIOS] =
    EXPORT_DIAGNOSTICS_SCENARIOS.default
) => {
  await TestBed.configureTestingModule({
    imports: [ExportDiagnosticsPageComponent],
    providers: [
      { provide: COGLATAS_ADMIN_AUDIT_MOCK, useValue: AUDIT_LOG_SCENARIOS.empty },
      { provide: COGLATAS_EXPORT_DIAGNOSTICS_MOCK, useValue: scenario }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ExportDiagnosticsPageComponent);
  fixture.detectChanges();
  return fixture;
};

describe('Admin audit and export mock UI', () => {
  beforeEach(() => window.localStorage.setItem('coglatas.locale', 'en'));

  afterEach(() => {
    window.localStorage.removeItem('coglatas.locale');
    vi.restoreAllMocks();
    TestBed.resetTestingModule();
  });

  it('registers target admin routes', () => {
    const appRoute = routes.find((route) => route.path === '' && route.children);
    const childPaths = appRoute?.children?.map((route) => route.path) ?? [];

    expect(childPaths).toContain('admin/audit');
    expect(childPaths).toContain('admin/export-diagnostics');
  });

  it('does not include export diagnostics in MVP0 navigation', () => {
    expect(DEFAULT_NAVIGATION_ITEMS.map((item) => item.route)).not.toContain('/admin/export-diagnostics');
  });

  it('audit route page does not directly use AgGridAngular and does use the shared wrapper', () => {
    const auditDependencies = dependencyNames(AuditLogPageComponent);

    expect(auditDependencies).not.toContain('AgGridAngular');
    expect(auditDependencies.some((name) => name.includes('AppDataGridComponent'))).toBe(true);
  });

  it('keeps ag-grid-enterprise absent from admin component dependencies', () => {
    const enterprisePackageName = 'ag-grid' + '-enterprise';
    const adminDependencies = [...dependencyNames(AuditLogPageComponent), ...dependencyNames(ExportDiagnosticsPageComponent)].join(' ');

    expect(adminDependencies).not.toContain(enterprisePackageName);
  });

  it('renders required audit grid columns without raw metadata JSON', async () => {
    const fixture = await renderAudit();
    const columnFields = queryAll(fixture, '[data-testid="grid-column"]').map((element) => element.textContent?.trim());

    expect(columnFields).toEqual([
      'createdAt',
      'action',
      'actorDisplay',
      'targetType',
      'severity',
      'result',
      'summary',
    ]);
    expect(fixture.componentInstance.vm().columns.map((column) => column.headerName)).toEqual([
      'Created',
      'Action',
      'Actor',
      'Target',
      'Severity',
      'Result',
      'Summary',
    ]);
    expect(textContent(fixture)).not.toContain(AUDIT_RAW_METADATA_PROBE);
    expect(textContent(fixture)).not.toContain('restricted body must stay hidden');
    expect(textContent(fixture)).not.toContain('tenant/private/key');
  });

  it('announces safe audit counts and local grid presentation changes', async () => {
    const fixture = await renderAudit();
    const status = query<HTMLParagraphElement>(fixture, '[data-testid="audit-log-status"]');
    const dense = query<HTMLButtonElement>(fixture, '[data-testid="audit-density-dense"]');
    const workspace = query<HTMLInputElement>(fixture, '[data-testid="audit-column-workspace"]');

    expect(status?.getAttribute('role')).toBe('status');
    expect(status?.getAttribute('aria-live')).toBe('polite');
    expect(status?.textContent).toContain('Showing 3 audit entries.');
    expect(status?.textContent).toContain('Default density.');
    expect(status?.textContent).toContain('Workspace hidden');
    expect(status?.textContent).not.toContain(AUDIT_RAW_METADATA_PROBE);

    dense?.click();
    workspace?.click();
    fixture.detectChanges();

    expect(status?.textContent).toContain('Dense density.');
    expect(status?.textContent).toContain('Workspace shown');
  });

  it('keeps structural audit loading out of the accessibility tree while announcing the initial load once', async () => {
    const fixture = await renderAudit(AUDIT_LOG_SCENARIOS.loading);
    const content = query<HTMLElement>(fixture, '[data-testid="audit-log-content"]');
    const skeleton = query<HTMLElement>(fixture, '[data-testid="audit-log-skeleton"]');
    const status = query<HTMLElement>(fixture, '[data-testid="audit-log-status"]');

    expect(content?.getAttribute('aria-busy')).toBe('true');
    expect(skeleton?.getAttribute('aria-hidden')).toBe('true');
    expect(queryAll(fixture, 'app-skeleton .skeleton__line')).toHaveLength(5);
    expect(query(fixture, 'app-inline-loading')).toBeNull();
    expect(status?.textContent?.trim()).toBe('Loading audit log.');
  });

  it('announces fixed empty and error states without exposing response details', async () => {
    const emptyFixture = await renderAudit(AUDIT_LOG_SCENARIOS.empty);
    const emptyStatus = query<HTMLParagraphElement>(emptyFixture, '[data-testid="audit-log-status"]');

    expect(emptyStatus?.textContent?.trim()).toBe('No audit entries are available for the current authorized scope.');
    expect(textContent(emptyFixture)).not.toContain('mock audit entries');

    TestBed.resetTestingModule();
    const errorFixture = await renderAudit({
      status: 'error',
      title: 'Admin audit log',
      subtitle: 'Live API data',
      auditRecords: [],
      message: 'sensitive upstream response detail',
    });
    const errorStatus = query<HTMLParagraphElement>(errorFixture, '[data-testid="audit-log-status"]');

    expect(errorStatus?.textContent?.trim()).toBe('Audit log could not be loaded.');
    expect(errorStatus?.textContent).not.toContain('sensitive upstream response detail');
    expect(textContent(errorFixture)).not.toContain('sensitive upstream response detail');
  });

  it('keeps keyboard retry focused, blocks duplicate requests, and recovers only from a transient audit failure', async () => {
    const { fixture, httpMock } = await renderLiveAudit();
    const initial = httpMock.expectOne('/api/admin/audit-grid');

    initial.flush({ error: 'internal detail must not render' }, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    const retry = query<HTMLButtonElement>(fixture, '[data-testid="audit-log-retry"]');
    expect(retry).not.toBeNull();
    expect(retry?.textContent?.trim()).toBe('Retry');
    retry?.focus();

    retry?.click();
    fixture.detectChanges();

    expect(retry?.getAttribute('aria-disabled')).toBe('true');
    expect(retry?.textContent?.trim()).toBe('Retrying...');
    expect(document.activeElement).toBe(retry);
    expect(query(fixture, '[data-testid="audit-log-skeleton"]')).not.toBeNull();
    expect(query(fixture, '[data-testid="audit-log-status"]')?.textContent?.trim()).toBe('Retrying audit log.');

    const retryRequest = httpMock.expectOne('/api/admin/audit-grid');
    retry?.click();
    httpMock.expectNone('/api/admin/audit-grid');

    retryRequest.flush({ items: [] });
    fixture.detectChanges();
    await settleAuditRender(fixture);
    expect(query(fixture, '[data-testid="audit-log-retry"]')).toBeNull();
    expect(query(fixture, '[data-testid="audit-log-status"]')?.textContent?.trim())
      .toBe('No audit entries are available for the current authorized scope.');
    expect(document.activeElement).toBe(query(fixture, '[data-testid="audit-log-title"]'));
    httpMock.verify();
  });

  it('does not offer retry for a non-transient audit-list failure', async () => {
    const { fixture, httpMock } = await renderLiveAudit();

    httpMock.expectOne('/api/admin/audit-grid').flush({}, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();
    expect(query(fixture, '[data-testid="audit-log-retry"]')).toBeNull();
    expect(query(fixture, '[data-testid="audit-log-status"]')?.textContent?.trim())
      .toBe('Audit log could not be loaded.');

    TestBed.inject(AdminFacade).reloadAuditLog();
    httpMock.expectNone('/api/admin/audit-grid');
    httpMock.verify();
  });

  it('returns keyboard focus to the Audit title when retry ends with lost permission', async () => {
    const { fixture, httpMock } = await renderLiveAudit();
    httpMock.expectOne('/api/admin/audit-grid').flush({}, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();

    const retry = query<HTMLButtonElement>(fixture, '[data-testid="audit-log-retry"]');
    retry?.focus();
    retry?.click();
    const retryRequest = httpMock.expectOne('/api/admin/audit-grid');

    retryRequest.flush({}, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();
    await settleAuditRender(fixture);

    expect(query(fixture, '[data-testid="audit-log-retry"]')).toBeNull();
    expect(query(fixture, '[data-testid="audit-log-status"]')?.textContent?.trim())
      .toBe('Audit log access is unavailable.');
    expect(document.activeElement).toBe(query(fixture, '[data-testid="audit-log-title"]'));
    httpMock.verify();
  });

  it('does not offer retry after audit permission is lost', async () => {
    const { fixture, httpMock } = await renderLiveAudit();

    httpMock.expectOne('/api/admin/audit-grid').flush({}, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();
    expect(query(fixture, '[data-testid="audit-log-retry"]')).toBeNull();
    expect(query(fixture, '[data-testid="audit-log-status"]')?.textContent?.trim())
      .toBe('Audit log access is unavailable.');

    TestBed.inject(AdminFacade).reloadAuditLog();
    httpMock.expectNone('/api/admin/audit-grid');
    httpMock.verify();
  });

  it('restores every audit facet from a shareable URL and sends only server filter parameters', async () => {
    const routeHarness = createAuditRouteHarness({
      q: 'retention',
      severity: 'critical',
      type: 'file.export.failed',
      actor: 'Audit Admin',
      source: 'ExportJob',
      status: 'failed',
      range: '7d',
    });
    const { fixture, httpMock } = await renderLiveAudit(routeHarness);
    const supersededDefault = httpMock.expectOne('/api/admin/audit-grid');
    expect(supersededDefault.cancelled).toBe(true);
    const request = httpMock.expectOne((candidate) =>
      candidate.url === '/api/admin/audit-grid' &&
      candidate.params.get('q') === 'retention' &&
      candidate.params.get('severity') === 'critical' &&
      candidate.params.get('action') === 'file.export.failed' &&
      candidate.params.get('actor') === 'Audit Admin' &&
      candidate.params.get('entityType') === 'ExportJob' &&
      candidate.params.get('result') === 'failed' &&
      candidate.params.has('fromDate'));
    expect(request.request.params.keys().sort()).toEqual(
      ['action', 'actor', 'entityType', 'fromDate', 'q', 'result', 'severity'].sort(),
    );
    request.flush({ items: [], page: 1, pageSize: 100, totalCount: 0 });
    fixture.detectChanges();

    expect(queryAll(fixture, '[data-testid="filter-chip"]')).toHaveLength(7);
    expect(query(fixture, '[data-testid="audit-result-count"]')?.textContent).toContain('0 authorized results');
    expect(query(fixture, '[data-testid="audit-zero-results"]')).not.toBeNull();
    expect(textContent(fixture)).not.toContain('unauthorized response detail');
    httpMock.verify();
  });

  it('retries a relative time filter with the same concrete UTC boundary', async () => {
    const initialNow = Date.parse('2026-08-30T08:00:00.000Z');
    const now = vi.spyOn(Date, 'now').mockReturnValue(initialNow);
    const { fixture, httpMock } = await renderLiveAudit(createAuditRouteHarness({ range: '24h' }));
    expect(httpMock.expectOne('/api/admin/audit-grid').cancelled).toBe(true);
    const initial = httpMock.expectOne((request) =>
      request.url === '/api/admin/audit-grid' && request.params.has('fromDate'));
    const appliedFromDate = initial.request.params.get('fromDate');
    expect(appliedFromDate).toBe('2026-08-29T08:00:00.000Z');
    initial.flush({}, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();

    now.mockReturnValue(initialNow + 60 * 60 * 1000);
    TestBed.inject(AdminFacade).reloadAuditLog();
    const retry = httpMock.expectOne((request) => request.url === '/api/admin/audit-grid');
    expect(retry.request.params.get('fromDate')).toBe(appliedFromDate);
    retry.flush({ items: [], totalCount: 0 });
    httpMock.verify();
  });

  it('applies a filter, exposes a keyboard-removable canonical chip, and recovers from zero results', async () => {
    const routeHarness = createAuditRouteHarness();
    const { fixture, httpMock } = await renderLiveAudit(routeHarness);
    httpMock.expectOne('/api/admin/audit-grid').flush({ items: [], totalCount: 0 });
    fixture.detectChanges();

    const search = query<HTMLInputElement>(fixture, '[data-testid="audit-filter-search"]')!;
    search.value = 'blocked export';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    query<HTMLButtonElement>(fixture, '[data-testid="audit-apply-filters"]')?.click();
    const filtered = httpMock.expectOne((request) =>
      request.url === '/api/admin/audit-grid' && request.params.get('q') === 'blocked export');
    filtered.flush({ items: [], totalCount: 0 });
    fixture.detectChanges();

    const remove = query<HTMLButtonElement>(fixture, '[aria-label="Remove filter Search: blocked export"]');
    expect(remove).not.toBeNull();
    remove?.focus();
    remove?.click();
    const cleared = httpMock.expectOne((request) =>
      request.url === '/api/admin/audit-grid' && request.params.keys().length === 0);
    cleared.flush({ items: [], totalCount: 0 });
    fixture.detectChanges();

    expect(query(fixture, '[data-testid="audit-filter-chips"]')).toBeNull();
    expect(routeHarness.router.calls.length).toBeGreaterThanOrEqual(2);
    httpMock.verify();
  });

  it('redacted audit detail drawer does not show restricted fields', async () => {
    const fixture = await renderAudit(
      AUDIT_LOG_SCENARIOS.redactedDetailDrawer,
      createAuditRouteHarness('audit-sample-002'),
    );
    const text = textContent(fixture);

    expect(query(fixture, '[data-testid="audit-detail-drawer"]')).not.toBeNull();
    expect(text).toContain('Suppressed');
    expect(text).toContain('Redacted');
    expect(text).not.toContain('restricted body must stay hidden');
    expect(text).not.toContain('tenant/private/key');
    expect(text).not.toContain('sample-target-001');
    expect(text).not.toContain('select hidden');
    expect(query(fixture, '[data-testid="audit-sensitive-metadata-toggle"]')).toBeNull();
  });

  it('severity and result are typed fields in the view model', () => {
    TestBed.configureTestingModule({
      providers: [{ provide: COGLATAS_ADMIN_AUDIT_MOCK, useValue: AUDIT_LOG_SCENARIOS.default }]
    });
    const facade = TestBed.inject(AdminFacade);
    const page = facade.getAuditLog();

    expect(page.rows[0].severity).toBe('info');
    expect(page.rows[1].result).toBe('denied');
    expect(page.typedFieldNote.metadataParsing).toBe('serverAuthorizedProgressiveDisclosure');
  });

  it('maps live audit grid result and severity from backend typed fields', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    const facade = TestBed.inject(AdminFacade);
    const httpMock = TestBed.inject(HttpTestingController);

    httpMock.expectOne('/api/admin/audit-grid').flush({
      items: [
        {
          id: 'audit-live-success',
          createdAt: '2026-07-02T09:14:00Z',
          action: 'workspace.member.view',
          actorDisplayName: 'Live Admin',
          targetType: 'WorkspaceMember',
          workspaceLabel: 'Live Workspace',
          severity: 'info',
          result: 'success',
          summary: 'Member list opened.',
          requestId: 'req-live-success'
        },
        {
          id: 'audit-live-denied',
          createdAt: '2026-07-02T09:22:00Z',
          action: 'file.download.denied',
          actorDisplayName: 'Live Teacher',
          targetType: 'File',
          workspaceLabel: 'Live Workspace',
          severity: 'warning',
          result: 'denied',
          summary: 'Download blocked.',
          requestId: 'req-live-denied'
        },
        {
          id: 'audit-live-failed',
          createdAt: '2026-07-02T09:35:00Z',
          action: 'export.request.failed',
          actorDisplayName: 'Live Operator',
          targetType: 'ExportJob',
          workspaceLabel: null,
          severity: 'critical',
          result: 'failed',
          summary: 'Export failed.',
          requestId: null
        }
      ],
      totalCount: 12,
    });

    const rows = facade.getAuditLog().rows;

    expect(rows.map((row) => row.result)).toEqual(['success', 'denied', 'failed']);
    expect(rows.map((row) => row.severity)).toEqual(['info', 'warning', 'critical']);
    expect(rows[2].workspace).toBe('');
    expect(facade.getAuditLog().totalCount).toBe(12);
    httpMock.verify();
  });

  it('shows a neutral explicit label when live audit classifications are unexpected', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    const facade = TestBed.inject(AdminFacade);
    const httpMock = TestBed.inject(HttpTestingController);

    httpMock.expectOne('/api/admin/audit-grid').flush({
      items: [{
        id: 'audit-live-unclassified',
        createdAt: '2026-07-02T09:14:00Z',
        action: 'workspace.member.view',
        actorDisplayName: 'Live Admin',
        targetType: 'WorkspaceMember',
        workspaceLabel: 'Live Workspace',
        severity: 'unexpected-severity',
        result: 'unexpected-result',
        summary: 'Member list opened.',
        requestId: 'req-live-unclassified'
      }]
    });

    const row = facade.getAuditLog().rows[0];
    expect(row.severity).toBe('unclassified');
    expect(row.severityLabel).toBe('Unrecognized severity');
    expect(row.result).toBe('unclassified');
    expect(row.resultLabel).toBe('Unrecognized result');
    expect(row.severityLabel).not.toContain('unexpected-severity');
    expect(row.resultLabel).not.toContain('unexpected-result');
    httpMock.verify();
  });

  it('cancels list state and clears counts before an authorization invalidation can render a late response', () => {
    let clearProtectedState: (() => void) | undefined;
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: COGLATAS_AUTH_SESSION_MOCK, useValue: DEFAULT_AUTH_SESSION },
        {
          provide: RealtimeFacade,
          useValue: {
            registerProtectedStateClearer: (_owner: string, clear: () => void) => {
              clearProtectedState = clear;
              return () => undefined;
            },
          },
        },
      ],
    });
    const facade = TestBed.inject(AdminFacade);
    const request = TestBed.inject(HttpTestingController).expectOne('/api/admin/audit-grid');

    clearProtectedState?.();

    expect(request.cancelled).toBe(true);
    expect(facade.getAuditLog().status).toBe('permissionDenied');
    expect(facade.getAuditLog().rows).toEqual([]);
    expect(facade.getAuditLog().totalCount).toBe(0);
  });

  it('loads a selected live audit row from the redacted row endpoint only', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    const facade = TestBed.inject(AdminFacade);
    const httpMock = TestBed.inject(HttpTestingController);
    httpMock.expectOne('/api/admin/audit-grid').flush({ items: [] });

    facade.selectAuditDetail('audit-live-safe');
    httpMock.expectOne('/api/audit/capabilities').flush({
      canView: true,
      canReview: true,
      canApprove: false,
      canExport: false,
      canViewSensitiveMetadata: false,
    });
    httpMock.expectOne('/api/admin/audit-grid/audit-live-safe').flush({
      id: 'audit-live-safe',
      createdAt: '2026-08-25T08:00:00Z',
      action: 'audit.detail.read',
      actorDisplayName: 'Redacted actor',
      targetType: 'AuditLog',
      workspaceLabel: 'Workspace A',
      severity: 'info',
      result: 'success',
      summary: 'A safe audit summary.',
      requestId: null,
    });

    const detail = facade.getAuditDetail();
    expect(detail.status).toBe('ready');
    expect(detail.row?.redactedDetails).toEqual([]);
    httpMock.verify();
  });

  it('progressively discloses exact-event metadata only after capability and user action', async () => {
    const { fixture, httpMock } = await renderLiveAudit();
    httpMock.expectOne('/api/admin/audit-grid').flush({
      items: [{
        id: 'audit-live-sensitive',
        createdAt: '2026-08-25T08:00:00Z',
        action: 'audit.metadata.read',
        actorDisplayName: 'Authorized auditor',
        targetType: 'AuditLog',
        workspaceLabel: 'Workspace A',
        severity: 'info',
        result: 'success',
        summary: 'A safe audit summary.',
        requestId: 'request-safe',
      }],
    });
    fixture.detectChanges();

    query<HTMLButtonElement>(fixture, '[data-testid="open-audit-detail"]')?.click();
    httpMock.expectOne('/api/audit/capabilities').flush({ canViewSensitiveMetadata: true });
    httpMock.expectOne('/api/admin/audit-grid/audit-live-sensitive').flush({
      id: 'audit-live-sensitive',
      createdAt: '2026-08-25T08:00:00Z',
      action: 'audit.metadata.read',
      actorDisplayName: 'Authorized auditor',
      targetType: 'AuditLog',
      workspaceLabel: 'Workspace A',
      severity: 'info',
      result: 'success',
      summary: 'A safe audit summary.',
      requestId: 'request-safe',
    });
    fixture.detectChanges();

    const toggle = query<HTMLButtonElement>(fixture, '[data-testid="audit-sensitive-metadata-toggle"]');
    expect(toggle?.textContent?.trim()).toBe('Show sensitive metadata');
    httpMock.expectNone('/api/admin/audit-grid/audit-live-sensitive/sensitive-metadata');
    toggle?.focus();
    toggle?.click();
    fixture.detectChanges();
    expect(toggle?.textContent?.trim()).toBe('Hide sensitive metadata');
    expect(document.activeElement).toBe(toggle);

    httpMock.expectOne('/api/admin/audit-grid/audit-live-sensitive/sensitive-metadata').flush({
      auditId: 'audit-live-sensitive',
      metadata: {
        outcome: 'Allowed',
        change: { category: '<img src=x onerror=alert(1)>' },
      },
      redactionApplied: true,
    });
    fixture.detectChanges();

    const metadata = query<HTMLElement>(fixture, '[data-testid="audit-sensitive-metadata-json"]');
    expect(metadata?.textContent).toContain('Allowed');
    expect(metadata?.textContent).toContain('<img src=x onerror=alert(1)>');
    expect(query(fixture, '.admin-drawer__metadata img')).toBeNull();
    expect(query(fixture, '[data-testid="audit-sensitive-metadata-redacted"]')?.textContent)
      .toContain('Prohibited fields were removed by the server.');
    expect(document.activeElement).toBe(toggle);

    toggle?.click();
    fixture.detectChanges();
    expect(query(fixture, '[data-testid="audit-sensitive-metadata-json"]')).toBeNull();
    expect(toggle?.textContent?.trim()).toBe('Show sensitive metadata');
    expect(document.activeElement).toBe(toggle);
    httpMock.verify();
  });

  it('cancels an exact-event metadata request and ignores stale state when selection changes', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const facade = TestBed.inject(AdminFacade);
    const httpMock = TestBed.inject(HttpTestingController);
    httpMock.expectOne('/api/admin/audit-grid').flush({ items: [] });

    facade.selectAuditDetail('audit-first');
    httpMock.expectOne('/api/audit/capabilities').flush({ canViewSensitiveMetadata: true });
    httpMock.expectOne('/api/admin/audit-grid/audit-first').flush({
      id: 'audit-first',
      createdAt: '2026-08-25T08:00:00Z',
      action: 'first',
      actorDisplayName: 'Auditor',
      targetType: 'AuditLog',
      severity: 'info',
      result: 'success',
      summary: 'First event.',
    });
    facade.revealAuditSensitiveMetadata('audit-first');
    const firstMetadata = httpMock.expectOne('/api/admin/audit-grid/audit-first/sensitive-metadata');

    facade.selectAuditDetail('audit-second');
    expect(firstMetadata.cancelled).toBe(true);
    httpMock.expectOne('/api/audit/capabilities').flush({ canViewSensitiveMetadata: true });
    httpMock.expectOne('/api/admin/audit-grid/audit-second').flush({
      id: 'audit-second',
      createdAt: '2026-08-25T08:01:00Z',
      action: 'second',
      actorDisplayName: 'Auditor',
      targetType: 'AuditLog',
      severity: 'info',
      result: 'success',
      summary: 'Second event.',
    });
    facade.revealAuditSensitiveMetadata('audit-second');
    httpMock.expectOne('/api/admin/audit-grid/audit-second/sensitive-metadata').flush({
      auditId: 'audit-second',
      metadata: { outcome: 'SecondOnly' },
      redactionApplied: false,
    });

    expect(facade.getAuditSensitiveMetadata().auditId).toBe('audit-second');
    expect(facade.getAuditSensitiveMetadata().formattedJson).toContain('SecondOnly');
    httpMock.verify();
  });

  it('keeps audit selection in URL state, supports route back/forward, and restores the activation focus', async () => {
    const routeHarness = createAuditRouteHarness();
    const fixture = await renderAudit(AUDIT_LOG_SCENARIOS.default, routeHarness);
    const activation = query<HTMLButtonElement>(fixture, '[data-testid="activate-audit-row"]');

    activation?.click();
    fixture.detectChanges();
    expect(query(fixture, '[data-testid="audit-detail-drawer"]')).not.toBeNull();
    expect(routeHarness.router.calls[0]?.[1]).toEqual({
      relativeTo: expect.anything(),
      queryParams: { event: 'audit-sample-001' },
      queryParamsHandling: 'merge',
      replaceUrl: false,
    });

    routeHarness.queryParamMap.next(convertToParamMap({ event: 'audit-sample-001' }));
    await settleAuditRender(fixture);
    routeHarness.queryParamMap.next(convertToParamMap({}));
    await settleAuditRender(fixture);
    expect(query(fixture, '[data-testid="audit-detail-drawer"]')).toBeNull();
    expect(document.activeElement).toBe(activation);

    routeHarness.queryParamMap.next(convertToParamMap({ event: 'audit-sample-001' }));
    await settleAuditRender(fixture);
    expect(query(fixture, '[data-testid="audit-detail-drawer"]')).not.toBeNull();

    query<HTMLButtonElement>(fixture, '[data-testid="audit-detail-close"]')?.click();
    await settleAuditRender(fixture);
    expect(document.activeElement).toBe(activation);
    expect(routeHarness.router.calls.at(-1)?.[1]).toEqual({
      relativeTo: expect.anything(),
      queryParams: { event: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  });

  it('uses the audit heading as a safe focus fallback for a direct URL selection', async () => {
    const fixture = await renderAudit(
      AUDIT_LOG_SCENARIOS.default,
      createAuditRouteHarness('audit-sample-001'),
    );

    query<HTMLButtonElement>(fixture, '[data-testid="audit-detail-close"]')?.click();
    await settleAuditRender(fixture);

    expect(document.activeElement).toBe(query(fixture, '[data-testid="audit-log-title"]'));
  });

  it('uses a same-row focusable replacement when a history return trigger was virtualized away', async () => {
    const routeHarness = createAuditRouteHarness();
    const fixture = await renderAudit(AUDIT_LOG_SCENARIOS.default, routeHarness);
    const activation = query<HTMLButtonElement>(fixture, '[data-testid="activate-audit-row"]');
    const replacement = query<HTMLButtonElement>(fixture, '[data-testid="open-audit-detail"]');

    activation?.click();
    activation?.remove();
    routeHarness.queryParamMap.next(convertToParamMap({ event: 'audit-sample-001' }));
    await settleAuditRender(fixture);
    routeHarness.queryParamMap.next(convertToParamMap({}));
    await settleAuditRender(fixture);

    expect(document.activeElement).toBe(replacement);
  });

  it('uses the audit heading when no same-row virtualized focus target remains', async () => {
    const routeHarness = createAuditRouteHarness();
    const fixture = await renderAudit(AUDIT_LOG_SCENARIOS.default, routeHarness);
    const activation = query<HTMLButtonElement>(fixture, '[data-testid="activate-audit-row"]');

    activation?.click();
    for (const target of queryAll(fixture, '[data-grid-row-id="audit-sample-001"]')) {
      target.remove();
    }
    routeHarness.queryParamMap.next(convertToParamMap({ event: 'audit-sample-001' }));
    await settleAuditRender(fixture);
    routeHarness.queryParamMap.next(convertToParamMap({}));
    await settleAuditRender(fixture);

    expect(document.activeElement).toBe(query(fixture, '[data-testid="audit-log-title"]'));
  });

  it('uses the audit heading instead of a connected recycled grid trigger', async () => {
    const fixture = await renderAudit(AUDIT_LOG_SCENARIOS.default, createAuditRouteHarness());
    const activation = query<HTMLButtonElement>(fixture, '[data-testid="activate-audit-row"]');

    activation?.click();
    fixture.detectChanges();
    for (const target of queryAll(fixture, '[data-grid-row-id="audit-sample-001"]')) {
      target.dataset['gridRowId'] = 'audit-sample-002';
    }

    query<HTMLButtonElement>(fixture, '[data-testid="audit-detail-close"]')?.click();
    await settleAuditRender(fixture);

    expect(document.activeElement).toBe(query(fixture, '[data-testid="audit-log-title"]'));
  });

  it('restores shell and bounded-grid scroll positions before returning activation focus', async () => {
    const scrollHost = document.createElement('main');
    scrollHost.id = 'app-shell-main-content';
    scrollHost.scrollLeft = 24;
    scrollHost.scrollTop = 312;
    Object.defineProperty(scrollHost, 'scrollHeight', { configurable: true, value: 1200 });
    Object.defineProperty(scrollHost, 'clientHeight', { configurable: true, value: 400 });
    Object.defineProperty(scrollHost, 'scrollWidth', { configurable: true, value: 1800 });
    Object.defineProperty(scrollHost, 'clientWidth', { configurable: true, value: 720 });
    Object.defineProperty(scrollHost, 'scrollTo', {
      configurable: true,
      value: (options: ScrollToOptions) => {
        scrollHost.scrollLeft = Number(options.left ?? 0);
        scrollHost.scrollTop = Number(options.top ?? 0);
      },
    });

    const gridViewport = document.createElement('div');
    gridViewport.className = 'ag-center-cols-viewport';
    gridViewport.scrollLeft = 36;
    gridViewport.scrollTop = 184;
    Object.defineProperty(gridViewport, 'scrollHeight', { configurable: true, value: 2400 });
    Object.defineProperty(gridViewport, 'clientHeight', { configurable: true, value: 360 });
    Object.defineProperty(gridViewport, 'scrollWidth', { configurable: true, value: 1600 });
    Object.defineProperty(gridViewport, 'clientWidth', { configurable: true, value: 560 });
    Object.defineProperty(gridViewport, 'scrollTo', {
      configurable: true,
      value: (options: ScrollToOptions) => {
        gridViewport.scrollLeft = Number(options.left ?? 0);
        gridViewport.scrollTop = Number(options.top ?? 0);
      },
    });
    scrollHost.append(gridViewport);
    document.body.append(scrollHost);

    try {
      const fixture = await renderAudit(AUDIT_LOG_SCENARIOS.default, createAuditRouteHarness());
      gridViewport.append(fixture.nativeElement);
      const activation = query<HTMLButtonElement>(fixture, '[data-testid="activate-audit-row"]');
      activation?.click();
      fixture.detectChanges();
      scrollHost.scrollLeft = 0;
      scrollHost.scrollTop = 0;
      gridViewport.scrollLeft = 0;
      gridViewport.scrollTop = 0;
      query<HTMLButtonElement>(fixture, '[data-testid="audit-detail-close"]')?.click();
      await settleAuditRender(fixture);

      expect(scrollHost.scrollLeft).toBe(24);
      expect(scrollHost.scrollTop).toBe(312);
      expect(gridViewport.scrollLeft).toBe(36);
      expect(gridViewport.scrollTop).toBe(184);
      expect(document.activeElement).toBe(activation);
    } finally {
      scrollHost.remove();
    }
  });

  it('uses the same URL and focus policy when Escape closes the drawer', async () => {
    const routeHarness = createAuditRouteHarness();
    const fixture = await renderAudit(AUDIT_LOG_SCENARIOS.default, routeHarness);
    const activation = query<HTMLButtonElement>(fixture, '[data-testid="activate-audit-row"]');

    activation?.click();
    fixture.detectChanges();
    query(fixture, 'app-audit-detail-drawer')?.dispatchEvent(
      new KeyboardEvent('keydown', { bubbles: true, key: 'Escape' }),
    );
    await settleAuditRender(fixture);

    expect(document.activeElement).toBe(activation);
    expect(routeHarness.router.calls.at(-1)?.[1]).toEqual({
      relativeTo: expect.anything(),
      queryParams: { event: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  });

  it('keeps density and optional columns local to the audit grid', async () => {
    const fixture = await renderAudit();
    const dense = query<HTMLButtonElement>(fixture, '[data-testid="audit-density-dense"]');
    const workspace = query<HTMLInputElement>(fixture, '[data-testid="audit-column-workspace"]');

    expect(query(fixture, '[data-testid="stub-sticky-header"]')?.textContent).toContain('true');
    expect(query(fixture, '[data-testid="stub-row-height"]')?.textContent).toContain('48');
    dense?.click();
    workspace?.click();
    fixture.detectChanges();

    expect(dense?.getAttribute('aria-pressed')).toBe('true');
    expect(query(fixture, '[data-testid="stub-row-height"]')?.textContent).toContain('36');
    expect(queryAll(fixture, '[data-testid="grid-column"]').map((element) => element.textContent?.trim())).toContain('workspace');
  });

  it('shows export diagnostics as unavailable without a request button or local rows', async () => {
    const fixture = await renderExport(EXPORT_DIAGNOSTICS_SCENARIOS.notAllowed);

    expect(query(fixture, '[data-testid="export-request-action"]')).toBeNull();
    expect(query(fixture, '[data-testid="export-request-disabled"]')).toBeNull();
    expect(query(fixture, '[data-testid="app-data-grid"]')).toBeNull();
    expect(queryAll(fixture, '[data-testid="export-row"]').length).toBe(0);
    expect(textContent(fixture)).toContain('Not available in MVP0');
    expect(textContent(fixture)).not.toContain('req-export');
  });

  it('does not expose Excel export API or visible-grid export actions', async () => {
    const audit = await renderAudit();
    TestBed.resetTestingModule();
    const exportDiagnostics = await renderExport();
    const combinedText = `${textContent(audit)} ${textContent(exportDiagnostics)}`;

    expect(combinedText).not.toContain('exportDataAsExcel');
    expect(combinedText).not.toContain('Excel');
    expect(combinedText).not.toContain('Download visible grid');
  });

  it('uses bounded page size with maximum 100', async () => {
    const fixture = await renderAudit(AUDIT_LOG_SCENARIOS.manyRowsBoundedPage);

    expect(query(fixture, '[data-testid="stub-page-size"]')?.textContent).toContain('50/100');
    expect(clampAppDataGridPageSize(250, 500)).toBe(100);
  }, 15_000);

  it('renders requestId as text without creating markup', async () => {
    const fixture = await renderAudit();

    expect(textContent(fixture)).toContain('req-safe-<audit-003>');
    expect(query(fixture, 'script')).toBeNull();
  });

  it('mobile layout does not expose hidden admin actions', async () => {
    const fixture = await renderExport(EXPORT_DIAGNOSTICS_SCENARIOS.notAllowed);

    (fixture.nativeElement as HTMLElement).style.width = '320px';
    fixture.detectChanges();

    expect(query(fixture, '[data-testid="export-request-action"]')).toBeNull();
  });
});
