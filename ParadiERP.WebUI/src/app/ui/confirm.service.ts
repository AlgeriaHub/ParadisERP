import {
  ApplicationRef,
  ChangeDetectionStrategy,
  Component,
  EnvironmentInjector,
  Injectable,
  createComponent,
  inject,
  input,
  output,
} from '@angular/core';
import { I18nService } from '../core/i18n.service';
import { DialogComponent } from './dialog.component';

export interface ConfirmRequest {
  /** The question, as a short heading: "Delete this product?" */
  title: string;
  /** What happens if the user goes ahead — the consequence, not a restatement of the title. */
  message?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Destructive: the confirming button is drawn in the danger style. */
  danger?: boolean;
  /** Focus starts on the confirming button, so Enter confirms. Never with `danger`: Enter must not destroy anything. */
  focusConfirm?: boolean;
}

@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [DialogComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-dialog [open]="true" [heading]="request().title" (dismiss)="answer.emit(false)">
      @if (request().message) {
        <p class="message">{{ request().message }}</p>
      }
      <div dialogFoot class="actions">
        <button type="button" class="btn-ghost" (click)="answer.emit(false)">
          {{ request().cancelLabel || i18n.t('ui.confirm.cancel') }}
        </button>
        <button
          type="button"
          [class]="request().danger ? 'btn-danger-solid' : 'btn-primary'"
          [attr.autofocus]="request().focusConfirm && !request().danger ? '' : null"
          (click)="answer.emit(true)"
        >
          {{ request().confirmLabel || i18n.t('ui.confirm.ok') }}
        </button>
      </div>
    </app-dialog>
  `,
  styles: [
    `
      .message {
        margin: 0;
        font-size: 15px;
        line-height: 1.5;
        color: var(--text-2);
      }
      .actions {
        display: contents;
      }
    `,
  ],
})
export class ConfirmDialogComponent {
  readonly i18n = inject(I18nService);
  readonly request = input.required<ConfirmRequest>();
  readonly answer = output<boolean>();
}

/**
 * The replacement for `window.confirm()` (16 call sites, migrated in Phase 4): the same question,
 * the same yes/no answer, in the app's own dialog — with the consequence written out and the
 * danger style kept for what cannot be undone.
 *
 * ```ts
 * if (!(await this.confirm.ask({ title: …, message: …, danger: true }))) return;
 * ```
 */
@Injectable({ providedIn: 'root' })
export class ConfirmService {
  private readonly appRef = inject(ApplicationRef);
  private readonly injector = inject(EnvironmentInjector);

  ask(request: ConfirmRequest): Promise<boolean> {
    // Focus goes back where it was when the dialog closes, as the native confirm does.
    const returnTo = document.activeElement as HTMLElement | null;
    const ref = createComponent(ConfirmDialogComponent, { environmentInjector: this.injector });
    ref.setInput('request', request);
    this.appRef.attachView(ref.hostView);
    document.body.appendChild(ref.location.nativeElement);
    return new Promise<boolean>((resolve) => {
      const sub = ref.instance.answer.subscribe((yes) => {
        sub.unsubscribe();
        this.appRef.detachView(ref.hostView);
        ref.destroy();
        returnTo?.focus?.();
        resolve(yes);
      });
    });
  }
}
