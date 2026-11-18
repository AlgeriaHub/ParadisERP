import {
  AfterViewInit, ChangeDetectorRef, Component, DestroyRef, DOCUMENT, ElementRef,
  NgZone, ViewChild, computed, inject, signal,
} from '@angular/core';
import { NavigationStart, Router } from '@angular/router';
import { I18nService } from '../core/i18n.service';
import { fold } from '../core/product-search';

interface Choice {
  option: HTMLOptionElement;
  label: string;
  group: string;
  index: number;
  disabled: boolean;
}

/**
 * Progressive enhancement for every single-choice native select, including lazy routes.
 * The native control remains the source of truth: selecting an original option and emitting
 * its normal change event preserves Angular's [value], [ngValue], validators and handlers.
 * No option values are parsed or reconstructed and searching never changes the selection.
 * Older browsers without popovers retain the native picker.
 */
@Component({
  selector: 'app-select-search',
  standalone: true,
  template: `
    <div #panel id="select-search-panel" popover="manual" class="select-search-panel"
      [attr.dir]="direction()" (click)="$event.stopPropagation()">
      <div class="search-head">
        <label for="select-search-input">{{ label() }}</label>
        <button type="button" class="search-close" [attr.aria-label]="i18n.t('common.close')"
          (click)="close(true)">×</button>
      </div>
      <input #search id="select-search-input" type="search" role="combobox"
        autocomplete="off" spellcheck="false" aria-autocomplete="list"
        [attr.aria-expanded]="opened()" aria-controls="select-search-list"
        [attr.aria-activedescendant]="activeId()" [value]="query()"
        [placeholder]="i18n.t('select.search')" (input)="filter($any($event.target).value)" />
      <div class="search-results" #results>
        <ul id="select-search-list" role="listbox" [attr.aria-label]="label()">
          @for (choice of filtered(); track choice.option) {
            <li role="option" [id]="'select-search-option-' + choice.index"
              [attr.aria-selected]="choice.option.selected" [attr.aria-disabled]="choice.disabled"
              [class.active]="choice.index === active()" [class.selected]="choice.option.selected"
              (pointerdown)="$event.preventDefault()" (click)="choose(choice)">
              <span class="option-label">{{ choice.label }}</span>
              @if (choice.group) { <span class="option-group">{{ choice.group }}</span> }
              @if (choice.option.selected) { <span class="option-check" aria-hidden="true">✓</span> }
            </li>
          }
        </ul>
        @if (!filtered().length) { <p class="search-empty" role="status">{{ i18n.t('select.empty') }}</p> }
      </div>
      <span class="sr-only" role="status" aria-live="polite">{{ i18n.t('select.count', { n: filtered().length }) }}</span>
    </div>
  `,
  styleUrl: './select-search.component.scss',
})
export class SelectSearchComponent implements AfterViewInit {
  readonly i18n = inject(I18nService);
  private readonly doc = inject(DOCUMENT);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly zone = inject(NgZone);
  private readonly cdr = inject(ChangeDetectorRef);
  private readonly destroy = inject(DestroyRef);
  private readonly router = inject(Router);
  @ViewChild('panel', { static: true }) panel!: ElementRef<HTMLDivElement>;
  @ViewChild('search', { static: true }) search!: ElementRef<HTMLInputElement>;
  @ViewChild('results', { static: true }) results!: ElementRef<HTMLDivElement>;
  readonly opened = signal(false);
  readonly query = signal('');
  readonly label = signal('');
  readonly direction = signal('ltr');
  readonly choices = signal<Choice[]>([]);
  readonly active = signal(-1);
  readonly filtered = computed(() => {
    const terms = fold(this.query()).split(/\s+/).filter(Boolean);
    return this.choices().filter(c => terms.every(t => fold(c.label + ' ' + c.group).includes(t)));
  });
  readonly activeId = computed(() => this.active() < 0 ? null : `select-search-option-${this.active()}`);
  private select: HTMLSelectElement | null = null;
  private observer?: MutationObserver;
  private savedAttributes = new Map<string, string | null>();

