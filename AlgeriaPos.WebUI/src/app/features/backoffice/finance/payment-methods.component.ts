import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminPaymentMethod, AdminService, UpsertPaymentMethodRequest, adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { ConfirmService } from '../../../ui/confirm.service';
import { deleteRequest, saveRequest } from '../../../ui/confirm-requests';
import { DialogComponent } from '../../../ui/dialog.component';
import { FoldComponent } from '../../../ui/fold.component';
import { MENU } from '../../../ui/menu.component';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PAGE_CHROME } from '../shell/page-chrome';

/**
 * Finance › Settings › Payment methods: which tenders exist and how each behaves — whether it
 * needs clearing, a reference, a cheque's details. The same list, form and calls as the treasury
 * screen's methods chip, on a page of Finance settings beside the voucher categories (§3.2); the
 * editor is the app's dialog instead of a hand-made overlay.
 *
 * Company-wide data: the page header says so (core/nav.ts `scope: 'company'`).
 */
@Component({
  selector: 'app-payment-methods',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ...MENU, DialogComponent, FoldComponent, StatusComponent, TableStateComponent],
  template: `
    <ng-template appPageActions>
      <button type="button" class="btn-primary" (click)="openCreate()">
        <span aria-hidden="true">+</span> {{ i18n.t('treasury.newMethod') }}
      </button>
    </ng-template>

    @if (error()) {
      <p class="notice danger" role="alert">{{ error() }}</p>
    }

    <section class="ui-surface" [attr.aria-busy]="busy() && !methods().length" aria-labelledby="pm-title">
      <div class="ui-surface__head">
        <h2 id="pm-title">{{ i18n.t('treasury.methods') }}</h2>
        <p>{{ i18n.t('treasury.methodsHint') }}</p>
      </div>
      @if (busy() && !methods().length) {
        <app-table-state kind="loading" />
      } @else if (!methods().length) {
        <app-table-state kind="empty" [message]="i18n.t('treasury.noMethods')" />
      } @else {
        <div class="ui-scroll">
          <table class="ui-table ui-table--cards">
            <thead>
              <tr>
                <th>{{ i18n.t('treasury.key') }}</th>
                <th>{{ i18n.t('common.name') }}</th>
                <th>{{ i18n.t('treasury.methodKind') }}</th>
                <th>{{ i18n.t('treasury.confirmation') }}</th>
                <th>{{ i18n.t('treasury.drawer') }}</th>
                <th>{{ i18n.t('common.state') }}</th>
                <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
              </tr>
            </thead>
            <tbody>
              @for (m of methods(); track m.id) {
                <tr>
                  <td class="mono" [attr.data-label]="i18n.t('treasury.key')">{{ m.key }}</td>
                  <td [attr.data-label]="i18n.t('common.name')" dir="auto">{{ name(m) }}</td>
                  <td [attr.data-label]="i18n.t('treasury.methodKind')">
                    <span class="ui-tag">{{ i18n.t(m.isBuiltIn ? 'treasury.builtIn' : 'treasury.operatorDefined') }}</span>
                  </td>
                  <td [attr.data-label]="i18n.t('treasury.confirmation')">
                    {{ i18n.t(m.requiresConfirmation ? 'treasury.needsClearing' : 'treasury.immediate') }}
                  </td>
                  <td [attr.data-label]="i18n.t('treasury.drawer')">{{ i18n.t(m.affectsCashDrawer ? 'common.yes' : 'common.no') }}</td>
                  <td [attr.data-label]="i18n.t('common.state')">
                    <app-status [tone]="tone(m)">{{ i18n.t(m.isActive ? 'common.active' : 'common.inactive') }}</app-status>
                  </td>
                  <td class="ui-cell-menu">
                    <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + name(m)">
                      <span menuTrigger aria-hidden="true">⋯</span>
                      <button type="button" appMenuItem (click)="edit(m)">{{ i18n.t('common.edit') }}</button>
                      @if (!m.isBuiltIn) {
                        <hr appMenuSeparator />
                        <button type="button" appMenuItem class="is-danger" (click)="remove(m)">{{ i18n.t('common.delete') }}</button>
                      }
                    </app-menu>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>

    <app-dialog [open]="editorOpen()" size="md" [heading]="editingId() ? i18n.t('common.edit') : i18n.t('treasury.newMethod')" (dismiss)="close()">
      <div class="bo-form">
        <app-fold class="bo-group" [heading]="i18n.t('treasury.identity')" [open]="true">
          <div class="bo-fields">
            <div class="bo-field" [class.is-invalid]="tried() && keyMissing()">
              <label for="tr-key">
                {{ i18n.t('treasury.key') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
              </label>
              <input id="tr-key" [(ngModel)]="form.key" [disabled]="!!editingId()" class="mono" placeholder="virement-ccp" />
              @if (tried() && keyMissing()) {
                <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
              }
            </div>
            <div class="bo-field">
              <label for="tr-fr">{{ i18n.t('field.nameFr') }}</label>
              <input id="tr-fr" [(ngModel)]="form.nameFr" />
            </div>
            <div class="bo-field">
              <label for="tr-ar">{{ i18n.t('field.nameAr') }}</label>
              <input id="tr-ar" [(ngModel)]="form.nameAr" dir="rtl" />
            </div>
            <div class="bo-field">
              <label for="tr-en">{{ i18n.t('field.nameEn') }}</label>
              <input id="tr-en" [(ngModel)]="form.nameEn" />
            </div>
            <div class="bo-field">
              <label for="tr-order">{{ i18n.t('treasury.sortOrder') }}</label>
              <input id="tr-order" type="number" [(ngModel)]="form.sortOrder" class="num mono" />
            </div>
          </div>
        </app-fold>

        <!-- Folded at first: a new method starts with working defaults (needs clearing, active). -->
        <app-fold class="bo-group" [heading]="i18n.t('treasury.rules')">
          <div class="bo-fields bo-fields--roomy">
            <div class="bo-check">
              <input id="tr-conf" type="checkbox" [(ngModel)]="form.requiresConfirmation" />
              <label for="tr-conf">{{ i18n.t('treasury.requiresConfirmation') }}</label>
            </div>
            <div class="bo-check">
              <input id="tr-ref-req" type="checkbox" [(ngModel)]="form.requiresReference" />
              <label for="tr-ref-req">{{ i18n.t('treasury.requiresReference') }}</label>
            </div>
            <div class="bo-check">
              <input id="tr-inst" type="checkbox" [(ngModel)]="form.requiresInstrumentDetails" />
              <label for="tr-inst">{{ i18n.t('treasury.requiresInstrument') }}</label>
            </div>
            <div class="bo-check">
              <input id="tr-active" type="checkbox" [(ngModel)]="form.isActive" />
              <label for="tr-active">{{ i18n.t('common.active') }}</label>
            </div>
          </div>
        </app-fold>
        @if (formError()) {
          <p class="notice danger" role="alert">{{ formError() }}</p>
        }
      </div>
      <div dialogFoot class="foot">
        <button type="button" class="btn-ghost" (click)="close()">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">{{ i18n.t('common.save') }}</button>
      </div>
    </app-dialog>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      :host {
        display: grid;
        gap: var(--sp-4);
      }
      .ui-surface__head h2 {
        margin: 0;
        font-size: var(--fs-section);
        font-weight: 600;
      }
      .foot {
        display: flex;
        justify-content: flex-end;
        gap: var(--sp-2);
      }
    `,
  ],
})
export class PaymentMethodsComponent implements OnInit {
  readonly i18n = inject(I18nService);
  private readonly admin = inject(AdminService);
  private readonly confirm = inject(ConfirmService);

