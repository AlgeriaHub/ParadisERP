import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { MaintenanceModule } from '../../../core/maintenance-module.service';
import { NavPage, visibleNav } from '../../../core/nav';
import { SubscriptionStatus, navUser } from '../../../core/subscription-status';
import { BackofficeNavState } from './backoffice-nav-state';

export interface NavSearchEntry {
  page: NavPage;
  label: string;
  module: string;
  link: string;
}

let nextId = 0;

function fold(value: string): string {
  return value
    .normalize('NFD')
    .replace(/[\u0300-\u036f\u064b-\u065f\u0640]/g, '')
    .toLowerCase()
    .trim();
}

/**
 * "Search for a page" in the back-office top bar (Paradis redesign, owner 2026-11-12):
 * lists the menu's own links for the signed-in user (so it follows permissions), and «/» focuses it.
 */
@Component({
  selector: 'app-bo-nav-search',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="search" role="search">
      <label class="sr-only" [for]="inputId">{{ i18n.t('nav.search.label') }}</label>
      <svg
        class="search__icon"
        viewBox="0 0 24 24"
        width="16"
        height="16"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        aria-hidden="true"
      >
        <circle cx="11" cy="11" r="6" />
        <path d="M20 20l-4.3-4.3" />
      </svg>
      <input
        #box
        [id]="inputId"
        class="search__input"
        type="search"
        role="combobox"
        aria-autocomplete="list"
        autocomplete="off"
        spellcheck="false"
        [attr.aria-expanded]="open()"
        [attr.aria-controls]="listId"
        [attr.aria-activedescendant]="open() && results().length ? listId + '-' + active() : null"
        [placeholder]="i18n.t('nav.search')"
        [value]="query()"
        (input)="onInput($event)"
        (focus)="opened.set(true)"
        (keydown)="onKey($event)"
      />
      <kbd class="search__key" aria-hidden="true">/</kbd>
      @if (open()) {
        <div class="search__results" role="listbox" [id]="listId" [attr.aria-label]="i18n.t('nav.search.label')">
          @for (r of results(); track r.page.key; let i = $index) {
            <a
              class="search__item"
              role="option"
              [id]="listId + '-' + i"
              [class.is-active]="i === active()"
              [attr.aria-selected]="i === active()"
              [routerLink]="r.link"
              (click)="chosen(r)"
              (mouseenter)="active.set(i)"
            >
              <span class="search__label">{{ r.label }}</span>
              @if (r.module) {
                <span class="search__module">{{ r.module }}</span>
              }
            </a>
          } @empty {
            <p class="search__none">{{ i18n.t('nav.search.none') }}</p>
          }
        </div>
      }
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
        position: relative;
      }
      .search {
        position: relative;
      }
      .search__icon {
        position: absolute;
        inset-block-start: 50%;
        inset-inline-start: 0.75rem;
        translate: 0 -50%;
        color: var(--muted);
        pointer-events: none;
      }
      .search__input {
        inline-size: 100%;
        block-size: 2.5rem;
        padding-block: 0;
        padding-inline: 2.25rem 2.25rem;
        border: 1px solid var(--border);
        border-radius: var(--radius-btn);
        background: var(--bg);
        box-shadow: none;
        font-size: var(--fs-sm);
      }
      .search__input::placeholder {
        color: var(--muted);
      }
      .search__input:focus {
        background: var(--surface);
        border-color: var(--primary-500);
        box-shadow: 0 0 0 3px color-mix(in srgb, var(--primary-500) 20%, transparent);
        outline: none;
      }
      .search__input::-webkit-search-cancel-button {
        display: none;
      }
      .search__key {
        position: absolute;
        inset-block-start: 50%;
        inset-inline-end: 0.75rem;
        translate: 0 -50%;
        min-inline-size: 1.25rem;
        padding: 0 0.3rem;
        border: 1px solid var(--border);
        border-radius: 0.375rem;
        background: var(--surface);
        color: var(--muted);
        font: inherit;
        font-size: 11px;
        line-height: 1.5;
        text-align: center;
        pointer-events: none;
      }
      .search__input:focus ~ .search__key {
        display: none;
      }
      .search__results {
        position: absolute;
        inset-block-start: calc(100% + 0.5rem);
        inset-inline-start: 0;
        z-index: var(--z-modal);
        inline-size: max(100%, 20rem);
        padding: 0.375rem;
        border: 1px solid var(--border);
        border-radius: var(--radius);
        background: var(--surface);
        box-shadow: var(--shadow-pop);
        display: grid;
        gap: 2px;
      }
      .search__item {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        gap: 0.75rem;
        padding: 0.5rem 0.75rem;
        border-radius: var(--radius-sm);
        color: var(--text);
        font-size: var(--fs-sm);
        text-decoration: none;
      }
      .search__item.is-active {
        background: var(--primary-soft);
        color: var(--primary-dark);
      }
      .search__module {
        flex: none;
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      .search__none {
        margin: 0;
        padding: 0.5rem 0.75rem;
        font-size: var(--fs-sm);
        color: var(--muted);
      }
    `,
  ],
})
export class NavSearchComponent {
  readonly i18n = inject(I18nService);
  private readonly auth = inject(AuthService);
  private readonly subscription = inject(SubscriptionStatus);
  private readonly maintenance = inject(MaintenanceModule);
  private readonly state = inject(BackofficeNavState);
  private readonly router = inject(Router);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly box = viewChild.required<ElementRef<HTMLInputElement>>('box');

  private readonly id = nextId++;
  readonly inputId = `bo-nav-search-${this.id}`;
  readonly listId = `bo-nav-search-list-${this.id}`;

  readonly query = signal('');
  readonly opened = signal(false);
  readonly active = signal(0);

  readonly entries = computed<NavSearchEntry[]>(() => {
    const mods = visibleNav(navUser(this.auth.currentUser(), this.subscription.status(), this.maintenance.on()));
    return mods.flatMap((m) => {
      const modLabel = this.i18n.t(m.label);
      return m.pages.map((page) => {
        const label = this.i18n.t(page.label);
        const module = m.pages.length === 1 && label === modLabel ? '' : modLabel;
        return {
          page,
          label,
          module,
          link: page.external ? page.path : '/backoffice/' + page.path,
        };
      });
    });
  });

  readonly results = computed<NavSearchEntry[]>(() => {
    const q = fold(this.query());
    if (!q) return [];
    return this.entries()
      .filter((e) => fold(e.label).includes(q) || (e.module !== '' && fold(e.module).includes(q)))
      .slice(0, 8);
  });

  readonly open = computed(() => this.opened() && this.query().trim() !== '');

  onInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.query.set(value);
    this.active.set(0);
    this.opened.set(true);
  }

  onKey(event: KeyboardEvent): void {
    const list = this.results();
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      if (list.length > 0) this.active.update((i) => (i + 1) % list.length);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      if (list.length > 0) this.active.update((i) => (i - 1 + list.length) % list.length);
    } else if (event.key === 'Enter') {
      const item = list[this.active()];
      if (item) {
        event.preventDefault();
        this.go(item);
      }
    } else if (event.key === 'Escape') {
      this.opened.set(false);
      this.box().nativeElement.blur();
    }
  }

  go(r: NavSearchEntry): void {
    this.state.picked(r.page);
    void this.router.navigateByUrl(r.link);
    this.reset();
  }

  chosen(r: NavSearchEntry): void {
    this.state.picked(r.page);
    this.reset();
  }

  reset(): void {
    this.query.set('');
    this.opened.set(false);
    this.active.set(0);
    this.box().nativeElement.blur();
  }

  @HostListener('document:keydown', ['$event'])
  onDocumentKeydown(event: KeyboardEvent): void {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey) return;
    const target = event.target as HTMLElement | null;
    if (
      target &&
      (target.isContentEditable ||
        target.getAttribute?.('contenteditable') === 'true' ||
        /^(INPUT|TEXTAREA|SELECT)$/i.test(target.tagName ?? ''))
    ) {
      return;
    }
    if (document.querySelector('dialog[open]')) return;
    event.preventDefault();
    this.box().nativeElement.focus();
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    const target = event.target as Node | null;
    if (!target || !this.host.nativeElement.contains(target)) {
      this.opened.set(false);
    }
  }
}
