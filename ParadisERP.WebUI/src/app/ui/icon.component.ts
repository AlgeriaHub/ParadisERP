import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ModuleIcon } from '../core/nav';

/**
 * The module icons — the one exception to "no icon set" (REDESIGN_PLAN.md decision D1).
 *
 * - **Modules only.** One drawing per sidebar module; no page, action, header control or till
 *   control gets one. Those keep text glyphs.
 * - **Never alone.** The icon is decorative (`aria-hidden`). In the expanded sidebar its label
 *   sits beside it; in the explicitly collapsed sidebar the link carries the label as
 *   `aria-label` and a tooltip.
 * - **No library.** Hand-drawn 20×20 strokes in `currentColor`, so they take the colour of the
 *   text they sit beside and cost nothing offline.
 * - **No direction.** Every drawing is symmetric or has no reading order, so nothing mirrors
 *   under RTL; arrows stay text glyphs swapped by direction.
 */
const PATHS: Record<ModuleIcon, string> = {
  // Four panes: an overview.
  dashboard: 'M3 3h6v6H3z M11 3h6v6h-6z M3 11h6v6H3z M11 11h6v6h-6z',
  // A till: the drawer and the screen above it.
  pos: 'M3 10h14v7H3z M5 10V4h10v6 M7 13.5h2 M11 13.5h2',
  // A scaffold frame — what the company rents.
  rentals: 'M4 3v14 M16 3v14 M4 6h12 M4 14h12 M4 6l12 8 M16 6L4 14',
  // A receipt with a torn foot.
  sales: 'M5 2.5h10v15l-2.5-1.5-2.5 1.5-2.5-1.5L5 17.5z M8 7h4 M8 10h4',
  // One person.
  customers: 'M10 4a3 3 0 1 1 0 6a3 3 0 1 1 0-6z M4 17c0-3.3 2.7-5 6-5s6 1.7 6 5',
  // A box.
  inventory: 'M3 6l7-3 7 3v8l-7 3-7-3z M3 6l7 3 7-3 M10 9v8',
  // A banknote.
  finance: 'M2 5h16v10H2z M10 7.5a2.5 2.5 0 1 1 0 5a2.5 2.5 0 1 1 0-5z',
  // A staff badge.
  employees: 'M5 3h10v14H5z M10 6.5a2 2 0 1 1 0 4a2 2 0 1 1 0-4z M7 14.5c.6-1.5 1.7-2.2 3-2.2s2.4.7 3 2.2',
  // A workshop with two chimneys, drawn symmetric so nothing mirrors.
  manufacturing: 'M3 17h14 M4 17V8h12v9 M6 8V4h2v4 M12 8V4h2v4 M7 12h2 M11 12h2',
  // An open-ended spanner standing upright: the jaw above, the handle below, symmetric so nothing mirrors.
  maintenance: 'M7 2.5v3.5a3 3 0 0 0 6 0V2.5 M10 9v8.5 M8.5 17.5h3',
  // Three bars of different heights: figures, with no rising or falling order to mirror.
  reports: 'M3 17h14 M5.5 17v-6 M10 17V5 M14.5 17v-8',
  // A gear, drawn as a hub and eight teeth.
  admin:
    'M10 6.5a3.5 3.5 0 1 1 0 7a3.5 3.5 0 1 1 0-7z M10 2v2.5 M10 15.5V18 M2 10h2.5 M15.5 10H18 M4.3 4.3l1.8 1.8 M13.9 13.9l1.8 1.8 M4.3 15.7l1.8-1.8 M13.9 6.1l1.8-1.8',
};

@Component({
  selector: 'app-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg viewBox="0 0 20 20" width="20" height="20" aria-hidden="true" focusable="false">
      <path [attr.d]="d()" />
    </svg>
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        flex: none;
        inline-size: 1.25rem;
        block-size: 1.25rem;
      }
      svg {
        inline-size: 100%;
        block-size: 100%;
        fill: none;
        stroke: currentColor;
        stroke-width: 1.6;
        stroke-linecap: round;
        stroke-linejoin: round;
      }
    `,
  ],
})
export class IconComponent {
  readonly name = input.required<ModuleIcon>();
  readonly d = computed(() => PATHS[this.name()]);
}

/** Exported for the spec: every module the navigation names has a drawing. */
export const ICON_NAMES = Object.keys(PATHS) as ModuleIcon[];
