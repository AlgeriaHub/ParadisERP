import { BuiltinLang } from '../models';

/**
 * Back-office vocabulary for a rental contract's tax choice (owner, 2026-11-02: VAT is optional when the bill is
 * made, rental bills included). Merged into `BACKOFFICE_TRANSLATIONS`. The two choices reuse the invoice form's
 * `inv.taxMode.Taxed` / `inv.taxMode.Untaxed`; `{tax}` is the country profile's name for the tax and `{rate}` the
 * company's hire rate as the profile writes it.
 */
export const RENTAL_TAX_TRANSLATIONS: Record<BuiltinLang, Record<string, string>> = {
  fr: {
    'rent.form.taxMode': '{tax} du contrat',
    'rent.form.taxHint.rate':
      'Les factures de ce contrat portent la {tax} au taux de location des réglages : {rate}. Le choix est fixé au premier bon de sortie.',
    'rent.form.taxHint.noRate':
      "Aucun taux de {tax} n'est fixé pour la location dans les réglages : enregistré maintenant, ce contrat n'en porte pas, même avec {tax}.",
    'rent.form.taxHint.untaxed':
      'Les factures de ce contrat ne portent pas de {tax}. Le choix est fixé au premier bon de sortie.',
  },
  ar: {
    'rent.form.taxMode': '{tax} في العقد',
    'rent.form.taxHint.rate':
      'تحمل فواتير هذا العقد {tax} بنسبة الإيجار المحددة في الإعدادات: {rate}. يُثبَّت الاختيار عند أول إذن إخراج.',
    'rent.form.taxHint.noRate':
      'لم تُحدَّد نسبة {tax} للإيجار في الإعدادات: إذا حُفظ هذا العقد الآن فلن يحمل {tax} حتى لو اخترت «مع {tax}».',
    'rent.form.taxHint.untaxed': 'لا تحمل فواتير هذا العقد {tax}. يُثبَّت الاختيار عند أول إذن إخراج.',
  },
  en: {
    'rent.form.taxMode': 'Contract {tax}',
    'rent.form.taxHint.rate':
      "This contract's bills carry {tax} at the hire rate in Rentals settings: {rate}. The choice is fixed at the first issue note.",
    'rent.form.taxHint.noRate':
      'No {tax} rate is set for hire in Rentals settings: saved now, this contract carries none, even with {tax}.',
    'rent.form.taxHint.untaxed': "This contract's bills carry no {tax}. The choice is fixed at the first issue note.",
  },
};
