/** Reveal containing disclosures before moving keyboard focus to a validation error. */
export function revealField(element: HTMLElement | null): void {
  if (!element) return;
  for (let parent = element.parentElement; parent; parent = parent.parentElement) {
    if (parent instanceof HTMLDetailsElement) parent.open = true;
  }
  element.dispatchEvent(new Event('revealField', { bubbles: true }));
  element.scrollIntoView({ block: 'center' });
  element.focus();
}
