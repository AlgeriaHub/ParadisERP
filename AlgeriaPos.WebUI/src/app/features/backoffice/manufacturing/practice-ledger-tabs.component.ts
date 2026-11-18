import { Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { canOpen } from '../../../core/nav';

/**
 * The practice ledger's two sections as tabs (docs/maintenance/plan.md §5.3, §7): Production (Manufacturing › Practice
 * ledger) and Maintenance (Maintenance › Practice ledger). Each is its own address, so a deep link, Back and Forward land on
 * it; the tabs show only to a reader who may open both — each section keeps its own permissions.
 */
@Component({
  selector: 'app-practice-ledger-tabs',
  standalone: true,
  imports: [RouterLink],
  template: `
    @if (both()) {
      <nav class="ui-tabs" [attr.aria-label]="i18n.t('maintLedger.tabs')">
        <a class="ui-tab" routerLink="/backoffice/manufacturing/practice-ledger"
          [attr.aria-current]="current() === 'production' ? 'page' : null">{{ i18n.t('maintLedger.tab.production') }}</a>
        <a class="ui-tab" routerLink="/backoffice/maintenance/practice-ledger"
          [attr.aria-current]="current() === 'maintenance' ? 'page' : null">{{ i18n.t('maintLedger.tab.maintenance') }}</a>
      </nav>
    }
  `,
  styleUrls: ['../../../ui/ui-patterns.scss'],
})
export class PracticeLedgerTabsComponent {
  private readonly auth = inject(AuthService);
  readonly i18n = inject(I18nService);
  readonly current = input.required<'production' | 'maintenance'>();
  readonly both = computed(() => {
    const user = this.auth.currentUser();
    return canOpen(user, 'manufacturing.practiceLedger') && canOpen(user, 'maintenance.practiceLedger');
  });
}
