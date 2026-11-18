import { I18nService } from '../core/i18n.service';
import { ConfirmRequest } from './confirm.service';

/**
 * The questions every destructive action asks before it acts (the owner, 2026-10-05: "every
 * delete in the program must show a confirmation first"). One shape so that a delete reads the
 * same on every screen: the title names the thing, the message says what is lost, the button
 * repeats the verb, and the danger style marks what cannot be taken back.
 */

/** Deleting a record. The message defaults to the generic consequence of a delete. */
export function deleteRequest(i18n: I18nService, title: string, message?: string): ConfirmRequest {
  return {
    title,
    message: message ?? i18n.t('confirm.delete.message'),
    confirmLabel: i18n.t('common.delete'),
    cancelLabel: i18n.t('confirm.keep'),
    danger: true,
  };
}

/** Taking a line out of a document or a settings table that is still being edited. */
export function removeLineRequest(i18n: I18nService): ConfirmRequest {
  return {
    title: i18n.t('confirm.line.title'),
    message: i18n.t('confirm.line.message'),
    confirmLabel: i18n.t('confirm.line.ok'),
    cancelLabel: i18n.t('confirm.keep'),
    danger: true,
  };
}

/**
 * Marking a cheque or a transfer collected. Not a delete, but there is no un-settle, so it asks
 * the same way; the message is the screen's own sentence saying what it settles.
 */
export function collectRequest(i18n: I18nService, reference: string, message: string): ConfirmRequest {
  return {
    title: i18n.t('confirm.collect.title', { n: reference }),
    message,
    confirmLabel: i18n.t('treasury.markCollected'),
  };
}

/**
 * Saving a record — a new one or changes to one (the owner, 2026-10-10: "every button that saves,
 * deletes or edits shows a confirmation first"). Asked after the form's own checks pass and before
 * anything is sent, so a "no" leaves the form exactly as it was, still open, still filled in. Name
 * the record when there is one: "Save “Client Benali”?".
 */
export function saveRequest(i18n: I18nService, name?: string | null): ConfirmRequest {
  const named = name?.trim();
  return {
    title: named ? i18n.t('confirm.save.named', { name: named }) : i18n.t('confirm.save.title'),
    message: i18n.t('confirm.save.message'),
    confirmLabel: i18n.t('confirm.save.ok'),
    cancelLabel: i18n.t('confirm.save.back'),
  };
}

/**
 * Any other action that writes — issuing a document, approving a run, recording a movement,
 * applying a preset. The button's own label is the verb: the title asks it back ("Confirm: Issue
 * the invoice?") and the confirming button repeats it, so the reader sees in words what will happen.
 * Pass `danger` for what cannot be undone.
 */
export function actionRequest(
  i18n: I18nService,
  action: string,
  message?: string | null,
  danger = false,
): ConfirmRequest {
  return {
    title: i18n.t('confirm.action.title', { action }),
    message: message ?? i18n.t('confirm.action.message'),
    confirmLabel: action,
    cancelLabel: i18n.t('confirm.save.back'),
    danger,
  };
}

/** Throwing away edits that have not been saved — the same words as leaving a form. */
export function discardRequest(i18n: I18nService): ConfirmRequest {
  return {
    title: i18n.t('ui.discard.title'),
    message: i18n.t('ui.discard.message'),
    confirmLabel: i18n.t('ui.discard.ok'),
    cancelLabel: i18n.t('ui.discard.keep'),
    danger: true,
  };
}

/**
 * Whether a line holds anything somebody typed or picked, judged against the line the form
 * starts a new one from. An untouched new line goes without a question: asking before removing
 * nothing teaches people to click through the dialog. The fields named in `ignore` (a line's
 * own key, a position) are not input.
 */
export function differsFromBlank<T extends object>(line: T, blank: T, ignore: readonly (keyof T)[] = []): boolean {
  const strip = (value: T) => {
    const copy: Partial<T> = { ...value };
    for (const key of ignore) delete copy[key];
    return JSON.stringify(copy);
  };
  return strip(line) !== strip(blank);
}
