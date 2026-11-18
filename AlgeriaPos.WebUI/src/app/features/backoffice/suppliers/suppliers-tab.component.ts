import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { localToday } from '../../../core/identity';
import { ConfirmService } from '../../../ui/confirm.service';
import { MENU } from '../../../ui/menu.component';
import { PAGE_CHROME } from '../shell/page-chrome';
import { deleteRequest } from '../../../ui/confirm-requests';
import { FoldComponent } from '../../../ui/fold.component';
import { TouchedFields } from '../backoffice-forms';
import { formatDay } from '../backoffice-format';
import { CustomerOpeningBalanceComponent } from '../customer-opening-balance.component';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { TableStateComponent } from '../../../ui/table-state.component';
import {
  OpeningParty,
  emptyOpeningDraft,
  openingDraftProblem,
  openingPayload,
} from '../../../core/opening-balance';
import { SalesPagesApi, SupplierStatementHead } from '../sales-pages.api';
import {
  SupplierDto,
  SupplierStatementRow,
  SuppliersApiService,
  UpsertSupplierRequest,
} from './suppliers-api.service';

interface SupplierForm {
  name: string;
  phone: string;
  address: string;
  note: string;
}

@Component({
  selector: 'app-backoffice-suppliers',
  standalone: true,
  imports: [
    FormsModule,
    FoldComponent,
    CustomerOpeningBalanceComponent,
    DateInputComponent,
    ListFooterComponent,
    TableStateComponent,
    ...PAGE_CHROME,
    ...MENU,
  ],
  template: `
    <div class="bo-stack">
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }
      @if (notice()) {
        <p class="notice ok" role="status">{{ notice() }}</p>
      }

      @if (editorOpen()) {
        <div class="bo-backdrop" (click)="cancelEdit()"></div>
      }
      <section
        class="bo-panel"
        [class.bo-edit-modal]="editorOpen()"
        [class.bo-editor-hidden]="!editorOpen()"
        [attr.role]="editorOpen() ? 'dialog' : null"
        [attr.aria-modal]="editorOpen() ? 'true' : null"
        [attr.aria-label]="editorOpen() ? (editingId() ? i18n.t('common.edit') : i18n.t('sup.new')) : null"
        (keydown.escape)="editorOpen() && cancelEdit()"
      >
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ editingId() ? i18n.t('common.edit') : i18n.t('sup.new') }}</h2>
        </div>
        <div class="bo-panel__body bo-form">
          <app-fold class="bo-group" [heading]="i18n.t('sup.group.identity')" [open]="true">
            <div class="bo-fields">
              <div class="bo-field" [class.is-invalid]="touched.missing('name', form.name)">
                <label for="sup-name">
                  {{ i18n.t('sup.name') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <input
                  id="sup-name"
                  [(ngModel)]="form.name"
                  [attr.aria-invalid]="touched.missing('name', form.name) ? 'true' : null"
                  [attr.aria-describedby]="touched.missing('name', form.name) ? 'sup-name-error' : null"
                  (blur)="touched.touch('name')"
                />
                @if (touched.missing('name', form.name)) {
                  <span id="sup-name-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                }
              </div>
              <div class="bo-field">
                <label for="sup-phone">
                  {{ i18n.t('sup.phone') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                </label>
                <input id="sup-phone" class="mono" dir="ltr" [(ngModel)]="form.phone" />
              </div>
              <div class="bo-field bo-field--wide">
                <label for="sup-address">
                  {{ i18n.t('sup.address') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                </label>
                <input id="sup-address" [(ngModel)]="form.address" />
              </div>
            </div>
          </app-fold>

          <app-fold class="bo-group" [heading]="i18n.t('sup.group.fiscalIds')">
            <div class="bo-fields">
              @for (id of fiscalIdDefs(); track id.key) {
                <div class="bo-field">
                  <label [for]="'sup-fid-' + id.key">
                    {{ i18n.text(id.labels) || id.code }}
                    <span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <input
                    [id]="'sup-fid-' + id.key"
                    class="mono"
                    dir="ltr"
                    maxlength="40"
                    [ngModel]="fiscalIds[id.key] ?? ''"
                    (ngModelChange)="fiscalIds[id.key] = $event"
                  />
                </div>
              }
              <div class="bo-field bo-field--wide">
                <label for="sup-note">
                  {{ i18n.t('sup.note') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                </label>
                <textarea id="sup-note" rows="2" [(ngModel)]="form.note"></textarea>
              </div>
            </div>
          </app-fold>

          <app-fold class="bo-group" [heading]="i18n.t('cust.opening.title')" [open]="openingInvalid()">
            <app-customer-opening-balance
              kind="supplier"
              [party]="editing()"
              [(draft)]="openingDraft"
              (changed)="openingChanged($event)"
            />
          </app-fold>
        </div>
        <div class="bo-panel__foot">
          <button type="button" class="btn-ghost" (click)="cancelEdit()">{{ i18n.t('common.cancel') }}</button>
          <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">
            {{ i18n.t('common.save') }}
          </button>
        </div>
      </section>

      @if (statementSupplier(); as sup) {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <div>
              <h2 class="bo-panel__title">{{ i18n.t('sup.statement.title', { name: sup.name }) }}</h2>
              <span class="mono bo-soft" dir="ltr">{{ sup.code }}</span>
            </div>
            <button type="button" class="btn-ghost bo-small" (click)="closeStatement()">
              {{ i18n.t('common.close') }}
            </button>
          </div>
          <div class="bo-panel__body">
            <div class="bo-fields">
              <div class="bo-field">
                <label for="stmt-from">{{ i18n.t('sup.statement.from') }}</label>
                <app-date-input id="stmt-from" [(ngModel)]="stmtFrom" />
              </div>
              <div class="bo-field">
                <label for="stmt-to">{{ i18n.t('sup.statement.to') }}</label>
                <app-date-input id="stmt-to" [(ngModel)]="stmtTo" />
              </div>
              <div class="bo-actions bo-actions--start">
                <button type="button" class="btn-ghost" [disabled]="stmtPager.loading()" (click)="loadStatement()">
                  {{ stmtPager.loading() ? i18n.t('common.loading') : i18n.t('sup.statement.load') }}
                </button>
              </div>
            </div>

            @if (stmtError()) {
              <p class="notice danger" role="alert">{{ stmtError() }}</p>
            }

            @if (statement(); as stmt) {
              @if (stmt.balanceDifference !== 0) {
                <p class="notice warn" role="alert">
                  {{ i18n.t('sup.statement.diffWarning', { diff: i18n.money(stmt.balanceDifference) }) }}
                </p>
              }

              <div class="bo-metrics">
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('sup.statement.opening') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(stmt.openingBalance) }}</strong>
                </div>
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('sup.statement.closing') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(stmt.closingBalance) }}</strong>
                </div>
              </div>

              <div class="bo-scroll bo-scroll--tall">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('sup.statement.date') }}</th>
                      <th>{{ i18n.t('sup.statement.kind') }}</th>
                      <th>{{ i18n.t('sup.statement.number') }}</th>
                      <th>{{ i18n.t('sup.statement.description') }}</th>
                      <th class="num">{{ i18n.t('sup.statement.debit') }}</th>
                      <th class="num">{{ i18n.t('sup.statement.credit') }}</th>
                      <th class="num">{{ i18n.t('sup.statement.balance') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    <!-- The opening line heads the first page, where the running balance starts from it. -->
                    @if (stmtPager.index() === 0) {
                      <tr>
                        <th scope="row">{{ i18n.t('sup.statement.opening') }}</th>
                        <td></td>
                        <td></td>
                        <td></td>
                        <td></td>
                        <td></td>
                        <td class="num mono bo-cell-strong">{{ i18n.money(stmt.openingBalance) }}</td>
                      </tr>
                    }
                    @for (row of stmtPager.rows(); track $index) {
                      <tr>
                        <td class="mono" [attr.data-label]="i18n.t('sup.statement.date')">{{ day(row.date) }}</td>
                        <td [attr.data-label]="i18n.t('sup.statement.kind')">{{ kindWord(row) }}</td>
                        <td class="mono" [attr.data-label]="i18n.t('sup.statement.number')">{{ row.documentNumber || '—' }}</td>
                        <td [attr.data-label]="i18n.t('sup.statement.description')">{{ row.description }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('sup.statement.debit')">
                          {{ row.debit ? i18n.money(row.debit) : '' }}
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('sup.statement.credit')">
                          {{ row.credit ? i18n.money(row.credit) : '' }}
                        </td>
                        <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('sup.statement.balance')">
                          {{ i18n.money(row.balance) }}
                        </td>
                      </tr>
                    } @empty {
                      <tr>
                        <td colspan="7" class="bo-empty">
                          <strong>{{ i18n.t('sup.statement.none') }}</strong>
                        </td>
                      </tr>
                    }
                  </tbody>
                  <tfoot>
                    <tr>
                      <th scope="row">{{ i18n.t('sup.statement.closing') }}</th>
                      <td></td>
                      <td></td>
                      <td></td>
                      <td class="num mono">{{ i18n.money(stmt.totalDebit) }}</td>
                      <td class="num mono">{{ i18n.money(stmt.totalCredit) }}</td>
                      <td class="num mono bo-cell-strong">{{ i18n.money(stmt.closingBalance) }}</td>
                    </tr>
                  </tfoot>
                </table>
              </div>
              <app-list-footer [pager]="stmtPager" [loading]="stmtPager.loading()" />
            }
          </div>
        </section>
      } @else {
        <!-- The list's chrome (Paradis, Batch 2): New in the page header, the search in the page toolbar. -->
        <ng-template appPageActions>
          <button type="button" class="btn-primary" (click)="openCreate()">
            <span aria-hidden="true">+</span> {{ i18n.t('sup.new') }}
          </button>
        </ng-template>
        <ng-template appPageToolbar>
          <div class="ui-toolbar" role="search">
            <div class="ui-filter ui-filter--search">
              <label for="sup-q">{{ i18n.t('sup.search') }}</label>
              <input
                id="sup-q"
                type="search"
                class="finder"
                [placeholder]="i18n.t('sup.search')"
                [ngModel]="query()"
                (ngModelChange)="query.set($event)"
              />
            </div>
            <div class="ui-filter">
              <label for="sup-f-balance">{{ i18n.t('sup.balance') }}</label>
              <select id="sup-f-balance" [ngModel]="balanceFilter()" (ngModelChange)="balanceFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="weOwe">{{ i18n.t('sup.filter.weOwe') }}</option>
                <option ngValue="owesUs">{{ i18n.t('sup.filter.owesUs') }}</option>
                <option ngValue="settled">{{ i18n.t('cust.filter.settled') }}</option>
              </select>
            </div>
            <div class="ui-filter">
              <label for="sup-f-missing">{{ i18n.t('cust.filter.missing') }}</label>
              <select id="sup-f-missing" [ngModel]="missingFilter()" (ngModelChange)="missingFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="phone">{{ i18n.t('cust.filter.noPhone') }}</option>
                <option ngValue="fiscalIds">{{ i18n.t('sup.filter.noFiscalIds') }}</option>
              </select>
            </div>
            @if (filtered()) {
              <div class="ui-toolbar__end">
                <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
              </div>
            }
          </div>
        </ng-template>
        <section class="ui-surface">
          <div class="bo-scroll bo-scroll--tall">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('sup.code') }}</th>
                  <th>{{ i18n.t('sup.name') }}</th>
                  <th>{{ i18n.t('sup.phone') }}</th>
                  <th class="num">{{ i18n.t('sup.balance') }}</th>
                  <th><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (s of pager.rows(); track s.id) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('sup.code')"><span dir="ltr">{{ s.code }}</span></td>
                    <td [attr.data-label]="i18n.t('sup.name')">
                      <button type="button" class="ui-link" (click)="edit(s)">{{ s.name }}</button>
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('sup.phone')"><span dir="ltr">{{ s.phone || '—' }}</span></td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('sup.balance')">
                      <span>{{ i18n.money(abs(s.balance)) }}</span>
                      @if (balanceWord(s.balance); as word) {
                        <span class="bo-soft"> {{ word }}</span>
                      }
                    </td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + s.name">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="openStatement(s)">{{ i18n.t('sup.statement.action') }}</button>
                        <button type="button" appMenuItem (click)="edit(s)">{{ i18n.t('common.edit') }}</button>
                        <hr appMenuSeparator />
                        <button type="button" appMenuItem class="is-danger" (click)="remove(s)">{{ i18n.t('common.delete') }}</button>
                      </app-menu>
                    </td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="5" class="bo-empty">
                      @if (!pager.loaded()) {
                        <strong>{{ i18n.t('common.loading') }}</strong>
                        <span class="bo-loading__rows" aria-hidden="true"></span>
                      } @else if (filtered()) {
                        <app-table-state kind="noResults" (clear)="clearFilters()" />
                      } @else {
                        <strong>{{ i18n.t('sup.none') }}</strong>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
        <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class BackofficeSuppliersComponent {
  private readonly suppliersApi = inject(SuppliersApiService);
  private readonly pages = inject(SalesPagesApi);
  private readonly regional = inject(RegionalService);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly editing = signal<SupplierDto | null>(null);
  readonly editingId = computed(() => this.editing()?.id ?? null);
  readonly editorOpen = signal(false);
  readonly query = signal('');

  readonly openingDraft = signal(emptyOpeningDraft(localToday()));
  readonly openingInvalid = computed(() => openingDraftProblem(this.openingDraft(), localToday()) !== null);

  readonly touched = new TouchedFields();
  form: SupplierForm = this.emptyForm();
  fiscalIds: Record<string, string> = {};

  readonly fiscalIdDefs = computed(() => this.regional.settings().fiscalIds ?? []);

  // Statement view state
  readonly statementSupplier = signal<SupplierDto | null>(null);
  /** The statement asked for (Show): its supplier and its days, both included. Null while none is open. */
  private readonly stmtQuery = signal<{ id: string; from: string; to: string } | null>(null);
  stmtFrom = this.defaultFrom();
  stmtTo = this.defaultTo();

  readonly abs = Math.abs;

  // The list's filters (owner, 2026-11-17), applied by the server over every supplier.
  readonly balanceFilter = signal<'all' | 'weOwe' | 'owesUs' | 'settled'>('all');
  readonly missingFilter = signal<'all' | 'phone' | 'fiscalIds'>('all');

  readonly filtered = computed(
    () => !!this.query().trim() || this.balanceFilter() !== 'all' || this.missingFilter() !== 'all',
  );

  /**
   * The suppliers a page at a time from the server (owner, 2026-11-17: "paginate in backend, not
   * frontend"): the search over code, name and phone and the filters run there; the balance signs
   * are the column's (above zero we owe them, below zero they owe us). A change goes back to page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.suppliersPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'suppliers.list',
      filters: () => ({ q: this.query().trim(), balance: this.balanceFilter(), missing: this.missingFilter() }),
    },
  );

  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  /**
   * The statement's lines a page at a time from the server: the running balance is computed there
   * over every line, and the head (opening, totals, closing) comes with each page. Paged only, never
   * filtered. Another supplier or other days start at page one.
   */
  readonly stmtPager = createServerPager(
    (page) => {
      const q = page.filters.query!;
      return this.pages.supplierStatementPage(q.id, { skip: page.skip, take: page.take, from: q.from, to: q.to });
    },
    {
      key: 'suppliers.statement',
      filters: () => ({ query: this.stmtQuery() }),
      enabled: () => !!this.stmtQuery(),
      debounceMs: 0,
    },
  );

  /** The statement's head, once the server has answered for the supplier asked for. */
  readonly statement = computed<SupplierStatementHead | null>(() => {
    const q = this.stmtQuery();
    const head = this.stmtPager.summary();
    return q && head && head.supplierId === q.id ? head : null;
  });

  readonly stmtError = computed(() => {
    const e = this.stmtQuery() ? this.stmtPager.error() : null;
    return e ? adminErrorMessage(e) : null;
  });

  clearFilters(): void {
    this.query.set('');
    this.balanceFilter.set('all');
    this.missingFilter.set('all');
  }

  private defaultFrom(): string {
    const today = localToday();
    return today.slice(0, 8) + '01';
  }

  private defaultTo(): string {
    return localToday();
  }

  private emptyForm(): SupplierForm {
    return { name: '', phone: '', address: '', note: '' };
  }

  /** Asks again for the page on screen, after a save or a delete. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  /** One supplier as the server now holds it, whatever page it is on. */
  private async freshRow(id: string): Promise<SupplierDto | null> {
    try {
      return (await this.pages.suppliersPage({ skip: 0, take: 1, id })).items[0] ?? null;
    } catch {
      return null;
    }
  }

  openCreate(): void {
    this.cancelEdit();
    this.editorOpen.set(true);
  }

  edit(s: SupplierDto): void {
    this.cancelEdit();
    this.editing.set(s);
    this.form = {
      name: s.name,
      phone: s.phone ?? '',
      address: s.address ?? '',
      note: s.note ?? '',
    };
    this.fiscalIds = { ...(s.fiscalIds ?? {}) };
    this.editorOpen.set(true);
  }

  cancelEdit(): void {
    this.editing.set(null);
    this.editorOpen.set(false);
    this.touched.reset();
    this.form = this.emptyForm();
    this.fiscalIds = {};
    this.openingDraft.set(emptyOpeningDraft(localToday()));
  }

  private cleanFiscalIds(): Record<string, string> {
    const result: Record<string, string> = {};
    for (const [k, v] of Object.entries(this.fiscalIds)) {
      const trimmed = v?.trim();
      if (trimmed) result[k] = trimmed;
    }
    return result;
  }

  async save(): Promise<void> {
    this.touched.touch('name');
    if (this.touched.missing('name', this.form.name)) return;
    if (!this.editingId() && this.openingInvalid()) return;

    this.busy.set(true);
    this.error.set(null);
    try {
      const fiscalIdsClean = this.cleanFiscalIds();
      const req: UpsertSupplierRequest = {
        name: this.form.name.trim(),
        phone: this.form.phone.trim() || null,
        address: this.form.address.trim() || null,
        // Always sent: an empty set clears what was stored; null would keep it.
        fiscalIds: fiscalIdsClean,
        note: this.form.note.trim() || null,
      };
      const id = this.editingId();
      if (id) {
        await this.suppliersApi.update(id, req);
      } else {
        req.openingBalance = openingPayload(this.openingDraft());
        await this.suppliersApi.create(req);
      }
      this.cancelEdit();
      await this.reload();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async openingChanged(fresh: OpeningParty | null): Promise<void> {
    await this.reload();
    const id = this.editingId();
    const row = (id ? await this.freshRow(id) : null) ?? (fresh as SupplierDto | null);
    if (row) this.editing.set(row);
    this.notice.set(this.i18n.t(fresh ? 'cust.opening.saved' : 'cust.opening.correction.saved'));
  }

  async remove(s: SupplierDto): Promise<void> {
    const title = this.i18n.t('sup.delete.title', { name: s.name || s.code });
    const msg = this.i18n.t('sup.delete.message');
    if (!(await this.confirm.ask(deleteRequest(this.i18n, title, msg)))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.suppliersApi.remove(s.id);
      if (this.editingId() === s.id) this.cancelEdit();
      if (this.statementSupplier()?.id === s.id) this.closeStatement();
      await this.reload();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  balanceWord(balance: number): string {
    if (balance > 0) return this.i18n.t('sup.weOwe');
    if (balance < 0) return this.i18n.t('sup.owesUs');
    return '';
  }

  async openStatement(s: SupplierDto): Promise<void> {
    this.statementSupplier.set(s);
    this.stmtFrom = this.defaultFrom();
    this.stmtTo = this.defaultTo();
    await this.loadStatement();
  }

  closeStatement(): void {
    this.statementSupplier.set(null);
    this.stmtQuery.set(null);
  }

  /**
   * Asks for the statement's first page. The days go out as the inputs hold them (`yyyy-MM-dd`,
   * both included); the server reads them in the company's zone.
   */
  async loadStatement(): Promise<void> {
    const sup = this.statementSupplier();
    if (!sup) return;
    this.stmtQuery.set({ id: sup.id, from: this.stmtFrom, to: this.stmtTo });
    this.stmtPager.reset();
    await this.stmtPager.reload();
  }

  day(value: string | null | undefined): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  kindWord(row: SupplierStatementRow): string {
    const map: Record<string, string> = {
      'receiving': 'sup.stmt.source.receiving',
      'payment-voucher': 'sup.stmt.source.payment-voucher',
      'supplier-opening': 'sup.stmt.source.supplier-opening',
      'supplier-opening-correction': 'sup.stmt.source.supplier-opening-correction',
    };
    const key = map[row.sourceType];
    return key ? this.i18n.t(key) : row.description || row.sourceType;
  }
}
