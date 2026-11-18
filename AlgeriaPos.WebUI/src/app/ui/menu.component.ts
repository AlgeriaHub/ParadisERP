import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Directive,
  ElementRef,
  HostListener,
  contentChildren,
  forwardRef,
  inject,
  input,
  signal,
} from '@angular/core';

// Declared before MenuComponent, which queries them in a field initialiser: a class used before
// its declaration is in its temporal dead zone in a JIT (test) build.
/** One choice in an `app-menu`. A button or a link; it closes the menu once chosen. */
@Directive({
  selector: '[appMenuItem]',
  standalone: true,
  host: {
    role: 'menuitem',
    tabindex: '-1',
    class: 'app-menu-item',
    '(click)': 'menu.chosen()',
  },
})
export class MenuItemDirective {
  readonly element = inject(ElementRef<HTMLElement>).nativeElement as HTMLElement;
  readonly menu = inject(forwardRef(() => MenuComponent));
}

/** A rule between groups of items: destructive choices sit last, after one. */
@Directive({
  selector: '[appMenuSeparator]',
  standalone: true,
  host: { role: 'separator', class: 'app-menu-separator' },
})
export class MenuSeparatorDirective {}

let nextId = 0;

/**
 * A menu button (WAI-ARIA menu-button pattern) — the ⋯ of a table row, the user menu of the
 * header, the branch menu.
 *
 * ```html
 * <app-menu [label]="i18n.t('ui.menu.more')">
 *   <span menuTrigger aria-hidden="true">⋯</span>
 *   <button appMenuItem (click)="edit()">…</button>
 *   <hr appMenuSeparator />
 *   <button appMenuItem class="is-danger" (click)="remove()">…</button>
 * </app-menu>
 * ```
 *
 * Opens on click, Enter, Space or ArrowDown; arrows, Home and End move between items; Escape
 * closes and gives focus back to the trigger; Tab, a click outside or choosing an item closes it.
 * The panel lines up with the trigger's inline-end edge (start with `align="start"`), so it
 * mirrors under RTL, and floats above any scroll box so a table row cannot clip it.
 */
