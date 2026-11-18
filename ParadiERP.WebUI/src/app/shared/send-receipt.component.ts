import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError } from '../core/api.service';
import { ConnectivityService } from '../core/connectivity.service';
import { I18nService } from '../core/i18n.service';
import { IntegrationsService, ReceiptChannel, normalizeRecipient } from '../core/integrations.service';
import { Sale } from '../core/models';
import { ConfirmService } from '../ui/confirm.service';
import { actionRequest } from '../ui/confirm-requests';

/** "Send receipt" by SMS or e-mail. Shown only when the operator enabled a channel; needs a connection. */
@Component({
  selector: 'app-send-receipt',
  standalone: true,
  imports: [FormsModule],
  template: `
    @if (channels().length) {
      <form class="send" (ngSubmit)="send()">
        <strong class="send-title">{{ i18n.t('receiptSend.title') }}</strong>
        @if (!connectivity.online()) {
          <!-- Sending needs a line; the sale itself never did. Saying which one is missing,
               in words, is the whole message here. -->
          <p class="state state-warn">{{ i18n.t('receiptSend.offline') }}</p>
        } @else if (sale().outboxState !== 'synced') {
          <p class="state state-warn">{{ i18n.t('receiptSend.notSynced') }}</p>
        } @else {
          <div class="row">
            @if (channels().length > 1) {
              <select name="channel" [attr.aria-label]="i18n.t('receiptSend.channel')" [(ngModel)]="channel">
                @for (c of channels(); track c) {
                  <option [value]="c">{{ i18n.t('receiptSend.' + c) }}</option>
                }
              </select>
            }
            <input
              name="to"
              [type]="channel === 'email' ? 'email' : 'tel'"
              maxlength="254"
              [attr.aria-label]="i18n.t(channel === 'email' ? 'receiptSend.email' : 'receiptSend.phone')"
              [placeholder]="i18n.t(channel === 'email' ? 'receiptSend.email' : 'receiptSend.phone')"
              [(ngModel)]="to"
            />
            <button type="submit" class="btn-ghost" [disabled]="busy() || !to.trim()">{{ i18n.t('receiptSend.send') }}</button>
          </div>
        }
        @if (message(); as m) {
          <p [class]="m.ok ? 'ok' : 'error'" role="status">{{ m.text }}</p>
        }
      </form>
    }
  `,
  styles: [
    `
      .send {
        display: grid;
        gap: var(--sp-3);
        justify-items: start;
        background: var(--surface-2);
        border: 1px solid var(--border);
        border-radius: var(--radius);
        padding: var(--sp-4);
      }
      .send-title {
        font-size: var(--fs-meta);
        font-weight: 700;
        letter-spacing: 0.05em;
        text-transform: uppercase;
        color: var(--muted);
      }
      .row { display: flex; gap: var(--sp-2); flex-wrap: wrap; width: 100%; }
      /* Sending a receipt is a one-handed action taken right after a sale, so the three
         controls take the same target height as everything else in the dialog. */
      .row select,
      .row input,
      .row button { min-height: var(--control-h-sm); }
      .row select { width: auto; flex: none; }
      .row input { flex: 1 1 10rem; min-width: 0; background: var(--surface); }
      .row button { flex: none; background: var(--surface); }
      p { margin: 0; font-size: var(--fs-sm); line-height: 1.45; }
      .ok { color: var(--ok); font-weight: 600; }
      .error { color: var(--danger); font-weight: 600; }
    `,
  ],
})
export class SendReceiptComponent {
  readonly i18n = inject(I18nService);
  readonly connectivity = inject(ConnectivityService);
  private readonly integrations = inject(IntegrationsService);
  private readonly confirm = inject(ConfirmService);

  readonly sale = input.required<Sale>();
  readonly channels = this.integrations.channels;
  readonly busy = signal(false);
  readonly message = signal<{ ok: boolean; text: string } | null>(null);
  readonly firstChannel = computed(() => this.channels()[0] ?? 'sms');

  channel: ReceiptChannel = 'sms';
  to = '';

  constructor() {
    effect(() => {
      this.sale();
      this.channel = this.firstChannel();
      this.to = '';
      this.message.set(null);
    });
  }

  async send(): Promise<void> {
    const to = normalizeRecipient(this.channel, this.to);
    if (!to) {
      this.message.set({ ok: false, text: this.i18n.t(this.channel === 'email' ? 'receiptSend.badEmail' : 'receiptSend.badPhone') });
      return;
    }
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('receiptSend.send'))))) return;
    this.busy.set(true);
    this.message.set(null);
    try {
      await this.integrations.sendReceipt(this.sale().id, this.channel, to);
      this.message.set({ ok: true, text: this.i18n.t('receiptSend.sent') });
    } catch (error) {
      const limited = error instanceof ApiError && error.status === 429;
      this.message.set({ ok: false, text: limited ? this.i18n.t('receiptSend.limit') : describe(error, this.i18n.t('receiptSend.failed')) });
    } finally {
      this.busy.set(false);
    }
  }
}

/** A 400 carries the server's reason (bad recipient, channel off); anything else gets the generic text. */
function describe(error: unknown, fallback: string): string {
  if (!(error instanceof ApiError) || error.status !== 400) return fallback;
  try {
    const body = JSON.parse(error.message);
    return typeof body === 'string' ? body : (body.detail ?? fallback);
  } catch {
    return fallback;
  }
}
