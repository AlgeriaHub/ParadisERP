import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { I18nService } from '../core/i18n.service';

let nextId = 0;

/**
 * One dialog for the whole app (REDESIGN_PLAN.md §6.4), built on the native `<dialog>` with
 * `showModal()`. That gives what the three hand-made overlays lack: the page behind is inert, focus
 * moves in and stays in, and Escape is handled by the browser.
 *
 * The dialog never closes itself. Escape, a click on the backdrop or the × each emit `dismiss`,
 * and the host decides — so a form with unsaved input can ask before it is thrown away instead of
 * losing it, which is what the old modals did.
 *
 * Below 640px it is a bottom sheet, as the till's dialog already is.
 */
@Component({
  selector: 'app-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <dialog
      #dialog
      [class]="'size-' + size()"
      [attr.aria-labelledby]="titleId"
      (cancel)="onCancel($event)"
      (click)="onClick($event)"
    >
      <div class="frame">
        <header class="head">
          <h2 class="title" [id]="titleId">{{ heading() }}</h2>
          <button type="button" class="close" [attr.aria-label]="i18n.t('common.close')" (click)="dismiss.emit()">×</button>
        </header>
        <div class="body"><ng-content /></div>
        <footer class="foot"><ng-content select="[dialogFoot]" /></footer>
      </div>
    </dialog>
  `,
  styles: [
    `
      dialog {
        padding: 0;
        border: 1px solid var(--border);
        border-radius: var(--radius-lg);
        background: var(--surface);
        color: var(--text);
        box-shadow: var(--shadow-modal);
        inline-size: min(100% - 2rem, 32rem);
        max-block-size: calc(100dvh - 2rem);
      }
      dialog.size-md {
        inline-size: min(100% - 2rem, 44rem);
      }
      dialog.size-lg {
        inline-size: min(100% - 2rem, 60rem);
      }
      dialog::backdrop {
        background: color-mix(in srgb, var(--primary-950) 45%, transparent);
        backdrop-filter: blur(4px);
      }
      @keyframes dialog-pop {
        from {
          opacity: 0;
          translate: 0 14px;
        }
        to {
          opacity: 1;
          translate: none;
        }
      }
      dialog[open] {
        display: flex;
        flex-direction: column;
        overflow: hidden;
        animation: dialog-pop 220ms cubic-bezier(0.2, 0, 0, 1) both;
      }
      @media (prefers-reduced-motion: reduce) {
        dialog[open] {
          animation: none;
        }
      }
      .frame {
        flex: 1 1 auto;
        min-block-size: 0;
        display: grid;
        grid-template-rows: auto minmax(0, 1fr) auto;
      }
      .head {
        display: flex;
        align-items: center;
        gap: var(--sp-3);
        padding: 1rem 1.25rem;
        border-block-end: 1px solid var(--tone-100);
        background: linear-gradient(calc(90deg * var(--sx)), var(--tone-50), var(--surface));
      }
      .title {
        flex: 1;
        margin: 0;
        font-family: var(--font-display);
        font-size: 1rem;
        font-weight: 700;
        letter-spacing: 0;
        line-height: 1.2;
        color: var(--text);
      }
      .close {
        flex: none;
        inline-size: var(--control-h-sm);
        block-size: var(--control-h-sm);
        padding: 0;
        border: 0;
        border-radius: var(--radius);
        background: transparent;
        color: var(--muted);
        font-size: 1.25rem;
        line-height: 1;
        transition: background-color var(--t-fast) ease, color var(--t-fast) ease;
      }
      .close:hover {
        background: color-mix(in srgb, var(--text) 8%, transparent);
        color: var(--text);
      }
      /* The one internal scroller the layout allows (§7.2). */
      .body {
        overflow-y: auto;
        padding: 1.25rem 1.25rem 1.5rem;
      }
      .foot {
        display: flex;
        flex-wrap: wrap;
        justify-content: flex-end;
        gap: var(--sp-3);
        padding: 0.875rem 1.25rem;
        border-block-start: 1px solid var(--border);
      }
      .foot:empty {
        display: none;
      }
      @media (max-width: 640px) {
        dialog,
        dialog.size-md,
        dialog.size-lg {
          inline-size: 100%;
          max-inline-size: 100%;
          margin-block-end: 0;
          border-start-start-radius: var(--radius-lg);
          border-start-end-radius: var(--radius-lg);
          border-end-start-radius: 0;
          border-end-end-radius: 0;
        }
      }
    `,
  ],
})
export class DialogComponent {
  readonly i18n = inject(I18nService);

  readonly open = input(false);
  readonly heading = input.required<string>();
  readonly size = input<'sm' | 'md' | 'lg'>('sm');
  /** Escape, the backdrop or the ×. The host closes the dialog by setting `open` to false. */
  readonly dismiss = output<void>();

  readonly titleId = `app-dialog-${nextId++}`;
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  constructor() {
    effect(() => {
      const el = this.dialog().nativeElement;
      if (this.open() && !el.open) el.showModal();
      if (!this.open() && el.open) el.close();
    });
  }

  onCancel(event: Event): void {
    // The browser would close it on Escape by itself; the host decides instead.
    event.preventDefault();
    this.dismiss.emit();
  }

  onClick(event: MouseEvent): void {
    // A click on the dialog element itself, outside the frame, is a click on the backdrop.
    if (event.target === this.dialog().nativeElement) this.dismiss.emit();
  }
}