@Component({
  selector: 'app-menu',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      #trigger
      type="button"
      class="trigger"
      [class]="triggerClass()"
      aria-haspopup="menu"
      [attr.aria-expanded]="open()"
      [attr.aria-controls]="panelId"
      [attr.aria-label]="label() || null"
      [attr.title]="label() || null"
      (click)="toggle()"
      (keydown)="onTriggerKey($event)"
    >
      <ng-content select="[menuTrigger]" />
    </button>
    <div
      class="panel"
      role="menu"
      [id]="panelId"
      [hidden]="!open()"
      [class.placed]="placed()"
      [style.top.px]="spot()?.top"
      [style.left.px]="spot()?.left"
      [attr.aria-label]="label() || null"
      (keydown)="onMenuKey($event)"
    >
      <ng-content />
    </div>
  `,
  styles: [
    `
      :host {
        position: relative;
        display: inline-flex;
      }
      .trigger {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-2);
      }
      @keyframes menu-pop {
        from {
          translate: 0 4px;
        }
        to {
          translate: none;
        }
      }
      /* Fixed, not absolute: a row's ⋯ sits inside a table's scroll box, which would clip an
         absolute panel to the row. The panel is placed from the trigger's box on screen
         (place()), and stays transparent for the one frame before it has been measured —
         opacity, not visibility, so the first item can already take focus. */
      .panel {
        position: fixed;
        opacity: 0;
        z-index: var(--z-modal);
        min-inline-size: 12rem;
        padding: 0.5rem;
        background: var(--surface);
        color: var(--text);
        border: var(--line) solid var(--border);
        border-radius: 1rem;
        box-shadow: var(--shadow-pop);
        display: grid;
        gap: 2px;
      }
      .panel.placed {
        opacity: 1;
        animation: menu-pop 140ms cubic-bezier(0.2, 0, 0, 1) both;
      }
      @media (prefers-reduced-motion: reduce) {
        .panel.placed {
          animation: none;
        }
      }
      .panel[hidden] {
        display: none;
      }
    `,
  ],
  host: { '[class.menu-start]': 'align() === "start"' },
})
export class MenuComponent {
  /** The trigger's accessible name; required when the visible trigger is a glyph such as ⋯. */
  readonly label = input<string>('');
  readonly triggerClass = input<string>('btn-ghost');
  /** Which edge of the trigger the panel lines up with: `end` (a row's ⋯), or `start`. */
  readonly align = input<'start' | 'end'>('end');

  readonly open = signal(false);
  readonly panelId = `app-menu-${nextId++}`;
  /** Where the fixed panel sits on screen; null until it has been measured. */
  readonly spot = signal<{ top: number; left: number } | null>(null);
  readonly placed = signal(false);

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly items = contentChildren(MenuItemDirective, { descendants: true });

  toggle(): void {
    if (this.open()) this.close(false);
    else this.show(0);
  }

  show(focusIndex: number): void {
    this.placed.set(false);
    this.open.set(true);
    // After change detection has un-hidden the panel: a hidden item cannot take focus, an
    // unrendered panel has no size, and a microtask can run before the render.
    setTimeout(() => {
      this.place();
      this.focusItem(focusIndex);
    });
  }

  close(returnFocus = true): void {
    if (!this.open()) return;
    this.open.set(false);
    this.placed.set(false);
    if (returnFocus) this.trigger()?.focus();
  }

  constructor() {
    // Any scroll box can move the trigger — the page, the back office's content pane, a wide
    // table — and scroll does not bubble, so listen in the capture phase.
    const onScroll = () => this.onViewportChange();
    document.addEventListener('scroll', onScroll, { capture: true, passive: true });
    inject(DestroyRef).onDestroy(() => document.removeEventListener('scroll', onScroll, { capture: true }));
  }

  /** A page or table scrolled under an open menu, or the window resized: keep the panel on its trigger. */
  @HostListener('window:resize')
  onViewportChange(): void {
    if (this.open()) this.place();
  }

  /**
   * Lines the panel up with the trigger's inline-end edge (or start, for `align="start"`),
   * below it, or above it when the screen has no room underneath. Fixed coordinates are
   * physical by nature, so the reading direction is read here rather than mirrored by CSS.
   */
  private place(): void {
    const trigger = this.trigger();
    const panel = this.host.nativeElement.querySelector('.panel') as HTMLElement | null;
    if (!trigger || !panel) return;
    const box = trigger.getBoundingClientRect();
    const width = panel.offsetWidth;
    const height = panel.offsetHeight;
    const viewWidth = document.documentElement.clientWidth;
    const viewHeight = document.documentElement.clientHeight;
    const gap = 4;
    const margin = 8;

    const rtl = getComputedStyle(this.host.nativeElement).direction === 'rtl';
    const toEnd = (this.align() === 'end') !== rtl;
    let left = toEnd ? box.right - width : box.left;
    left = Math.min(Math.max(left, margin), Math.max(margin, viewWidth - width - margin));

    let top = box.bottom + gap;
    if (top + height > viewHeight - margin && box.top - gap - height >= margin) top = box.top - gap - height;

    this.spot.set({ top, left });
    this.placed.set(true);
  }

  onTriggerKey(event: KeyboardEvent): void {
    if (event.key === 'ArrowDown' || event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.show(0);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.show(this.enabled().length - 1);
    }
  }

  onMenuKey(event: KeyboardEvent): void {
    const items = this.enabled();
    const current = items.indexOf(document.activeElement as HTMLElement);
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        items[(current + 1) % items.length]?.focus();
        break;
      case 'ArrowUp':
        event.preventDefault();
        items[(current - 1 + items.length) % items.length]?.focus();
        break;
      case 'Home':
        event.preventDefault();
        items[0]?.focus();
        break;
      case 'End':
        event.preventDefault();
        items[items.length - 1]?.focus();
        break;
      case 'Escape':
        event.preventDefault();
        this.close();
        break;
      case 'Tab':
        this.close(false);
        break;
    }
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) this.close(false);
  }

  /** Called by an item once it has been chosen. */
  chosen(): void {
    this.close();
  }

  private enabled(): HTMLElement[] {
    return this.items()
      .map((i) => i.element)
      .filter((el) => !(el as HTMLButtonElement).disabled);
  }

  private focusItem(index: number): void {
    const items = this.enabled();
    // preventScroll: focusing the first item must not scroll the page out from under the panel.
    items[Math.max(0, Math.min(index, items.length - 1))]?.focus({ preventScroll: true });
  }

  private trigger(): HTMLButtonElement | null {
    return this.host.nativeElement.querySelector('.trigger');
  }
}

export const MENU = [MenuComponent, MenuItemDirective, MenuSeparatorDirective] as const;