  readonly methods = signal<AdminPaymentMethod[]>([]);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly formError = signal<string | null>(null);
  readonly editorOpen = signal(false);
  readonly editingId = signal<string | null>(null);
  readonly tried = signal(false);

  form: UpsertPaymentMethodRequest = blank();

  ngOnInit(): Promise<void> {
    return this.load();
  }

  name(m: AdminPaymentMethod): string {
    return localizedName(m, this.i18n.lang()) || m.key;
  }

  tone(m: AdminPaymentMethod) {
    return toneOf('active', m.isActive ? 'Active' : 'Inactive');
  }

  openCreate(): void {
    this.editingId.set(null);
    this.form = blank();
    this.formError.set(null);
    this.tried.set(false);
    this.editorOpen.set(true);
  }

  edit(m: AdminPaymentMethod): void {
    this.editingId.set(m.id);
    this.form = {
      key: m.key,
      nameAr: m.nameAr,
      nameFr: m.nameFr,
      nameEn: m.nameEn,
      requiresConfirmation: m.requiresConfirmation,
      requiresReference: m.requiresReference,
      requiresInstrumentDetails: m.requiresInstrumentDetails,
      isActive: m.isActive,
      sortOrder: m.sortOrder,
    };
    this.formError.set(null);
    this.tried.set(false);
    this.editorOpen.set(true);
  }

  close(): void {
    this.editorOpen.set(false);
    this.editingId.set(null);
    this.form = blank();
    this.tried.set(false);
  }

  keyMissing(): boolean {
    return !this.editingId() && !this.form.key?.trim();
  }

  async save(): Promise<void> {
    this.tried.set(true);
    if (this.keyMissing()) return;
    const name = localizedName(this.form, this.i18n.lang()) || this.form.key;
    if (!(await this.confirm.ask(saveRequest(this.i18n, name)))) return;
    this.busy.set(true);
    this.formError.set(null);
    try {
      const id = this.editingId();
      if (id) await this.admin.updatePaymentMethod(id, this.form);
      else await this.admin.createPaymentMethod(this.form);
      this.close();
      await this.load();
    } catch (e) {
      // Said in the dialog, beside what was typed, rather than behind it on the page.
      this.formError.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async remove(m: AdminPaymentMethod): Promise<void> {
    const title = this.i18n.t('paymentMethods.delete.title', { name: this.name(m) });
    if (!(await this.confirm.ask(deleteRequest(this.i18n, title, this.i18n.t('paymentMethods.delete.message'))))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.admin.deletePaymentMethod(m.id);
      await this.load();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    this.busy.set(true);
    try {
      this.methods.set(await this.admin.listPaymentMethods());
      this.error.set(null);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

function blank(): UpsertPaymentMethodRequest {
  return {
    key: '',
    nameAr: '',
    nameFr: '',
    nameEn: '',
    // An added tender is money in transit by default: if it were instant it would already be cash.
    // The operator can still turn this off.
    requiresConfirmation: true,
    requiresReference: false,
    requiresInstrumentDetails: false,
    isActive: true,
    sortOrder: 10,
  };
}
