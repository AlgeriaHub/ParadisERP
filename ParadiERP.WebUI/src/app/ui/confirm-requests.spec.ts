import { TestBed } from '@angular/core/testing';
import { I18nService } from '../core/i18n.service';
import { TRANSLATIONS } from '../core/i18n/translations';
import { actionRequest, saveRequest } from './confirm-requests';

/** The save and action questions (the owner, 2026-10-10): what will happen, in words. */
describe('confirm requests — save and action', () => {
  let i18n: I18nService;
  beforeEach(() => (i18n = TestBed.inject(I18nService)));

  it('names the record being saved when there is one, and never draws a save as dangerous', () => {
    const named = saveRequest(i18n, '  Client Benali ');
    expect(named.title).toContain('Client Benali');
    expect(named.confirmLabel).toBe(i18n.t('confirm.save.ok'));
    expect(named.danger).toBeFalsy();
    expect(saveRequest(i18n).title).toBe(i18n.t('confirm.save.title'));
    expect(saveRequest(i18n, '   ').title).toBe(i18n.t('confirm.save.title'));
  });

  it('asks an action back with its own verb on the confirming button', () => {
    const request = actionRequest(i18n, 'Issue the invoice');
    expect(request.title).toContain('Issue the invoice');
    expect(request.confirmLabel).toBe('Issue the invoice');
    expect(request.message).toBe(i18n.t('confirm.action.message'));
    expect(actionRequest(i18n, 'Reverse', 'It cannot be undone.', true)).toEqual(
      jasmine.objectContaining({ danger: true, message: 'It cannot be undone.' }),
    );
  });

  it('has the words in all three languages', () => {
    for (const lang of ['fr', 'ar', 'en'] as const) {
      for (const key of ['confirm.save.title', 'confirm.save.named', 'confirm.save.message', 'confirm.save.ok',
        'confirm.save.back', 'confirm.action.title', 'confirm.action.message']) {
        expect(TRANSLATIONS[lang][key]).withContext(`${lang} ${key}`).toBeTruthy();
      }
    }
  });
});
