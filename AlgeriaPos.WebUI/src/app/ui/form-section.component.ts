import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { FoldComponent } from './fold.component';

/**
 * One titled group of a long form (REDESIGN_PLAN.md §11): *Basic information*, *Commercial*,
 * *Rental*, *Tax*… — only the sections a form's real fields fill. The fields go in a 1–3 column
 * grid that stacks on narrow widths, labels above controls so the layout mirrors for free.
 *
 * It folds like every long form's section (`app-fold`), but **opens open**: a section that holds
 * fields to fill is seen first and folded by the reader, never the other way round. Pass
 * `[open]="false"` only for a group a reader may usually skip.
 */
@Component({
  selector: 'app-form-section',
  standalone: true,
  imports: [FoldComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-fold [heading]="heading()" [open]="open()">
      @if (note()) {
        <p class="note" foldLead>{{ note() }}</p>
      }
      <div class="grid" [style.--cols]="columns()"><ng-content /></div>
    </app-fold>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .note {
        margin: 0;
        font-size: 13px;
        color: var(--muted);
      }
      .grid {
        display: grid;
        grid-template-columns: repeat(var(--cols, 2), minmax(0, 1fr));
        gap: var(--sp-3);
      }
      @media (max-width: 720px) {
        .grid {
          grid-template-columns: minmax(0, 1fr);
        }
      }
    `,
  ],
})
export class FormSectionComponent {
  readonly heading = input.required<string>();
  readonly note = input<string | null>(null);
  readonly columns = input<1 | 2 | 3>(2);
  /** Whether the section starts open. Open unless the group is one a reader may usually skip. */
  readonly open = input(true);
}
