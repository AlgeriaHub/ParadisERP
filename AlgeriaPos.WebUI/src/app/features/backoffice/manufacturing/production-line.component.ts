import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { RegionalService } from '../../../core/regional.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest, saveRequest } from '../../../ui/confirm-requests';
import { DateInputComponent } from '../../../ui/date-input.component';
import { DialogComponent } from '../../../ui/dialog.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createPager } from '../../../ui/pager';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatDay } from '../backoffice-format';
import { PageChrome } from '../shell/page-chrome';
import { LineEditorComponent } from './line-editor.component';
import {
  JobRoleDto,
  LineEmployeeCandidate,
  LineEmployeeDto,
  LineMemberRole,
  LineStockRow,
  LineUserCandidate,
  ManufacturingApiService,
  MaterialDto,
  PRODUCED_ROLES,
  ProductionLineDto,
} from './manufacturing-api.service';
import { ChoicesPageSummary, ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError, qty } from './manufacturing-shared';

/**
 * One production line (`manufacturing/lines/<id>`): what it is and where it stands, the employees
 * working on it — each still paid by their own branch — the products it makes, the logins named on it
 * and the stock its warehouse holds. A working panel, never folded. What the reader may change comes
 * from the server (`canEdit`, `canAssign`); a line out of their reach answers "not found".
 */
