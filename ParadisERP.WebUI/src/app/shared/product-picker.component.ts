import {
  Component,
  DestroyRef,
  ElementRef,
  ViewChild,
  afterRenderEffect,
  booleanAttribute,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../core/i18n.service';
import { listenForScans } from '../core/barcode-scan';
import { localizedName } from '../core/multilingual';
import { SearchableProduct, matchCode, resolveEntry, searchProducts, looksLikeCode } from '../core/product-search';

/**
 * Putting an article on a line: scan it, or type a few letters of it.
 *
 * <p>Every screen that needed a product was offering a `select` of the whole catalogue. At a
 * hundred articles that is already a scroll; at a thousand it is unusable, and it cannot read
 * a barcode at all, so the one piece of equipment on the counter that makes this instant was
 * useless everywhere except the till. This is the control that fixes that, and it is one
 * control rather than six so that a scan behaves the same on a contract as on a sale.</p>
 *
 * <p>Two shapes, chosen with <code>clearOnPick</code>:</p>
 * <ul>
 *   <li><b>A field.</b> The article stays in the box, and the box says which one. This is a
 *   line that already exists and is being corrected.</li>
 *   <li><b>An entry box.</b> The box empties and keeps focus, so scanning five boxes of
 *   clamps puts five lines on the document without a hand leaving the gun.</li>
 * </ul>
 *
 * <p><code>catchScans</code> adds the document-wide listener described in
 * <code>barcode-scan.ts</code>: a scan is read even when the cashier last clicked something
 * else, which on a counter is most of the time.</p>
 */
@Component({
  selector: 'app-product-picker',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="pick" [class.is-open]="open()" [class.is-invalid]="invalid()">
      <div class="pick__control">
        <span class="pick__icon" aria-hidden="true">&#9638;</span>
        <input
          #box
          type="text"
          class="pick__box"
          role="combobox"
          aria-autocomplete="list"
          autocomplete="off"
          [attr.id]="inputId() || null"
          [attr.aria-expanded]="open()"
          [attr.aria-controls]="listId"
          [attr.aria-activedescendant]="activeId()"
          [attr.aria-label]="inputId() ? null : i18n.t('pick.label')"
          [disabled]="disabled()"
          [placeholder]="placeholder() || i18n.t('pick.placeholder')"
          [ngModel]="term()"
          (ngModelChange)="onType($event)"
          (focus)="onFocus()"
          (blur)="onBlur()"
          (keydown)="onKey($event)"
        />
        @if (term() && !disabled()) {
          <button
            type="button"
            class="pick__clear"
            [attr.aria-label]="i18n.t('pick.clear')"
            (mousedown)="$event.preventDefault()"
            (click)="clear()"
          >
            &times;
          </button>
        }
      </div>

      @if (open()) {
        <ul class="pick__list" role="listbox" [attr.id]="listId">
          @for (product of shown(); track product.id; let i = $index) {
            <li
              role="option"
              class="pick__row"
              [attr.id]="listId + '-' + i"
              [class.is-active]="i === active()"
              [class.is-default]="i === 0 && active() < 0 && enterTakesFirst()"
              [attr.aria-selected]="i === active()"
              (mousedown)="$event.preventDefault()"
              (click)="choose(product, true)"
            >
              <span class="pick__name">{{ name(product) }}</span>
              <span class="pick__code mono">{{ product.sku }}</span>
              @if (noteFor(product); as note) {
                <span class="pick__note">{{ note }}</span>
              }
            </li>
          } @empty {
            <li class="pick__empty">{{ i18n.t('pick.none', { term: term() }) }}</li>
          }
          @if (more() > 0) {
            <li class="pick__more">{{ i18n.t('pick.more', { n: more() }) }}</li>
          }
        </ul>
      }
    </div>
  `,
  styles: [
    `
      .pick {
        position: relative;
        display: grid;
      }

      .pick__control {
        display: flex;
        align-items: center;
        gap: var(--sp-2);
        padding-inline: var(--sp-3);
        min-height: 42px;
        background: var(--surface);
        border: var(--line) solid var(--border-strong);
        border-radius: var(--radius-field);
        transition: box-shadow var(--t-fast) ease;
      }

      .pick__control:focus-within {
        outline: none;
        box-shadow: calc(3px * var(--sx)) 3px 0 var(--accent);
      }

      .pick.is-invalid .pick__control {
        border-color: var(--danger);
        box-shadow: calc(3px * var(--sx)) 3px 0 var(--danger-soft);
      }

      .pick__icon {
        color: var(--muted);
        font-size: 0.95rem;
        line-height: 1;
      }

      /* The box carries no border of its own: the wrapper draws it, so the icon, the text and
         the clear button sit inside one control rather than beside a smaller one. */
      .pick__box {
        flex: 1;
        min-width: 0;
        border: 0;
        background: none;
        padding-inline: 0;
        box-shadow: none;
        font: inherit;
        color: var(--text);
      }

      .pick__box:focus {
        outline: none;
        box-shadow: none;
      }

      .pick__clear {
        border: 0;
        background: none;
        color: var(--muted);
        font-size: 1.15rem;
        line-height: 1;
        padding: 0 var(--sp-1);
        cursor: pointer;

        &:hover {
          color: var(--text);
        }
      }

      /* Fixed, not absolute: the back office's panels clip their content (overflow: hidden)
         and a picker near a panel's foot had its list cut to a row or two. The list is laid
         over the control's box on screen by place(). */
      .pick__list {
        position: fixed;
        z-index: var(--z-modal);
        margin: 0;
        padding: 6px;
        list-style: none;
        max-height: 17rem;
        overflow-y: auto;
        background: var(--surface);
        border: var(--line) solid var(--border-strong);
        border-radius: var(--radius-card);
        box-shadow: var(--shadow);
      }

      .pick__row {
        display: grid;
        grid-template-columns: 1fr auto;
        gap: 0 var(--sp-3);
        align-items: baseline;
        padding: var(--sp-2) var(--sp-3);
        border-radius: var(--radius-sm);
        cursor: pointer;
        transition: background var(--t-fast) ease;
      }

      .pick__row.is-active,
      .pick__row:hover,
      .pick__row.is-default {
        background: var(--surface-tint);
      }

      .pick__name {
        font-weight: 600;
        color: var(--text);
      }

      .pick__code {
        font-size: var(--fs-meta);
        color: var(--muted);
      }

      .pick__note {
        grid-column: 1 / -1;
        font-size: var(--fs-meta);
        color: var(--muted);
      }

      .pick__empty,
      .pick__more {
        padding: var(--sp-2) var(--sp-3);
        font-size: var(--fs-meta);
        color: var(--muted);
      }
    `,
  ],
})
export class ProductPickerComponent {
  readonly i18n = inject(I18nService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly host = inject(ElementRef);

  readonly products = input<readonly SearchableProduct[]>([]);
  /** The article on the line, so an existing document opens with its own articles shown. */
  readonly productId = input<string | null>(null);
  readonly inputId = input<string>('');
  readonly placeholder = input<string>('');
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly invalid = input(false, { transform: booleanAttribute });
  /** Empty the box after each pick and keep focus: the shape for adding lines in a row. */
  readonly clearOnPick = input(false, { transform: booleanAttribute });
  /** Read scans made while the page, rather than this box, has focus. */
  readonly catchScans = input(false, { transform: booleanAttribute });
  readonly limit = input(12);
  /** A line under each result — stock on hand, a price — decided by the screen using it. */
  readonly note = input<((product: SearchableProduct) => string | null) | null>(null);
  /**
   * Called when a scan matches nothing, before the code is called unknown: the article may
   * have been added a minute ago on another machine. The host decides what refreshing means
   * — the back office refetches its shared list — and returns the list to look in again.
   */
  readonly refresh = input<(() => Promise<readonly SearchableProduct[]>) | null>(null);

  readonly picked = output<SearchableProduct>();
  /**
   * The same article, emitted only when a person chose it from the list — a click, or Enter on a
   * row reached with the arrow keys — and never for a scan or an exact code typed in. A screen
   * that wants the next field after a deliberate choice listens to this; a scan keeps the focus
   * in the box so the next scan is read.
   */
  readonly pickedFromList = output<SearchableProduct>();
  /** Whether Enter would take the top row: a name is being typed, not a code. */
  readonly enterTakesFirst = computed(() => !!this.term().trim() && !looksLikeCode(this.term()));
  /** A scan that matched nothing, so the screen can say so in its own words. */
  readonly unknownScan = output<string>();

  readonly term = signal('');
  readonly open = signal(false);
  readonly active = signal(-1);

  /** Unique per instance: several pickers live on one contract, each with its own list. */
  readonly listId = `pick-${Math.random().toString(36).slice(2, 9)}`;

  /** Set just before a pick hands focus back, so that focus does not reopen the list. */
  private quietFocus = false;

  @ViewChild('box') private boxRef?: ElementRef<HTMLInputElement>;

  /**
   * What the search found. Typing nothing shows the head of the catalogue rather than an
   * empty panel, because a picker that looks empty until you type reads as broken.
   */
  readonly shown = computed(() => searchProducts(this.products(), this.term(), this.limit()));

  readonly more = computed(() => {
    const total = searchProducts(this.products(), this.term(), Number.MAX_SAFE_INTEGER).length;
    return Math.max(0, total - this.shown().length);
  });

  readonly activeId = computed(() =>
    this.open() && this.active() >= 0 ? `${this.listId}-${this.active()}` : null,
  );

  private readonly selected = computed(() => {
    const id = this.productId();
    return id ? (this.products().find((p) => p.id === id) ?? null) : null;
  });

  constructor() {
    // The box shows whatever is on the line until somebody types over it. Skipped in entry
    // mode, where the box is never a record of anything.
    effect(() => {
      const chosen = this.selected();
      if (this.clearOnPick()) return;
      if (!this.open()) this.term.set(chosen ? this.label(chosen) : '');
    });

    effect((onCleanup) => {
      if (!this.catchScans() || this.disabled()) return;
      const stop = listenForScans((code) => this.onScan(code));
      onCleanup(() => stop());
    });

    this.destroyRef.onDestroy(() => this.open.set(false));

    // Once the list is drawn, and again whenever its rows change height, lay it on the control.
    afterRenderEffect(() => {
      this.shown();
      if (this.open()) this.place();
    });
    // Any scroll box can carry the control away — the page, a panel, a dialog — and scroll does
    // not bubble, so listen in the capture phase.
    const follow = () => {
      if (this.open()) this.place();
    };
    document.addEventListener('scroll', follow, { capture: true, passive: true });
    window.addEventListener('resize', follow);
    this.destroyRef.onDestroy(() => {
      document.removeEventListener('scroll', follow, { capture: true });
      window.removeEventListener('resize', follow);
    });
  }

  /**
   * The list as wide as the control and just under it, or just over it when the screen has no
   * room underneath. Fixed coordinates are physical, but the list spans the control exactly,
   * so there is no reading direction to mirror.
   */
  private place(): void {
    const host = this.host.nativeElement as HTMLElement;
    const control = host.querySelector('.pick__control') as HTMLElement | null;
    const list = host.querySelector('.pick__list') as HTMLElement | null;
    if (!control || !list) return;
    const box = control.getBoundingClientRect();
    const gap = 4;
    const margin = 8;
    const height = list.offsetHeight;
    const viewHeight = document.documentElement.clientHeight;
    let top = box.bottom + gap;
    if (top + height > viewHeight - margin && box.top - gap - height >= margin) top = box.top - gap - height;
    list.style.top = `${top}px`;
    list.style.left = `${box.left}px`;
    list.style.width = `${box.width}px`;
  }

  name(product: SearchableProduct): string {
    return localizedName(product, this.i18n.lang()) || product.sku;
  }

  private label(product: SearchableProduct): string {
    return `${product.sku} — ${this.name(product)}`;
  }

  noteFor(product: SearchableProduct): string | null {
    return this.note()?.(product) ?? null;
  }

  focus(): void {
    setTimeout(() => this.boxRef?.nativeElement.focus(), 0);
  }

  onFocus(): void {
    // Focus given back by a pick in entry mode is not a request to browse: the list would
    // open on the head of the catalogue and sit over the line just added. It opens again on
    // the first keystroke.
    this.open.set(!this.quietFocus);
    this.quietFocus = false;
    this.active.set(-1);
    // Whatever is in the box is a record of the current choice, not a search anybody is
    // partway through, so the first keystroke should replace it rather than append to it.
    if (!this.clearOnPick()) setTimeout(() => this.boxRef?.nativeElement.select(), 0);
  }

  onBlur(): void {
    this.open.set(false);
    this.active.set(-1);
    // Half-typed text left in a field would claim an article that was never chosen.
    if (!this.clearOnPick()) {
      const chosen = this.selected();
      this.term.set(chosen ? this.label(chosen) : '');
    }
  }

  onType(value: string): void {
    this.term.set(value);
    this.open.set(true);
    this.active.set(-1);
  }

  clear(): void {
    this.term.set('');
    this.open.set(true);
    this.focus();
  }

  onKey(event: KeyboardEvent): void {
    const rows = this.shown().length;
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.open.set(true);
        if (rows) this.active.set((this.active() + 1) % rows);
        break;
      case 'ArrowUp':
        event.preventDefault();
        if (rows) this.active.set((this.active() <= 0 ? rows : this.active()) - 1);
        break;
      case 'Escape':
        if (this.open()) {
          // Swallowed only when there was a list to close, so Escape still reaches the dialog
          // this picker may be sitting in.
          event.preventDefault();
          event.stopPropagation();
          this.open.set(false);
        }
        break;
      case 'Enter':
        event.preventDefault();
        void this.commit();
        break;
      default:
        break;
    }
  }

  /**
   * Enter: take the highlighted row, or work out what the text means.
   *
   * <p>A scanner ends its burst with Enter, so this is also the scan handler for the case
   * where the box already had focus — the code is in the box by then and reads as an exact
   * barcode, which is the one thing `resolveEntry` never has to guess about.</p>
   */
  private async commit(): Promise<void> {
    const rows = this.shown();
    if (this.active() >= 0 && this.active() < rows.length) {
      this.choose(rows[this.active()], true);
      return;
    }

    const first = resolveEntry(this.products(), this.term());
    if (first.taken) {
      // A code read exactly is a scan: the box keeps the focus. A name narrowed to one is a choice.
      this.choose(first.taken, !first.exact);
      return;
    }
    // A name typed and Enter: the top of the list, the row marked as the one Enter takes. Never
    // for a code — a scanner types those, and an unknown code must stay unknown.
    if (first.shown.length && !looksLikeCode(this.term())) {
      this.choose(first.shown[0], true);
      return;
    }
    let taken: SearchableProduct | null = null;
    if (!first.shown.length) {
      // Nothing at all — the one case worth a trip to the server.
      const pool = await this.refreshed();
      if (pool) taken = resolveEntry(pool, this.term()).taken;
    }
    if (taken) this.choose(taken);
    else this.open.set(true);
  }

  /**
   * The catalogue after the host has refreshed it, or null when it will not or cannot.
   *
   * <p>Matched against the returned list, not against `products`: that input still holds
   * the old list until the next change detection, and a picker that refreshed and then
   * looked at the stale copy would call the new article unknown anyway.</p>
   */
  private async refreshed(): Promise<readonly SearchableProduct[] | null> {
    const refresh = this.refresh();
    if (!refresh) return null;
    try {
      return await refresh();
    } catch {
      // An unreachable server does not prove the article unknown, but "unknown" is the most
      // that can honestly be said; the operator scans again once the line is back.
      return null;
    }
  }

  choose(product: SearchableProduct, fromList = false): void {
    this.picked.emit(product);
    this.active.set(-1);

    if (this.clearOnPick()) {
      this.term.set('');
      // The box itself too. A scanner types the code and Enter inside one animation frame, and
      // change detection is coalesced to the frame, so the binding last rendered '' and sees no
      // change: the code would stay in the box and the next scan would be appended to it.
      if (this.boxRef) this.boxRef.nativeElement.value = '';
      this.open.set(false);
      this.quietFocus = true;
      this.focus();
    } else {
      this.term.set(this.label(product));
      this.open.set(false);
    }
    // Last, after the box has queued taking its focus back, so a screen that moves the cursor
    // on from here (a timer queued now runs after the box's) has the final word.
    if (fromList) this.pickedFromList.emit(product);
  }

  /**
   * A scan that arrived while something else had focus.
   *
   * <p>Only an exact code counts here. The cashier is not looking at this box — they are
   * looking at the goods — so a near miss must not quietly put a different article on the
   * document; the code goes in the box and they are told what became of it.</p>
   */
  private async onScan(code: string): Promise<void> {
    let exact = matchCode(this.products(), code);
    if (!exact.length) {
      const pool = await this.refreshed();
      if (pool) exact = matchCode(pool, code);
    }
    if (exact.length === 1) {
      this.choose(exact[0]);
      return;
    }

    this.term.set(code);

    setTimeout(() => {
      const box = this.boxRef?.nativeElement;
      // Selected, not merely focused. The box now has focus, so the next scan is typed into
      // it rather than caught at the document — and appended to the code already there unless
      // the old one is selected for replacing. Two bad scans would make one nonsense line.
      box?.focus();
      box?.select();

      // After the focus, which opens the list on its own. Two articles carrying one code: the
      // list is the only way to ask which is meant. Nothing at all: an empty list says the
      // same as the message below the box and floats over it, so it stays shut.
      this.open.set(exact.length > 1);
    }, 0);

    if (!exact.length) this.unknownScan.emit(code);
  }
}
