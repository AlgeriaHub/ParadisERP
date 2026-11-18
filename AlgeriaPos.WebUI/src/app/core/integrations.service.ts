import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from './api.service';
import { normalizeCardCode } from './loyalty';
import { IntegrationsPublic, PaymentMethod } from './models';

export type ReceiptChannel = 'sms' | 'email';

export const NO_INTEGRATIONS: IntegrationsPublic = {
  cardMode: 'None',
  requireCardReference: false,
  smsReceipts: false,
  emailReceipts: false,
};

export const MAX_CARD_REFERENCE_LENGTH = 100;

/** True when this payment line still needs the bank terminal's approval number. */
export function missingCardReference(
  settings: IntegrationsPublic,
  method: PaymentMethod,
  amount: number,
  reference: string | null | undefined,
): boolean {
  return settings.requireCardReference && method === 'Card' && amount > 0 && !(reference ?? '').trim();
}

/**
 * What a payment part keeps as its reference: a gift card keeps its normalized code, a card
 * payment keeps the terminal's approval number, and every other method keeps none. The card
 * number is trimmed and capped to the column's width so a long paste cannot be silently cut
 * to something different by the server.
 */
export function paymentReference(method: PaymentMethod, reference: string | null | undefined): string | null {
  if (method === 'GiftCard') return normalizeCardCode(reference);
  if (method === 'Card') return (reference ?? '').trim().slice(0, MAX_CARD_REFERENCE_LENGTH) || null;
  return null;
}

/** Mirrors the server's recipient rules; returns the normalized value or null. */
export function normalizeRecipient(channel: ReceiptChannel, to: string): string | null {
  const value = to.trim();
  if (channel === 'sms') {
    const phone = value.replace(/[\s.-]/g, '');
    return /^\+?[0-9]{6,15}$/.test(phone) ? phone : null;
  }
  return value.length <= 254 && /^[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+$/.test(value) ? value : null;
}

/** Operator-configured integrations as the till may see them (no URLs, no secrets). */
@Injectable({ providedIn: 'root' })
export class IntegrationsService {
  private readonly api = inject(ApiService);

  readonly settings = signal<IntegrationsPublic>(NO_INTEGRATIONS);
  readonly channels = computed<ReceiptChannel[]>(() => {
    const s = this.settings();
    return [...(s.smsReceipts ? (['sms'] as const) : []), ...(s.emailReceipts ? (['email'] as const) : [])];
  });

  set(settings: IntegrationsPublic | null | undefined): void {
    if (settings) this.settings.set(settings);
  }

  sendReceipt(saleId: string, channel: ReceiptChannel, to: string): Promise<void> {
    return this.api.postJson(`/api/sales/${encodeURIComponent(saleId)}/send-receipt`, { channel, to });
  }
}
