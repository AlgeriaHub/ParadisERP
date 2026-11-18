import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  DestroyRef,
  ElementRef,
  afterEveryRender,
  inject,
  input,
  model,
} from '@angular/core';

let nextId = 0;

/**
 * One section of an accordion form (owner, PinSay #303, 2026-10-05: "use accordion for form"; and
 * 2026-10-07/08: "pages that contain long inputs and groups"): a heading that opens and closes the
 * fields beneath it. Several may be open at once — a section is folded away to shorten the page,
 * never because another one opened.
 *
 * - **The sidebar's disclosure, not a new one.** A real button inside the heading, `aria-expanded`
 *   and `aria-controls`, and the `+`/`−` text glyph the sidebar already draws: no icon, nothing to
 *   mirror by hand.
 * - **Open where the work starts.** A form opens its first section and every section holding a
 *   required field (`[open]="true"`); only the ones a reader may skip start folded. A form that
 *   opened as a stack of closed headings hid the very fields it was waiting for (2026-10-08).
 * - **A state stays in view when folded.** Content marked `foldState` is projected beside the
 *   heading, and `foldLead` (a setting row's explanation) beneath it, both outside the folding
 *   part — so what a section is and how it stands are said whether it is open or not.
 * - **A setting row folds as a row.** On the host, `class="bo-group bo-group--row"` lays the head
 *   out as the row's lead and the body as its controls; folded, the row keeps its title and note.
 * - **Folded is hidden, not removed.** The fields stay in the page with their values, so a folded
 *   section loses nothing and its Save is still the panel's.
 * - **A problem opens its section.** A field marked invalid after Save, a native `required` check,
 *   or an error summary's link (`revealField`) opens the section holding it; the reader can fold it
 *   again once they have seen it.
 */
@Component({
  selector: 'app-fold',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(revealField)': 'reveal()' },
  template: `
    <div class="fold__head">
      <h3 class="fold__title">
        <button type="button" [attr.aria-expanded]="open()" [attr.aria-controls]="bodyId" (click)="open.set(!open())">
          <span class="fold__mark" aria-hidden="true">{{ open() ? '−' : '+' }}</span>
          <span>{{ heading() }}</span>
        </button>
      </h3>
      <ng-content select="[foldState]" />
      <div class="fold__lead"><ng-content select="[foldLead]" /></div>
    </div>
    <div class="fold__body" [id]="bodyId" [hidden]="!open()">
      <ng-content />
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
        background: var(--surface);
        border: var(--line) solid var(--border);
        border-radius: var(--radius);
        box-shadow: var(--shadow-sm);
        overflow: hidden;
      }
      .fold__head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        align-content: start;
        gap: var(--sp-2) var(--sp-3);
        padding: 12px 18px;
        min-inline-size: 0;
        background: var(--surface);
      }
      :host:has(.fold__body:not([hidden])) .fold__head,
      .fold__head:has(button[aria-expanded='true']) {
        border-block-end: var(--line) solid var(--border);
      }
      .fold__title {
        margin: 0;
        flex: 1 1 14rem;
        min-inline-size: 0;
        font-family: var(--font-display);
        font-size: 15px;
        font-weight: 600;
      }
      .fold__title button {
        display: flex;
        align-items: center;
        gap: var(--sp-2-5, 10px);
        inline-size: 100%;
        min-block-size: var(--control-h-sm);
        padding: 0;
        border: 0;
        background: transparent;
        color: var(--text);
        font: inherit;
        text-align: start;
        cursor: pointer;
        transform: none;
        box-shadow: none;
      }
      .fold__title button:hover {
        background: transparent;
        transform: none;
        box-shadow: none;
      }
      .fold__mark {
        display: inline-grid;
        place-items: center;
        inline-size: 24px;
        block-size: 24px;
        border: var(--line) solid var(--border);
        border-radius: var(--radius-sm);
        background: var(--surface-2);
        color: var(--text-2);
        font-size: 13px;
        font-weight: 600;
        line-height: 1;
        flex: none;
      }
      .fold__title button:hover .fold__mark {
        background: var(--surface-hover);
        color: var(--text);
      }
      .fold__lead {
        flex: 1 0 100%;
        display: grid;
        gap: var(--sp-2);
        min-inline-size: 0;
      }
      .fold__lead:empty {
        display: none;
      }
      .fold__body {
        padding: 14px 18px;
        display: grid;
        gap: var(--sp-3);
        align-content: start;
        min-inline-size: 0;
      }
      .fold__body[hidden] {
        display: none;
      }
    `,
  ],
})
export class FoldComponent {
  readonly heading = input.required<string>();
  /**
   * Two-way, and the initial state: `[open]="true"` for a section a reader must see first; a page
   * that must open a section itself (a refused save naming it) binds `[(open)]`.
   */
  readonly open = model(false);
  readonly bodyId = `fold-${++nextId}`;

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly changeDetector = inject(ChangeDetectorRef);

  constructor() {
    let previousError: Element | null = null;
    afterEveryRender(() => {
      // Custom form validation marks fields after Save. Each newly invalid field opens its section
      // once, so the reader can still fold a section they have already looked at.
      const error = this.host.nativeElement.querySelector('.is-invalid, [aria-invalid="true"], .ng-invalid.ng-touched');
      if (error && error !== previousError) this.open.set(true);
      previousError = error;
    });
    // The browser's own `required` check must be able to focus a control in a folded section.
    const reveal = () => this.reveal();
    this.host.nativeElement.addEventListener('invalid', reveal, true);
    inject(DestroyRef).onDestroy(() => this.host.nativeElement.removeEventListener('invalid', reveal, true));
  }

  reveal(): void {
    this.open.set(true);
    this.changeDetector.detectChanges();
  }
}
