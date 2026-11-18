import { Component, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { AdminBranch, AdminCustomer, AdminService, adminErrorMessage } from '../../../core/admin.service';
import { CatalogStore } from '../../../core/catalog-store';
import { I18nService } from '../../../core/i18n.service';
import { PrintKind, PrintService } from '../../print/print.service';
import { IssueNote, RentalsApi, RentalSettings, ReturnNote } from './rentals-api.service';
import { RentalContractsComponent } from './rental-contracts.component';
import { RentalContractComponent } from './rental-contract.component';
import { RentalContractFormComponent } from './rental-contract-form.component';
import { RentalIssueNoteComponent } from './rental-issue-note.component';
import { RentalReturnNoteComponent } from './rental-return-note.component';
import { RentalExtendNewComponent } from './rental-extend-new.component';
import { RentalSettingsComponent } from './rental-settings.component';
import { PageChrome } from '../shell/page-chrome';

/** Where the section is, in the order a contract is actually worked through. */
export type View =
  | { kind: 'list' }
  | { kind: 'new' }
  | { kind: 'edit'; id: string }
  | { kind: 'contract'; id: string }
  | { kind: 'issue'; id: string }
  | { kind: 'return'; id: string }
  | { kind: 'extend'; id: string };

/**
 * Renting, the owner's main business line: hiring scaffolding and formwork out to contractors.
 *
 * <p>One tab rather than six, with the customer lists, branches, catalogue and company settings
 * loaded once here and handed down. Every screen under it needs the same four, and six copies of
 * the same four requests is what makes a section feel slow.</p>
 *
 * <p>Raising a document is a task, not a dialog: the issue, return and extension screens replace
 * the contract rather than covering it, so a clerk filling one in on a phone has the screen.</p>
 */
@Component({
  selector: 'app-backoffice-rentals',
  standalone: true,
  imports: [
    RentalContractsComponent,
    RentalContractComponent,
    RentalContractFormComponent,
    RentalIssueNoteComponent,
    RentalReturnNoteComponent,
    RentalExtendNewComponent,
    RentalSettingsComponent,
  ],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <!-- An issue or return note is paper the driver leaves with, and at the moment it exists it
           is reachable nowhere else: the contract's tables are a screen away. So the offer to print
           it sits in the sentence that says it was raised, and both go when the reader moves on. -->
      @if (justSaved(); as p) {
        <p class="notice ok" role="status">
          {{ p.label }}
          <button
            type="button"
            class="btn-ghost bo-small"
            (click)="printer.open(p.kind, p.id, { lang: i18n.lang(), contract: p.contractId })"
          >
            {{ i18n.t('common.print') }}
          </button>
        </p>
      }

      @if (!page()) {
        <nav class="bo-chips" [attr.aria-label]="i18n.t('backoffice.tabs.rentals')">
          <button
            type="button"
            [class.is-on]="section() === 'contracts'"
            [attr.aria-current]="section() === 'contracts' ? 'true' : null"
            (click)="show('contracts')"
          >
            {{ i18n.t('rent.section.contracts') }}
          </button>
          <button
            type="button"
            [class.is-on]="section() === 'settings'"
            [attr.aria-current]="section() === 'settings' ? 'true' : null"
            (click)="show('settings')"
          >
            {{ i18n.t('rent.section.settings') }}
          </button>
        </nav>
      }

      @if (!settings()) {
        <p class="bo-loading">
          <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
          <span class="bo-loading__rows" aria-hidden="true"></span>
        </p>
      } @else if (section() === 'settings') {
        <app-rental-settings [products]="products()" [settings]="settings()!" (changed)="settings.set($event)" />
      } @else {
        @switch (view().kind) {
          @case ('list') {
            <app-rental-contracts
              [customers]="customers()"
              [branches]="branches()"
              (openContract)="go({ kind: 'contract', id: $event })"
              (createContract)="go({ kind: 'new' })"
            />
          }
          @case ('new') {
            <app-rental-contract-form
              [customers]="customers()"
              [branches]="branches()"
              [settings]="settings()!"
              (customerCreated)="addCustomer($event)"
              (saved)="go({ kind: 'contract', id: $event })"
              (cancelled)="go({ kind: 'list' })"
            />
          }
          @case ('edit') {
            <!-- Keyed on the contract: another contract's address is another screen, loaded afresh. -->
            @for (id of [currentId()!]; track id) {
              <app-rental-contract-form
                [contractId]="id"
                [customers]="customers()"
                [branches]="branches()"
                [settings]="settings()!"
                (customerCreated)="addCustomer($event)"
                (saved)="go({ kind: 'contract', id: $event })"
                (cancelled)="go({ kind: 'contract', id: id })"
              />
            }
          }
          @case ('contract') {
            <!-- Keyed on the contract: another contract's address is another screen, loaded afresh. -->
            @for (id of [currentId()!]; track id) {
              <app-rental-contract
                [contractId]="id"
                [tab]="tab()"
                (tabChange)="setTab($event)"
                [products]="products()"
                [branches]="branches()"
                (back)="go({ kind: 'list' })"
                (editContract)="go({ kind: 'edit', id: $event })"
                (issueNote)="go({ kind: 'issue', id: $event })"
                (returnNote)="go({ kind: 'return', id: $event })"
                (extendContract)="go({ kind: 'extend', id: $event })"
                (openContract)="go({ kind: 'contract', id: $event })"
              />
            }
          }
          @case ('issue') {
            <!-- Keyed on the contract: another contract's address is another screen, loaded afresh. -->
            @for (id of [currentId()!]; track id) {
              <app-rental-issue-note
                [contractId]="id"
                [branches]="branches()"
                [products]="products()"
                [settings]="settings()"
                (done)="issueRaised($event)"
                (cancelled)="go({ kind: 'contract', id: id })"
              />
            }
          }
          @case ('return') {
            <!-- Keyed on the contract: another contract's address is another screen, loaded afresh. -->
            @for (id of [currentId()!]; track id) {
              <app-rental-return-note
                [contractId]="id"
                [branches]="branches()"
                [products]="products()"
                [customers]="customers()"
                [settings]="settings()"
                (done)="returnRaised($event)"
                (cancelled)="go({ kind: 'contract', id: id })"
              />
            }
          }
          @case ('extend') {
            <!-- Answer 8: the extension is a new contract, opened as soon as it exists. -->
            <!-- Keyed on the contract: another contract's address is another screen, loaded afresh. -->
            @for (id of [currentId()!]; track id) {
              <app-rental-extend-new
                [contractId]="id"
                (done)="go({ kind: 'contract', id: $event })"
                (cancelled)="go({ kind: 'contract', id: id })"
              />
            }
          }
        }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      /* The offer belongs in the sentence rather than under it, and the gap is logical so the
         Arabic layout puts it on the other side of the words with no second rule. */
      .notice > .btn-ghost {
        margin-inline-start: 0.5rem;
      }
    `,
  ],
})
export class BackofficeRentalsComponent implements OnInit {
  private readonly admin = inject(AdminService);
  private readonly rentals = inject(RentalsApi);
  readonly i18n = inject(I18nService);
  readonly printer = inject(PrintService);

  /**
   * The chip the route opened this section on (core/nav.ts). Set, the sidebar is the navigation
   * and this component's own chip strip is not drawn; unset, it behaves as it always did.
   */
  readonly page = input<'contracts' | 'settings' | null>(null);
  readonly section = signal<'contracts' | 'settings'>('contracts');
  /**
   * The address under `rentals/contracts` (REDESIGN_PLAN.md §4): `[]` the list, `['new']`,
   * `[id]` a contract, `[id, 'edit' | 'issue' | 'return' | 'extend']` its documents. The view is
   * read from it, never held apart from it, so a reload, a deep link, Back and Forward all land
   * where the address says.
   */
  readonly path = input<string[]>([]);
  readonly view = computed<View>(() => viewFor(this.path()));
  /**
   * The note just raised: its sentence and what the print route needs to draw it. Held here
   * rather than handed down to the contract screen as `done`, because the sentence and the
   * button that prints what it names have to stay together — and `go` drops both.
   */
  readonly justSaved = signal<{
    kind: PrintKind;
    id: string;
    contractId: string;
    label: string;
  } | null>(null);
  readonly error = signal<string | null>(null);

  readonly customers = signal<AdminCustomer[]>([]);
  readonly branches = signal<AdminBranch[]>([]);
  /** The session's one copy of the catalogue; see CatalogStore for when it is refreshed. */
  private readonly catalog = inject(CatalogStore);
  readonly products = this.catalog.active;
  readonly settings = signal<RentalSettings | null>(null);

  readonly currentId = computed(() => {
    const view = this.view();
    return 'id' in view ? view.id : null;
  });

  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly chrome = inject(PageChrome, { optional: true });
  /** The contract page's open tab (`?tab=`), so a reload keeps it too. */
  readonly tab = toSignal(this.route.queryParamMap.pipe(map((q) => q.get('tab'))), { initialValue: null });

  constructor() {
    effect(() => {
      const view = this.view();
      untracked(() => {
        // The note-raised sentence belongs to the contract it was raised on; reaching anything
        // else — through the sidebar, Back or Forward — drops it, as moving on always did.
        const saved = this.justSaved();
        if (saved && !(view.kind === 'contract' && view.id === saved.contractId)) this.justSaved.set(null);
        // The list is the page itself; an entity screen names itself in the header.
        if (view.kind === 'list') {
          this.chrome?.heading.set(null);
          this.chrome?.trail.set([]);
          this.chrome?.description.set(null);
        }
      });
    });
  }

  /** A customer created from a contract form (FR-1) joins the list every later form is given. */
  addCustomer(customer: AdminCustomer): void {
    this.customers.update((list) => (list.some((c) => c.id === customer.id) ? list : [...list, customer]));
  }

  setTab(tab: string): void {
    // In place, not a new history entry: Back leaves the contract rather than walking its tabs.
    void this.router.navigate([], { relativeTo: this.route, queryParams: { tab }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  async ngOnInit(): Promise<void> {
    const page = this.page();
    if (page) this.section.set(page);
    try {
      const [customers, branches, settings] = await Promise.all([
        this.admin.listCustomers(),
        this.admin.listBranches(),
        this.rentals.getSettings(),
        this.catalog.ensure(),
      ]);
      this.customers.set(customers);
      this.branches.set(branches.filter((b) => b.isActive));
      this.settings.set(settings);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  show(section: 'contracts' | 'settings'): void {
    this.section.set(section);
  }

  issueRaised(note: IssueNote): void {
    this.go({ kind: 'contract', id: note.contractId });
    this.justSaved.set({
      kind: 'issue-note',
      id: note.id,
      contractId: note.contractId,
      label: this.i18n.t('rent.issue.saved', { n: note.number }),
    });
  }

  returnRaised(note: ReturnNote): void {
    this.go({ kind: 'contract', id: note.contractId });
    this.justSaved.set({
      kind: 'return-note',
      id: note.id,
      contractId: note.contractId,
      label: this.i18n.t('rent.return.saved', { n: note.number }),
    });
  }

  go(view: View): void {
    this.justSaved.set(null);
    void this.router.navigate(urlFor(view));
    // A note screen replaces a list the reader had scrolled; landing halfway down the new one
    // hides its heading and its warning.
    window.scrollTo({ top: 0 });
  }
}

/** The view an address under `rentals/contracts` names; anything unrecognised is the list. */
export function viewFor(path: readonly string[]): View {
  const [first, second] = path;
  if (!first) return { kind: 'list' };
  if (first === 'new' && path.length === 1) return { kind: 'new' };
  if (path.length === 1) return { kind: 'contract', id: first };
  if (path.length === 2 && (second === 'edit' || second === 'issue' || second === 'return' || second === 'extend')) {
    return { kind: second, id: first };
  }
  return { kind: 'list' };
}

/** The address of a view: the inverse of `viewFor`. */
export function urlFor(view: View): string[] {
  const base = ['/backoffice', 'rentals', 'contracts'];
  switch (view.kind) {
    case 'list':
      return base;
    case 'new':
      return [...base, 'new'];
    case 'contract':
      return [...base, view.id];
    default:
      return [...base, view.id, view.kind];
  }
}
