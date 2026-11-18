import { Component, inject, input, output } from '@angular/core';
import { AdminCustomer } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { BackofficeCustomersComponent } from './customers-tab.component';

/**
 * "+ New customer" from the rental form or the till (FR-1): the customer editor of the Customers
 * page, opened on a new customer and nothing else. It brings the back-office vocabulary with it,
 * because the till never downloads that dictionary — the till loads this component (and the
 * editor) only when the button is pressed, so its offline bundle does not grow.
 *
 * The host stays as it was underneath: the editor is an overlay, so a half-filled rental form or
 * a till's cart is untouched whether the reader saves or cancels.
 */
@Component({
  selector: 'app-quick-customer',
  standalone: true,
  imports: [BackofficeCustomersComponent],
  template: `<app-backoffice-customers
    [createOnly]="true"
    [atBranch]="branchId()"
    (created)="created.emit($event)"
    (closed)="closed.emit()"
  />`,
})
export class QuickCustomerComponent {
  /** A field initialiser, so the keys are in place before the editor's first render. */
  private readonly words = inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  /**
   * The branch the host is raising its document at — the rental contract's — whose code goes
   * into the new customer's (owner, 2026-10-05). Left out (the till) or null (no branch chosen
   * yet), the editor falls back to the working-branch rule.
   */
  readonly branchId = input<string | null>(null);
  readonly created = output<AdminCustomer>();
  readonly closed = output<void>();
}
