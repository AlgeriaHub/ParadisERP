import { EffectRef, Signal, WritableSignal, effect } from '@angular/core';
import { Sale } from './models';

/**
 * Keeps a sale held in memory in step with its sync. Checkout hands the till its sale pending, and the
 * push that follows marks it synced in IndexedDB only (`SyncService.pushed` says when). Until the held
 * sale is synced, each push — and each newly held sale, in case its push was quicker — reads it again.
 * It is replaced only when its state moved, and only while that same sale object is still held, so a
 * slow read never puts back an older state or the previous customer's sale. Nothing is written.
 *
 * Without it the till's finished-sale screen said "saved offline" for good, and the send-receipt form
 * (shown once a sale is synced) never appeared (found 2026-09-17).
 */
export function followSaleSync(
  held: WritableSignal<Sale | null>,
  read: (id: string) => Promise<Sale | undefined>,
  pushed: Signal<number>,
): EffectRef {
  const refresh = async (shown: Sale): Promise<void> => {
    const stored = await read(shown.id);
    if (!stored || stored.outboxState === shown.outboxState) return;
    if (held() === shown) held.set(stored);
  };
  return effect(() => {
    pushed();
    const shown = held();
    if (shown && shown.outboxState !== 'synced') void refresh(shown);
  });
}