  ngAfterViewInit(): void {
    if (typeof this.panel.nativeElement.showPopover !== 'function') return;
    // Capture Escape before the underlying payment/dialog handles it. One listener also
    // covers selects created later by @if, lazy routes, or editable table rows.
    const listen = (target: EventTarget, name: string, handler: EventListener, capture = false) => {
      target.addEventListener(name, handler, capture);
      this.destroy.onDestroy(() => target.removeEventListener(name, handler, capture));
    };
    this.zone.runOutsideAngular(() => {
      listen(this.doc, 'pointerdown', e => this.onPointer(e as PointerEvent), true);
      listen(this.doc, 'click', e => this.onClick(e as MouseEvent), true);
      listen(this.doc, 'keydown', e => this.onKey(e as KeyboardEvent), true);
      listen(this.doc, 'focusin', e => {
        if (this.select && e.target !== this.select && !this.panel.nativeElement.contains(e.target as Node)) {
          this.zone.run(() => this.close(false));
        }
      });
      listen(this.doc, 'scroll', e => {
        if (this.select && !this.panel.nativeElement.contains(e.target as Node)) this.place();
      }, true);
      const win = this.doc.defaultView!;
      listen(win, 'resize', () => this.place());
      if (win.visualViewport) {
        listen(win.visualViewport, 'resize', () => this.place());
        listen(win.visualViewport, 'scroll', () => this.place());
      }
    });
    const sub = this.router.events.subscribe(e => {
      if (e instanceof NavigationStart) this.close(false);
    });
    this.destroy.onDestroy(() => { sub.unsubscribe(); this.close(false); });
  }

  private eligible(target: EventTarget | null): target is HTMLSelectElement {
    return target instanceof HTMLSelectElement && !target.disabled && !target.matches(':disabled')
      && !target.multiple && target.size <= 1 && !target.closest('[inert]')
      && target.dataset['searchable'] !== 'false';
  }

  private onPointer(event: PointerEvent): void {
    if (event.button !== 0) return;
    if (this.eligible(event.target)) {
      event.preventDefault();
      const select = event.target;
      this.zone.run(() => this.openFor(select));
    } else if (this.select && !this.panel.nativeElement.contains(event.target as Node)) {
      this.zone.run(() => this.close(false));
    }
  }

  private onClick(event: MouseEvent): void {
    if (!this.eligible(event.target)) return;
    event.preventDefault();
    // Assistive technology may activate a control without a pointerdown.
    if (this.select !== event.target) {
      const select = event.target;
      this.zone.run(() => this.openFor(select));
    }
  }