@Component({
  selector: 'app-mfg-line',
  standalone: true,
  imports: [
    FormsModule, ...MENU, DateInputComponent, DialogComponent, LineEditorComponent, ListFooterComponent, StatusComponent, TableStateComponent,
  ],
  template: `
    <div class="bo-stack">
      @if (error() || stockError()) {
        <p class="notice danger" role="alert">{{ error() || stockError() }}</p>
      }
      @if (notice()) {
        <p class="notice ok" role="status">{{ notice() }}</p>
      }

      @if (!line() && busy()) {
        <section class="ui-surface"><app-table-state kind="loading" /></section>
      } @else if (line(); as d) {
        <!-- ---------- the line ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.line.overview') }}</h2>
            <div class="head-actions">
              <button type="button" class="btn-ghost" (click)="openRecipes.emit(d.line.id)">{{ i18n.t('mfg.line.recipes') }}</button>
              @if (d.line.canEdit) {
                <button type="button" class="btn-ghost" (click)="editor.open(d.line)">{{ i18n.t('common.edit') }}</button>
              }
            </div>
          </div>
          <div class="bo-panel__body">
            <dl class="facts">
              <div><dt>{{ i18n.t('mfg.common.code') }}</dt><dd class="mono" dir="ltr">{{ d.line.code }}</dd></div>
              <div><dt>{{ i18n.t('field.nameAr') }}</dt><dd dir="rtl">{{ d.line.nameAr }}</dd></div>
              <div><dt>{{ i18n.t('field.nameFr') }}</dt><dd dir="ltr">{{ d.line.nameFr }}</dd></div>
              <div>
                <dt>{{ i18n.t('mfg.lines.location') }}</dt>
                <dd><span class="mono" dir="ltr">{{ d.line.locationCode }}</span> {{ i18n.lang() === 'ar' ? d.line.locationNameAr : d.line.locationNameFr }}</dd>
              </div>
              <div><dt>{{ i18n.t('mfg.lines.manager') }}</dt><dd>{{ d.line.managerName || '—' }}</dd></div>
              <div>
                <dt>{{ i18n.t('common.state') }}</dt>
                <dd><app-status [tone]="activeTone(d.line.isActive)">{{ i18n.t(d.line.isActive ? 'common.active' : 'common.inactive') }}</app-status></dd>
              </div>
              @if (d.line.description) {
                <div class="facts__wide"><dt>{{ i18n.t('mfg.common.description') }}</dt><dd>{{ d.line.description }}</dd></div>
              }
            </dl>
          </div>
        </section>

        <!-- ---------- employees ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.line.employees') }}</h2>
            @if (d.line.canAssign) {
              <button type="button" class="btn-primary" (click)="openAssign()">+ {{ i18n.t('mfg.line.assign') }}</button>
            }
          </div>
          <p class="bo-hint pad">{{ i18n.t('mfg.line.employeesHint') }}</p>
          @if (!d.employees.length) {
            <app-table-state kind="empty" [message]="i18n.t('mfg.line.noEmployees')" />
          } @else {
            <div class="ui-toolbar pane-bar" role="search">
              <div class="ui-filter">
                <label for="le-state">{{ i18n.t('common.state') }}</label>
                <select id="le-state" [ngModel]="employeeState()" (ngModelChange)="employeeState.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="working">{{ i18n.t('mfg.line.working') }}</option>
                  <option ngValue="ended">{{ i18n.t('mfg.filter.ended') }}</option>
                </select>
              </div>
              @if (payrollBranches().length > 1 || employeeBranch() !== 'all') {
                <div class="ui-filter">
                  <label for="le-branch">{{ i18n.t('mfg.line.payrollBranch') }}</label>
                  <select id="le-branch" [ngModel]="employeeBranch()" (ngModelChange)="employeeBranch.set($event)">
                    <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                    @for (b of payrollBranches(); track b.id) {
                      <option [ngValue]="b.id">{{ b.code }} — {{ b.name }}</option>
                    }
                  </select>
                </div>
              }
              @if (employeesFiltered()) {
                <div class="ui-toolbar__end">
                  <button type="button" class="btn-ghost" (click)="clearEmployeeFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                </div>
              }
            </div>
            @if (!shownEmployees().length) {
              <app-table-state kind="noResults" (clear)="clearEmployeeFilters()" />
            } @else {
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('mfg.line.employee') }}</th>
                      <th>{{ i18n.t('mfg.line.payrollBranch') }}</th>
                      <th>{{ i18n.t('mfg.line.jobRole') }}</th>
                      <th>{{ i18n.t('mfg.line.from') }}</th>
                      <th>{{ i18n.t('common.state') }}</th>
                      @if (d.line.canAssign) {
                        <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                      }
                    </tr>
                  </thead>
                  <tbody>
                    @for (e of employeePager.rows(); track e.id) {
                      <tr [class.is-muted]="!!e.endedOn">
                        <td [attr.data-label]="i18n.t('mfg.line.employee')">
                          {{ e.fullName }} <span class="ui-sub mono" dir="ltr">{{ e.number }}</span>
                          @if (e.jobTitle) {
                            <span class="ui-sub">{{ e.jobTitle }}</span>
                          }
                        </td>
                        <td [attr.data-label]="i18n.t('mfg.line.payrollBranch')">
                          <span class="mono" dir="ltr">{{ e.payrollBranchCode }}</span> {{ i18n.lang() === 'ar' ? e.payrollBranchNameAr : e.payrollBranchNameFr }}
                        </td>
                        <td class="mono" [attr.data-label]="i18n.t('mfg.line.jobRole')">{{ e.jobRoleCode || '—' }}</td>
                        <td class="mono" [attr.data-label]="i18n.t('mfg.line.from')">{{ day(e.assignedOn) }}</td>
                        <td [attr.data-label]="i18n.t('common.state')">
                          <app-status [tone]="assignmentTone(e)">
                            {{ e.endedOn ? i18n.t('mfg.line.endedOn', { day: day(e.endedOn) }) : i18n.t('mfg.line.working') }}
                          </app-status>
                        </td>
                        @if (d.line.canAssign) {
                          <td class="ui-cell-menu">
                            @if (!e.endedOn) {
                              <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + e.fullName">
                                <span menuTrigger aria-hidden="true">⋯</span>
                                <button type="button" appMenuItem class="is-danger" (click)="openEnd(e)">{{ i18n.t('mfg.line.end') }}</button>
                              </app-menu>
                            }
                          </td>
                        }
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              <app-list-footer class="pane-foot" [pager]="employeePager" />
            }
          }
        </section>

        <!-- ---------- products ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.line.products') }}</h2>
            @if (d.line.canAssign) {
              <button type="button" class="btn-ghost" (click)="openProducts()">{{ i18n.t('mfg.line.editProducts') }}</button>
            }
          </div>
          @if (!d.products.length) {
            <app-table-state kind="empty" [message]="i18n.t('mfg.line.noProducts')" />
          } @else {
            <ul class="chips">
              @for (p of d.products; track p.productId) {
                <li><span class="mono" dir="ltr">{{ p.sku }}</span> {{ name(p) }} <span class="ui-sub">{{ i18n.t('mfg.role.' + p.role) }}</span></li>
              }
            </ul>
          }
        </section>

        <!-- ---------- members ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.line.members') }}</h2>
            @if (d.line.canEdit) {
              <button type="button" class="btn-ghost" (click)="openMembers()">{{ i18n.t('mfg.line.editMembers') }}</button>
            }
          </div>
          <p class="bo-hint pad">{{ i18n.t('mfg.line.membersHint') }}</p>
          @if (!d.members.length) {
            <app-table-state kind="empty" [message]="i18n.t('mfg.line.noMembers')" />
          } @else {
            <ul class="chips">
              @for (m of d.members; track m.userId) {
                <li>{{ m.fullName }} <span class="ui-sub mono" dir="ltr">{{ m.username }}</span> · {{ i18n.t('mfg.member.' + m.role) }}</li>
              }
            </ul>
          }
        </section>

        <!-- ---------- stock ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.line.stock') }}</h2>
            <button type="button" class="btn-ghost" [disabled]="stockPager.loading()" (click)="loadStock()">{{ i18n.t('subscription.refresh') }}</button>
          </div>
          <p class="bo-hint pad">{{ i18n.t('mfg.line.stockHint') }}</p>
          @if (!stockPager.loaded() && !stockPager.error()) {
            <app-table-state kind="loading" />
          } @else if (!stockPager.summary()?.all) {
            <app-table-state kind="empty" [message]="i18n.t('mfg.line.noStock')" />
          } @else {
            <div class="ui-toolbar pane-bar" role="search">
              <div class="ui-filter">
                <label for="ls-role">{{ i18n.t('mfg.materials.role') }}</label>
                <select id="ls-role" [ngModel]="stockRole()" (ngModelChange)="stockRole.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  @for (r of stockRoles(); track r) {
                    <option [ngValue]="r">{{ i18n.t('mfg.role.' + r) }}</option>
                  }
                </select>
              </div>
              @if (stockRole() !== 'all') {
                <div class="ui-toolbar__end">
                  <button type="button" class="btn-ghost" (click)="stockRole.set('all')">{{ i18n.t('ui.state.clearFilters') }}</button>
                </div>
              }
            </div>
            @if (!stockPager.total()) {
              <app-table-state kind="noResults" (clear)="stockRole.set('all')" />
            } @else {
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('backoffice.products.sku') }}</th>
                      <th>{{ i18n.t('receipt.item') }}</th>
                      <th>{{ i18n.t('mfg.materials.role') }}</th>
                      <th class="num">{{ i18n.t('mfg.line.available') }}</th>
                      <th class="num">{{ i18n.t('mfg.line.maintenance') }}</th>
                      <th class="num">{{ i18n.t('mfg.line.scrap') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (s of stockPager.rows(); track s.productId) {
                      <tr>
                        <td class="mono" [attr.data-label]="i18n.t('backoffice.products.sku')"><span dir="ltr">{{ s.sku }}</span></td>
                        <td [attr.data-label]="i18n.t('receipt.item')">{{ name(s) }}</td>
                        <td [attr.data-label]="i18n.t('mfg.materials.role')">{{ i18n.t('mfg.role.' + s.role) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.line.available')"><strong>{{ qty(s.available) }}</strong> <span class="ui-sub" dir="ltr">{{ s.unit }}</span></td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.line.maintenance')">{{ qty(s.maintenance) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.line.scrap')">{{ qty(s.scrap) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              <app-list-footer class="pane-foot" [pager]="stockPager" [loading]="!stockPager.loaded()" />
            }
          }
        </section>
      }
    </div>

    <app-mfg-line-editor #editor (saved)="reload()" />

    <!-- Assign an employee -->
    <app-dialog [open]="assignOpen()" size="md" [heading]="i18n.t('mfg.line.assign')" (dismiss)="assignOpen.set(false)">
      @if (assignOpen()) {
        <div class="editor bo-form">
          @if (dialogError()) {
            <p class="notice danger" role="alert">{{ dialogError() }}</p>
          }
          <div class="bo-field">
            <label for="as-emp">{{ i18n.t('mfg.line.employee') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <select id="as-emp" [(ngModel)]="assignEmployee">
              <option value="" disabled>{{ i18n.t('mfg.line.chooseEmployee') }}</option>
              @for (c of freeCandidates(); track c.id) {
                <option [ngValue]="c.id">{{ c.fullName }} ({{ c.number }}) — {{ c.branchCode }}</option>
              }
            </select>
            <p class="bo-hint">{{ i18n.t('mfg.line.candidatesHint') }}</p>
          </div>
          <div class="bo-field">
            <label for="as-role">{{ i18n.t('mfg.line.jobRole') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
            <select id="as-role" [(ngModel)]="assignRole">
              <option [ngValue]="null">—</option>
              @for (r of activeRoles(); track r.id) {
                <option [ngValue]="r.id">{{ r.code }} — {{ name(r) }}</option>
              }
            </select>
          </div>
          <div class="bo-field">
            <label for="as-day">{{ i18n.t('mfg.line.from') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
            <app-date-input id="as-day" [(ngModel)]="assignDay" />
            <p class="bo-hint">{{ i18n.t('mfg.line.todayIfEmpty') }}</p>
          </div>
          <div class="bo-field">
            <label for="as-note">{{ i18n.t('mfg.common.note') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
            <input id="as-note" maxlength="400" [(ngModel)]="assignNote" />
          </div>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="assignOpen.set(false)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy() || !assignEmployee" (click)="assign()">{{ i18n.t('mfg.line.assign') }}</button>
      </div>
    </app-dialog>

    <!-- End an assignment -->
    <app-dialog [open]="!!ending()" size="sm" [heading]="i18n.t('mfg.line.end')" (dismiss)="ending.set(null)">
      @if (ending(); as e) {
        <div class="editor bo-form">
          @if (dialogError()) {
            <p class="notice danger" role="alert">{{ dialogError() }}</p>
          }
          <p>{{ i18n.t('mfg.line.endWhat', { name: e.fullName }) }}</p>
          <div class="bo-field">
            <label for="en-day">{{ i18n.t('mfg.line.endDay') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
            <app-date-input id="en-day" [(ngModel)]="endDay" />
            <p class="bo-hint">{{ i18n.t('mfg.line.todayIfEmpty') }}</p>
          </div>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="ending.set(null)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-danger" [disabled]="busy()" (click)="end()">{{ i18n.t('mfg.line.end') }}</button>
      </div>
    </app-dialog>

    <!-- The products the line makes -->
    <app-dialog [open]="productsOpen()" size="md" [heading]="i18n.t('mfg.line.editProducts')" (dismiss)="productsOpen.set(false)">
      @if (productsOpen()) {
        <div class="editor bo-form">
          @if (dialogError()) {
            <p class="notice danger" role="alert">{{ dialogError() }}</p>
          }
          @if (!producible().length) {
            <p class="bo-hint">{{ i18n.t('mfg.line.noProducible') }}</p>
          }
          @for (p of producible(); track p.productId) {
            <div class="bo-check">
              <input [id]="'lp-' + p.productId" type="checkbox" [checked]="chosenProducts.has(p.productId)" (change)="toggleProduct(p.productId)" />
              <label [for]="'lp-' + p.productId"><span class="mono" dir="ltr">{{ p.sku }}</span> {{ name(p) }} · {{ i18n.t('mfg.role.' + p.role) }}</label>
            </div>
          }
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="productsOpen.set(false)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy()" (click)="saveProducts()">{{ i18n.t('common.save') }}</button>
      </div>
    </app-dialog>

    <!-- Members -->
    <app-dialog [open]="membersOpen()" size="md" [heading]="i18n.t('mfg.line.editMembers')" (dismiss)="membersOpen.set(false)">
      @if (membersOpen()) {
        <div class="editor bo-form">
          @if (dialogError()) {
            <p class="notice danger" role="alert">{{ dialogError() }}</p>
          }
          @for (m of memberRows; track $index; let i = $index) {
            <div class="member-row">
              <select [attr.aria-label]="i18n.t('mfg.line.member')" [(ngModel)]="m.userId">
                @for (u of users(); track u.id) {
                  <option [ngValue]="u.id">{{ u.fullName }} ({{ u.username }})</option>
                }
              </select>
              <select [attr.aria-label]="i18n.t('mfg.line.memberRole')" [(ngModel)]="m.role">
                <option ngValue="Manager">{{ i18n.t('mfg.member.Manager') }}</option>
                <option ngValue="Supervisor">{{ i18n.t('mfg.member.Supervisor') }}</option>
              </select>
              <button type="button" class="btn-ghost bo-small" (click)="memberRows.splice(i, 1)">{{ i18n.t('mfg.common.remove') }}</button>
            </div>
          } @empty {
            <p class="bo-hint">{{ i18n.t('mfg.line.noMembers') }}</p>
          }
          <div>
            <button type="button" class="btn-ghost" [disabled]="!users().length" (click)="addMember()">+ {{ i18n.t('mfg.line.addMember') }}</button>
          </div>
          <p class="bo-hint">{{ i18n.t('mfg.line.membersAccessHint') }}</p>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="membersOpen.set(false)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy()" (click)="saveMembers()">{{ i18n.t('common.save') }}</button>
      </div>
    </app-dialog>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .head-actions {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-2);
      }
      .facts {
        display: grid;
        gap: var(--sp-3) var(--sp-4);
        grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr));
        margin: 0;
      }
      .facts dt {
        color: var(--muted);
        font-size: var(--fs-sm);
      }
      .facts dd {
        margin: 0;
        font-weight: 600;
      }
      .facts__wide {
        grid-column: 1 / -1;
      }
      .pad {
        padding: var(--sp-2) var(--sp-4) 0;
        margin: 0;
      }
      /* A list's filters and its pages, inside the panel's own edges. */
      .pane-bar {
        padding-block: var(--sp-3);
        padding-inline: var(--sp-4);
      }
      .pane-foot {
        padding-block: var(--sp-3);
        padding-inline: var(--sp-4);
      }
      .chips {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-2);
        margin: 0;
        padding: var(--sp-3) var(--sp-4) var(--sp-4);
        list-style: none;
      }
      .chips li {
        padding: var(--sp-1) var(--sp-3);
        border: var(--line) solid var(--border);
        border-radius: var(--radius-pill);
        background: var(--surface-2);
      }
      .editor {
        display: grid;
        gap: var(--sp-3);
      }
      .member-row {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-2);
        align-items: center;
      }
    `,
  ],
})
export class MfgLineComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly confirm = inject(ConfirmService);
  private readonly regional = inject(RegionalService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly id = input.required<string>();
  /** The recipes page, filtered on this line. */
  readonly openRecipes = output<string>();

  readonly line = signal<ProductionLineDto | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly dialogError = signal<string | null>(null);
  readonly qty = qty;

  readonly jobRoles = signal<JobRoleDto[]>([]);
  readonly activeRoles = computed(() => this.jobRoles().filter((r) => r.isActive));

  // The employees' list (owner, 2026-11-17): paged, and filtered in memory on its state and the branch that pays. It is
  // the line's own record (every route on the line answers it whole, and the assign dialog reads it), not a list of
  // its own, so it pages in the browser like the other lines of one document.
  readonly employeeState = signal<'all' | 'working' | 'ended'>('all');
  readonly employeeBranch = signal<string>('all');
  /** The paying branches the list names, each once. */
  readonly payrollBranches = computed(() => {
    const seen = new Map<string, { id: string; code: string; name: string }>();
    const ar = this.i18n.lang() === 'ar';
    for (const e of this.line()?.employees ?? []) {
      if (!seen.has(e.payrollBranchId)) {
        seen.set(e.payrollBranchId, { id: e.payrollBranchId, code: e.payrollBranchCode, name: ar ? e.payrollBranchNameAr : e.payrollBranchNameFr });
      }
    }
    return [...seen.values()];
  });
  readonly employeesFiltered = computed(() => this.employeeState() !== 'all' || this.employeeBranch() !== 'all');
  readonly shownEmployees = computed(() => {
    const state = this.employeeState();
    const branch = this.employeeBranch();
    return (this.line()?.employees ?? []).filter(
      (e) => (state === 'all' || !!e.endedOn === (state === 'ended')) && (branch === 'all' || e.payrollBranchId === branch),
    );
  });
  readonly employeePager = createPager(() => this.shownEmployees(), {
    key: 'mfg.line.employees',
    resetOn: () => [this.employeeState(), this.employeeBranch()],
  });

  // The line's stock (owner, 2026-11-17: "paginate in backend, not frontend"): a page at a time from the server, by
  // the product's manufacturing role.
  readonly stockRole = signal<string>('all');
  readonly stockPager = createServerPager<LineStockRow, { role: string }, ChoicesPageSummary>(
    (page) => this.pages.lineStock(this.id(), { skip: page.skip, take: page.take }, page.filters),
    { key: 'mfg.line.stock', filters: () => ({ role: this.stockRole() }) },
  );
  /** The roles the stock holds, in the order they first appear. */
  readonly stockRoles = computed(() => this.stockPager.summary()?.choices ?? []);
  /** The stock could not be read, in words. */
  readonly stockError = computed(() => {
    const e = this.stockPager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : null;
  });

  clearEmployeeFilters(): void {
    this.employeeState.set('all');
    this.employeeBranch.set('all');
  }

  // assign
  readonly assignOpen = signal(false);
  readonly candidates = signal<LineEmployeeCandidate[]>([]);
  /** Who is not already working on the line. */
  readonly freeCandidates = computed(() => {
    const working = new Set((this.line()?.employees ?? []).filter((e) => !e.endedOn).map((e) => e.employeeId));
    return this.candidates().filter((c) => !working.has(c.id));
  });
  assignEmployee = '';
  assignRole: string | null = null;
  assignDay = '';
  assignNote = '';

  // end
  readonly ending = signal<LineEmployeeDto | null>(null);
  endDay = '';

  // products
  readonly productsOpen = signal(false);
  readonly producible = signal<MaterialDto[]>([]);
  chosenProducts = new Set<string>();

  // members
  readonly membersOpen = signal(false);
  readonly users = signal<LineUserCandidate[]>([]);
  memberRows: { userId: string; role: LineMemberRole }[] = [];

  async ngOnInit(): Promise<void> {
    await this.reload();
    this.api.jobRoles().then((r) => this.jobRoles.set(r), () => undefined);
  }

  async reload(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const line = await this.api.line(this.id());
      this.line.set(line);
      this.chrome?.heading.set(`${line.line.code} — ${localizedName(line.line, this.i18n.lang())}`);
      this.chrome?.description.set(null);
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
      this.chrome?.heading.set('—');
    } finally {
      this.busy.set(false);
    }
  }

  /** Asks again for the page of stock on screen. */
  async loadStock(): Promise<void> {
    await this.stockPager.reload();
  }

  name(value: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(value, this.i18n.lang());
  }

  day(value: string | null): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  activeTone(active: boolean) {
    return toneOf('active', active ? 'Active' : 'Inactive');
  }

  assignmentTone(e: LineEmployeeDto) {
    return toneOf('lineAssignment', e.endedOn ? 'ended' : 'working');
  }

  // ---------- assign / end ----------

  async openAssign(): Promise<void> {
    this.dialogError.set(null);
    this.assignEmployee = '';
    this.assignRole = null;
    this.assignDay = '';
    this.assignNote = '';
    this.assignOpen.set(true);
    try {
      this.candidates.set(await this.api.employeeCandidates(this.id()));
    } catch (e) {
      this.dialogError.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }

  async assign(): Promise<void> {
    if (!this.assignEmployee) return;
    this.busy.set(true);
    this.dialogError.set(null);
    try {
      this.line.set(
        await this.api.assignEmployee(this.id(), {
          employeeId: this.assignEmployee,
          jobRoleId: this.assignRole,
          assignedOn: this.assignDay || null,
          note: this.assignNote.trim() || null,
        }),
      );
      this.assignOpen.set(false);
      this.notice.set(this.i18n.t('mfg.line.assigned'));
    } catch (e) {
      this.dialogError.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  openEnd(e: LineEmployeeDto): void {
    this.dialogError.set(null);
    this.endDay = '';
    this.ending.set(e);
  }

  async end(): Promise<void> {
    const e = this.ending();
    if (!e) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('mfg.line.end'), this.i18n.t('mfg.line.endWhat', { name: e.fullName }))))) return;
    this.busy.set(true);
    this.dialogError.set(null);
    try {
      this.line.set(await this.api.endEmployee(this.id(), e.id, this.endDay || null));
      this.ending.set(null);
    } catch (err) {
      this.dialogError.set(mfgError(err, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  // ---------- products ----------

  async openProducts(): Promise<void> {
    this.dialogError.set(null);
    this.chosenProducts = new Set((this.line()?.products ?? []).map((p) => p.productId));
    this.productsOpen.set(true);
    try {
      const materials = await this.api.materials();
      this.producible.set(materials.filter((m) => PRODUCED_ROLES.includes(m.role)));
    } catch (e) {
      this.dialogError.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }

  toggleProduct(id: string): void {
    if (this.chosenProducts.has(id)) this.chosenProducts.delete(id);
    else this.chosenProducts.add(id);
  }

  async saveProducts(): Promise<void> {
    this.busy.set(true);
    this.dialogError.set(null);
    try {
      this.line.set(await this.api.setProducts(this.id(), [...this.chosenProducts]));
      this.productsOpen.set(false);
    } catch (e) {
      this.dialogError.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  // ---------- members ----------

  async openMembers(): Promise<void> {
    this.dialogError.set(null);
    this.memberRows = (this.line()?.members ?? []).map((m) => ({ userId: m.userId, role: m.role }));
    this.membersOpen.set(true);
    try {
      this.users.set(await this.api.lineUserCandidates());
    } catch (e) {
      this.dialogError.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }

  addMember(): void {
    const free = this.users().find((u) => !this.memberRows.some((m) => m.userId === u.id));
    if (free) this.memberRows.push({ userId: free.id, role: 'Supervisor' });
  }

  async saveMembers(): Promise<void> {
    if (!(await this.confirm.ask(saveRequest(this.i18n, this.line()?.line.code ?? '')))) return;
    this.busy.set(true);
    this.dialogError.set(null);
    try {
      this.line.set(await this.api.setMembers(this.id(), this.memberRows));
      this.membersOpen.set(false);
    } catch (e) {
      this.dialogError.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }
}
