import { Component, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { AdminBranch, AdminCustomer, AdminPaymentMethod, AdminService, adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { PageChrome } from '../shell/page-chrome';
import { PaymentVoucherDetailComponent } from './payment-voucher-detail.component';
import { PaymentVoucherFormComponent } from './payment-voucher-form.component';
import { PaymentVoucherListComponent } from './payment-voucher-list.component';
import { PaymentVoucherResponse } from './payment-vouchers-api.service';

/** Where the section is: the list, one voucher, or the form that raises one. */
export type VoucherView = { kind: 'list' } | { kind: 'voucher'; id: string } | { kind: 'new' };

/** Tenders that move a balance of their own do not pay money out; the server refuses them. */
const NOT_A_PAYOUT = new Set<AdminPaymentMethod['behavior']>(['Credit', 'GiftCard', 'Loyalty']);

/**
 * Payment vouchers: the list, one voucher, and the form that raises one — money back to a
 * customer or an expense the branch paid (the owner's answers 24–27).
 *
 * <p>Every view is an address (REDESIGN_PLAN.md §4): `finance/vouchers`, `…/new`, `…/<id>`. The view
 * is read from it, never held apart from it, so a reload, a deep link, Back and Forward all land
 * where the address says.</p>
 *
 * <p><b>`…/new?returnNote=<id>`</b> opens the form on that return note: the rental screens link
 * here when a note left a refund owed. The older `finance/vouchers?returnNote=<id>` is sent there,
 * in place, so a link printed before still works.</p>
 */
@Component({
  selector: 'app-backoffice-payment-vouchers',
  standalone: true,
  imports: [PaymentVoucherListComponent, PaymentVoucherDetailComponent, PaymentVoucherFormComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (!loaded()) {
        <p class="bo-loading">
          <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
          <span class="bo-loading__rows" aria-hidden="true"></span>
        </p>
      } @else {
        @switch (view().kind) {
          @case ('list') {
            <!-- The contract-required categories live in Finance › Settings
                 (voucher-categories.component.ts): a setting, kept off the day's list. -->
            <app-payment-voucher-list [branches]="branches()" [methods]="methods()" (newVoucher)="go({ kind: 'new' })" />
          }
          @case ('voucher') {
            <!-- Keyed on the voucher: another voucher's address is another screen, loaded afresh. -->
            @for (id of [voucherId()!]; track id) {
              <app-payment-voucher-detail [id]="id" [arrival]="arrivalFor(id)" [methods]="methods()" [branches]="branches()" />
            }
          }
          @case ('new') {
            <!-- Keyed on the note: a link to another note is another form. -->
            @for (note of [returnNote() + '|' + refundContract()]; track note) {
              <app-payment-voucher-form
                [returnNoteId]="returnNote()"
                [refundContractId]="refundContract()"
                [customers]="customers()"
                [branches]="branches()"
                [methods]="methods()"
                (saved)="saved($event)"
                (cancelled)="go({ kind: 'list' })"
              />
            }
          }
        }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
})
export class BackofficePaymentVouchersComponent implements OnInit {
  private readonly admin = inject(AdminService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  /** The address under `finance/vouchers` (core/nav.ts `deep`): `[]`, `['new']` or `[id]`. */
  readonly path = input<string[]>([]);
  readonly view = computed<VoucherView>(() => voucherViewFor(this.path()));
  readonly voucherId = computed(() => {
    const view = this.view();
    return view.kind === 'voucher' ? view.id : null;
  });

  private readonly query = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });
  readonly returnNote = computed(() => this.query().get('returnNote'));
  /** `…/new?contract=<id>`: the refund of a contract's unused period (owner, 2026-10-06). */
  readonly refundContract = computed(() => this.query().get('contract'));

  readonly loaded = signal(false);
  readonly error = signal<string | null>(null);
  readonly customers = signal<AdminCustomer[]>([]);
  readonly branches = signal<AdminBranch[]>([]);
  readonly methods = signal<AdminPaymentMethod[]>([]);

  /**
   * The answer of the save that brought the reader to a voucher, with its warning: shown on that
   * voucher until the reader moves on. Not part of the address — a reload shows the voucher as
   * it stands, without repeating "saved".
   */
  private readonly arrival = signal<PaymentVoucherResponse | null>(null);

  constructor() {
    effect(() => {
      const view = this.view();
      const note = this.returnNote();
      untracked(() => {
        // An older link, `finance/vouchers?returnNote=…`, is the form on that note.
        if (view.kind === 'list' && note) {
          void this.router.navigate(voucherUrl({ kind: 'new' }), { queryParams: { returnNote: note }, replaceUrl: true });
          return;
        }
        if (view.kind !== 'voucher' || view.id !== this.arrival()?.voucher.id) this.arrival.set(null);
        // The list is the page itself; a voucher or the form names itself in the header.
        if (view.kind === 'list') {
          this.chrome?.heading.set(null);
          this.chrome?.trail.set([]);
          this.chrome?.description.set(null);
        } else if (view.kind === 'new') {
          this.chrome?.heading.set(this.i18n.t('pv.form.title'));
          this.chrome?.trail.set([]);
          this.chrome?.description.set(null);
        }
      });
    });
  }

  async ngOnInit(): Promise<void> {
    try {
      const [customers, branches, methods] = await Promise.all([
        this.admin.listCustomers(),
        this.admin.listBranches(),
        this.admin.listPaymentMethods(),
      ]);
      this.customers.set(customers);
      this.branches.set(branches.filter((b) => b.isActive));
      this.methods.set(methods.filter((m) => m.isActive && !NOT_A_PAYOUT.has(m.behavior)));
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.loaded.set(true);
    }
  }

  arrivalFor(id: string): PaymentVoucherResponse | null {
    const arrival = this.arrival();
    return arrival?.voucher.id === id ? arrival : null;
  }

  saved(response: PaymentVoucherResponse): void {
    this.arrival.set(response);
    // In place of the form: Back from the new voucher goes to the list, not to a blank form.
    void this.router.navigate(voucherUrl({ kind: 'voucher', id: response.voucher.id }), { replaceUrl: true });
    window.scrollTo({ top: 0 });
  }

  go(view: VoucherView): void {
    void this.router.navigate(voucherUrl(view));
    window.scrollTo({ top: 0 });
  }
}

/** The view an address under `finance/vouchers` names; anything unrecognised is the list. */
export function voucherViewFor(path: readonly string[]): VoucherView {
  const [first] = path;
  if (!first || path.length > 1) return { kind: 'list' };
  if (first === 'new') return { kind: 'new' };
  return { kind: 'voucher', id: first };
}

/** The address of a view: the inverse of `voucherViewFor`. */
export function voucherUrl(view: VoucherView): string[] {
  const base = ['/backoffice', 'finance', 'vouchers'];
  if (view.kind === 'new') return [...base, 'new'];
  if (view.kind === 'voucher') return [...base, view.id];
  return base;
}
