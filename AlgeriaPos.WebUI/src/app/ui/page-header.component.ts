import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ModuleIcon } from '../core/nav';
import { IconComponent } from './icon.component';

/** A breadcrumb step: text, or a link back to where it names. */
export type HeaderCrumb = string | { label: string; link?: string; query?: Record<string, string> };

/**
 * The head of every back-office page (REDESIGN_PLAN.md §6.4):
 *
 * ```
 * Module / Page                                         (breadcrumb)
 * [icon] Page title                           [primary action]
 * One line, only when it helps.          Scope: Branch: Central depot
 * [toolbar: search, filters, secondary actions]
 * ```
 *
 * The primary action goes in `[pageActions]`, filters in `[pageToolbar]`. The scope label says
 * whose data the page shows — one branch, all branches, the group, or the company — so branch
 * data is never mixed without saying so (§8.3).
 */
@Component({
  selector: 'app-page-header',
  standalone: true,
  imports: [RouterLink, IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (crumbs().length) {
      <nav class="crumbs" [attr.aria-label]="crumbsLabel()">
        <ol>
          @for (c of crumbs(); track $index) {
            @if (asLink(c); as l) {
              <li><a [routerLink]="l.link" [queryParams]="l.query ?? null">{{ l.label }}</a></li>
            } @else {
              <li>{{ label(c) }}</li>
            }
          }
        </ol>
      </nav>
    }
    <div class="row">
      @if (icon(); as name) {
        <span class="page-icon" aria-hidden="true"><app-icon [name]="name" /></span>
      }
      <div class="text">
        <h1 class="title">{{ heading() }}</h1>
        @if (description()) {
          <p class="lead">{{ description() }}</p>
        }
      </div>
      <div class="actions"><ng-content select="[pageActions]" /></div>
    </div>
    @if (scope()) {
      <p class="scope"><span class="scope__dot" aria-hidden="true"></span>{{ scope() }}</p>
    }
    <div class="toolbar"><ng-content select="[pageToolbar]" /></div>
  `,
  styles: [
    `
      :host {
        display: grid;
        gap: var(--sp-3);
        padding-block-end: var(--sp-5);
      }
      .crumbs ol {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-2);
        margin: 0;
        padding: 0;
        list-style: none;
        font-size: 12.5px;
        font-weight: 500;
        color: var(--muted);
      }
      /* › is a bidi-mirrored character: the browser draws it as ‹ in Arabic, so the separator
         follows the reading direction with no rule of ours (a second flip would undo it). */
      .crumbs li + li::before {
        content: '›';
        margin-inline-end: var(--sp-2);
        color: var(--border-strong);
      }
      .crumbs a {
        color: var(--muted);
        text-decoration: none;
      }
      .crumbs a:hover {
        color: var(--link);
        text-decoration: underline;
      }
      .row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-4);
      }
      .page-icon {
        display: none;
        place-items: center;
        flex: none;
        inline-size: 2.5rem;
        block-size: 2.5rem;
        border-radius: var(--radius);
        background: linear-gradient(135deg, var(--tone-500), var(--tone-700));
        color: var(--on-tone);
        box-shadow: var(--shadow-sm);
      }
      /* phones keep the room for the title */
      @media (min-width: 640px) {
        .page-icon {
          display: grid;
        }
      }
      .text {
        min-inline-size: 0;
        flex: 1 1 16rem;
      }
      .title {
        margin: 0;
        font-family: var(--font-display);
        font-size: clamp(20px, 1.15rem + 0.35vw, 24px);
        font-weight: 700;
        letter-spacing: 0;
        line-height: 1.3;
        color: var(--text);
      }
      .lead {
        margin: 2px 0 0;
        font-size: 13px;
        color: var(--muted);
        max-inline-size: 72ch;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-3);
      }
      .actions:empty,
      .toolbar:empty {
        display: none;
      }
      .scope {
        display: inline-flex;
        align-items: center;
        gap: 0.375rem;
        min-block-size: 1.5rem;
        padding: 0.125rem 0.625rem;
        border-radius: var(--radius-pill);
        border: 1px solid var(--tone-200);
        background: var(--tone-50);
        color: var(--tone-ink);
        font-size: 12px;
        font-weight: 600;
        white-space: nowrap;
        margin: 0;
        justify-self: start;
      }
      .scope__dot {
        inline-size: 6px;
        block-size: 6px;
        border-radius: 50%;
        background: var(--tone-500);
        flex: none;
      }
      .toolbar {
        position: relative;
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        gap: var(--sp-4);
        padding: 1rem;
        border: 1px solid var(--tone-100);
        border-radius: var(--radius-card);
        box-shadow: var(--shadow);
        /* calc(90deg * var(--sx)): the wash starts at the inline-start edge in both directions (--sx is -1 in RTL). */
        background: linear-gradient(calc(90deg * var(--sx)), var(--tone-50), var(--surface) 55%);
      }
      .toolbar::before {
        content: '';
        position: absolute;
        inset-block-start: 1.25rem;
        inset-inline-start: -1px;
        inline-size: 4px;
        block-size: 2.25rem;
        border-start-end-radius: 999px;
        border-end-end-radius: 999px;
        background: var(--tone-500);
        pointer-events: none;
      }
    `,
  ],
})
export class PageHeaderComponent {
  readonly heading = input.required<string>();
  /** Already translated, outermost first: the module, then the page (the title repeats the last). */
  readonly crumbs = input<HeaderCrumb[]>([]);
  readonly crumbsLabel = input<string>('');
  readonly description = input<string | null>(null);
  /** Already translated, e.g. "Branch: Central depot" or "Company-wide". */
  readonly scope = input<string | null>(null);
  readonly icon = input<ModuleIcon | null>(null);

  asLink(c: HeaderCrumb): { label: string; link: string; query?: Record<string, string> } | null {
    return typeof c === 'object' && c.link ? { label: c.label, link: c.link, query: c.query } : null;
  }

  label(c: HeaderCrumb): string {
    return typeof c === 'string' ? c : c.label;
  }
}
