/**
 * When a required field is allowed to complain.
 *
 * <p>The back-office forms all worked the same way before this: the save button went grey and
 * nothing said which of a dozen fields was the reason. On the customer form, where which fields
 * are mandatory is a company setting, that could be any of six. The refusal now sits under the
 * field that has to change.</p>
 *
 * <p>The timing is the whole point. A form that turns red the moment it opens is nagging at
 * someone who has not typed anything yet; one that stays silent after a refused save is hiding.
 * So a field complains once it has been left, and every field complains once a save has been
 * attempted.</p>
 */
export class TouchedFields {
  private readonly seen = new Set<string>();

  /** The field has been visited and left. */
  touch(name: string): void {
    this.seen.add(name);
  }

  /** A save was attempted: from here every named field may speak. */
  touchAll(...names: string[]): void {
    for (const name of names) this.seen.add(name);
  }

  /** Back to silence — a new record, or one just saved. */
  reset(): void {
    this.seen.clear();
  }

  /** One field back to silence — another choice took it off the form, the reader did not leave it empty. */
  forget(name: string): void {
    this.seen.delete(name);
  }

  /** Whether this field may speak at all — for a rule other than "still empty", such as a malformed value. */
  visited(name: string): boolean {
    return this.seen.has(name);
  }

  /** Whether this field is allowed to say, right now, that it is still empty. */
  missing(name: string, value: unknown): boolean {
    return this.seen.has(name) && !String(value ?? '').trim();
  }
}
