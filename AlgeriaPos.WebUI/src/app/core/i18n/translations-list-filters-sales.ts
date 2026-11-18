import { BuiltinLang } from '../models';

/**
 * Filter words of the sales lists (owner, 2026-11-17: every list pages and filters). Shared words
 * (`filters.*`) live in `translations-list-filters.ts`; reuse them first.
 * Merged into `BACKOFFICE_TRANSLATIONS`; the till's Sales page (`features/sales`) also registers it
 * on its own, since a till may never open the back office.
 */
export const LIST_FILTERS_SALES_TRANSLATIONS: Record<BuiltinLang, Record<string, string>> = {
  fr: {
    'giftCards.filter.balanceLeft': 'Avec un solde',
    'giftCards.filter.spent': 'Épuisée (0)',
    'giftCards.filter.linked': 'Liée à un client',
    'giftCards.filter.anonymous': 'Sans client',

    'sup.filter.weOwe': 'Nous lui devons',
    'sup.filter.owesUs': 'Nous doit',
    'sup.filter.noFiscalIds': 'Sans identifiant fiscal',

    'sales.filter.sale': 'Ventes',
    'sales.filter.refund': 'Remboursements',
  },
  ar: {
    'giftCards.filter.balanceLeft': 'بها رصيد',
    'giftCards.filter.spent': 'مستنفدة (0)',
    'giftCards.filter.linked': 'مرتبطة بعميل',
    'giftCards.filter.anonymous': 'بلا عميل',

    'sup.filter.weOwe': 'ندين له',
    'sup.filter.owesUs': 'مدين لنا',
    'sup.filter.noFiscalIds': 'بلا معرّف جبائي',

    'sales.filter.sale': 'المبيعات',
    'sales.filter.refund': 'الاسترجاعات',
  },
  en: {
    'giftCards.filter.balanceLeft': 'Balance left',
    'giftCards.filter.spent': 'Used up (0)',
    'giftCards.filter.linked': 'Linked to a customer',
    'giftCards.filter.anonymous': 'No customer',

    'sup.filter.weOwe': 'We owe them',
    'sup.filter.owesUs': 'Owes us',
    'sup.filter.noFiscalIds': 'No fiscal identifier',

    'sales.filter.sale': 'Sales',
    'sales.filter.refund': 'Refunds',
  },
};