  private onKey(event: KeyboardEvent): void {
    if (event.isComposing) return;
    if (!this.select) {
      if (!this.eligible(event.target) || event.ctrlKey || event.metaKey) return;
      const typed = event.key.length === 1 && event.key !== ' ' && !event.altKey;
      if (!typed && !['Enter', ' ', 'ArrowDown', 'ArrowUp', 'F4'].includes(event.key)) return;
      event.preventDefault(); event.stopPropagation();
      const select = event.target;
      this.zone.run(() => this.openFor(select, typed ? event.key : ''));
      return;
    }
    if (!this.panel.nativeElement.contains(event.target as Node) && event.target !== this.select) return;
    if (event.key === 'Escape') {
      event.preventDefault(); event.stopPropagation();
      this.zone.run(() => this.close(true));
    } else if (event.key === 'Tab') {
      // Return to the original position in the form, then let Tab advance normally.
      this.zone.run(() => this.close(true));
    } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault(); event.stopPropagation();
      this.zone.run(() => this.move(event.key === 'ArrowDown' ? 1 : -1));
    } else if (event.key === 'Enter') {
      event.preventDefault(); event.stopPropagation();
      const choice = this.filtered().find(c => c.index === this.active());
      if (choice) this.zone.run(() => this.choose(choice));
    }
  }

  openFor(select: HTMLSelectElement, query = ''): void {
    if (!this.eligible(select)) return;
    this.close(false);
    this.select = select;
    select.focus({ preventScroll: true });
    this.query.set(query);
    const labelledBy = (select.getAttribute('aria-labelledby') || '').split(/\s+/)
      .map(id => this.doc.getElementById(id)?.textContent?.trim()).filter(Boolean).join(' ');
    this.label.set(select.getAttribute('aria-label') || labelledBy
      || Array.from(select.labels || []).map(l => {
        const copy = l.cloneNode(true) as HTMLElement;
        copy.querySelectorAll('select, input, button').forEach(el => el.remove());
        return copy.textContent?.trim();
      }).filter(Boolean).join(' ') || this.i18n.t('select.label'));
    this.direction.set(getComputedStyle(select).direction);
    for (const [name, value] of [['aria-expanded', 'true'], ['aria-controls', 'select-search-list']]) {
      this.savedAttributes.set(name, select.getAttribute(name));
      select.setAttribute(name, value);
    }
    // A popup belonging to a modal must stay within that modal's focus/inert boundary.
    const parent = select.closest('dialog, [role="dialog"]') || this.host.nativeElement;
    parent.appendChild(this.panel.nativeElement);
    this.refresh();
    this.opened.set(true);
    this.cdr.detectChanges();
    this.panel.nativeElement.showPopover();
    this.place();
    this.search.nativeElement.focus({ preventScroll: true });
    this.scrollActive();
    this.observer = new MutationObserver(() => this.zone.run(() => {
      if (!this.select || !this.eligible(this.select)) { this.close(false); return; }
      this.refresh(); this.cdr.detectChanges(); this.place();
    }));
    this.observer.observe(select, { childList: true, subtree: true, characterData: true, attributes: true,
      attributeFilter: ['disabled', 'hidden', 'label', 'value', 'selected'] });
  }

  private refresh(): void {
    if (!this.select) return;
    this.choices.set(Array.from(this.select.options).filter(o => !o.hidden && !o.closest('optgroup[hidden]'))
      .map(option => ({ option, label: option.label || '—', index: option.index,
        group: option.parentElement instanceof HTMLOptGroupElement ? option.parentElement.label : '',
        disabled: option.disabled || !!option.closest('optgroup[disabled]') })));
    const enabled = this.filtered().filter(c => !c.disabled);
    this.active.set((enabled.find(c => c.option.selected) || enabled[0])?.index ?? -1);
  }

  filter(value: string): void {
    this.query.set(value);
    this.active.set(this.filtered().find(c => !c.disabled)?.index ?? -1);
    this.cdr.detectChanges(); this.scrollActive(); this.place();
  }

  private move(delta: number): void {
    const enabled = this.filtered().filter(c => !c.disabled);
    if (!enabled.length) return;
    const current = enabled.findIndex(c => c.index === this.active());
    const index = current < 0 ? (delta > 0 ? 0 : enabled.length - 1)
      : Math.max(0, Math.min(enabled.length - 1, current + delta));
    this.active.set(enabled[index].index);
    this.cdr.detectChanges(); this.scrollActive();
  }

  private scrollActive(): void {
    this.doc.getElementById(this.activeId() || '')?.scrollIntoView({ block: 'nearest' });
  }

  choose(choice: Choice): void {
    const select = this.select;
    if (!select || !select.isConnected || !this.eligible(select) || choice.option.disabled
      || choice.option.closest('optgroup[disabled]') || !select.contains(choice.option)) return;
    const changed = select.selectedIndex !== choice.option.index;
    select.selectedIndex = choice.option.index;
    this.close(true);
    if (changed) {
      select.dispatchEvent(new Event('input', { bubbles: true }));
      select.dispatchEvent(new Event('change', { bubbles: true }));
    }
  }

  close(restoreFocus: boolean): void {
    this.observer?.disconnect(); this.observer = undefined;
    const select = this.select;
    this.select = null;
    if (select) {
      for (const [name, value] of this.savedAttributes) {
        if (value === null) select.removeAttribute(name); else select.setAttribute(name, value);
      }
    }
    this.savedAttributes.clear();
    const panel = this.panel?.nativeElement;
    if (panel?.matches(':popover-open')) panel.hidePopover();
    if (panel && panel.parentElement !== this.host.nativeElement) this.host.nativeElement.appendChild(panel);
    this.opened.set(false);
    if (restoreFocus && select?.isConnected) select.focus({ preventScroll: true });
  }

  private place(): void {
    const select = this.select;
    if (!select) return;
    if (!select.isConnected || !select.getClientRects().length || select.closest('[inert]')) {
      this.zone.run(() => this.close(false)); return;
    }
    const panel = this.panel.nativeElement;
    const rect = select.getBoundingClientRect();
    const viewport = this.doc.defaultView!.visualViewport;
    const left = viewport?.offsetLeft || 0;
    const top = viewport?.offsetTop || 0;
    const width = viewport?.width || this.doc.documentElement.clientWidth;
    const height = viewport?.height || this.doc.documentElement.clientHeight;
    const gap = 8;
    const popupWidth = Math.min(Math.max(rect.width, 280), width - gap * 2);
    const x = Math.max(left + gap, Math.min(this.direction() === 'rtl' ? rect.right - popupWidth : rect.left,
      left + width - popupWidth - gap));
    panel.style.inlineSize = `${popupWidth}px`;
    panel.style.insetInlineStart = `${this.direction() === 'rtl' ? this.doc.documentElement.clientWidth - x - popupWidth : x}px`;
    const below = Math.max(0, top + height - rect.bottom - gap * 2);
    const above = Math.max(0, rect.top - top - gap * 2);
    const openAbove = below < 260 && above > below;
    const available = Math.max(0, Math.min(height - gap * 2, openAbove ? above : below));
    panel.style.maxBlockSize = `${available}px`;
    const panelHeight = panel.getBoundingClientRect().height;
    panel.style.insetBlockStart = `${openAbove ? Math.max(top + gap, rect.top - gap - panelHeight) : rect.bottom + gap}px`;
  }
}
