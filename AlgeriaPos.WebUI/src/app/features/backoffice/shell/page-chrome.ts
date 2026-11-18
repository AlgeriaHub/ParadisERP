import { Directive, Injectable, OnDestroy, OnInit, TemplateRef, inject, signal } from '@angular/core';

/**
 * Lets a section put its primary action and its filters into the page header, where every page
 * keeps them (REDESIGN_PLAN.md §6.4), although the header is drawn by `BackofficePageComponent`
 * and not by the section.
 *
 * ```html
 * <ng-template appPageActions><button class="btn-primary">…</button></ng-template>
 * <ng-template appPageToolbar>…search, filters…</ng-template>
 * ```
 *
 * The templates keep the section's own context, so their bindings and handlers are the
 * section's. Provided per page, so two pages never share a header.
 */
@Injectable()
export class PageChrome {
  readonly actions = signal<TemplateRef<unknown> | null>(null);
  readonly toolbar = signal<TemplateRef<unknown> | null>(null);
  /** A one-line description, when it helps (§6). */
  readonly description = signal<string | null>(null);
  /**
   * A title for an entity page (a contract's number) that replaces the page's name; the
   * breadcrumb then ends on the page's name as a step back.
   */
  readonly heading = signal<string | null>(null);
  /**
   * Steps between the page and the entity on screen, e.g. the contract a note belongs to. The
   * page's own crumb links back to its list whenever an entity is open.
   */
  readonly trail = signal<Crumb[]>([]);
}

export interface Crumb {
  label: string;
  /** Absolute router link; omitted for the step the reader is on. */
  link?: string;
  query?: Record<string, string>;
}

@Directive({ selector: 'ng-template[appPageActions]', standalone: true })
export class PageActionsDirective implements OnInit, OnDestroy {
  private readonly chrome = inject(PageChrome, { optional: true });
  private readonly template = inject(TemplateRef<unknown>);

  ngOnInit(): void {
    // Set after the header's own check, so the header picks it up on the next pass.
    queueMicrotask(() => this.chrome?.actions.set(this.template));
  }

  ngOnDestroy(): void {
    if (this.chrome?.actions() === this.template) this.chrome.actions.set(null);
  }
}

@Directive({ selector: 'ng-template[appPageToolbar]', standalone: true })
export class PageToolbarDirective implements OnInit, OnDestroy {
  private readonly chrome = inject(PageChrome, { optional: true });
  private readonly template = inject(TemplateRef<unknown>);

  ngOnInit(): void {
    queueMicrotask(() => this.chrome?.toolbar.set(this.template));
  }

  ngOnDestroy(): void {
    if (this.chrome?.toolbar() === this.template) this.chrome.toolbar.set(null);
  }
}

export const PAGE_CHROME = [PageActionsDirective, PageToolbarDirective] as const;
