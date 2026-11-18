import { SubscriptionAccess } from './models';

/** A cached lease enables offline operation only through its absolute expiry. */
export function canOperate(subscription: SubscriptionAccess | undefined, now = Date.now()): boolean {
  // Compatibility with existing installations until their first upgraded API pull.
  if (!subscription) return true;
  if (!subscription.canOperate) return false;
  if (subscription.status === 'Unrestricted') return true;
  return !!subscription.accessUntilUtc && now < Date.parse(subscription.accessUntilUtc);
}
