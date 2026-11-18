/**
 * Runs a DOM change inside a view transition, so the browser cross-fades the old frame into
 * the new one instead of snapping. Used for switching the interface language, where the text
 * and the whole left-to-right / right-to-left layout change at once.
 *
 * Keeping the feature test and the reduced-motion check here means callers just say "apply
 * this change", and a browser without the API — or a cashier who has asked for less motion —
 * simply gets today's instant swap.
 */
type WithViewTransition = Document & {
  startViewTransition?: (update: () => void | Promise<void>) => unknown;
};

export function prefersReducedMotion(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}

export function runViewTransition(update: () => void): void {
  const doc = document as WithViewTransition;
  if (typeof doc.startViewTransition !== 'function' || prefersReducedMotion()) {
    update();
    return;
  }
  doc.startViewTransition(update);
}
