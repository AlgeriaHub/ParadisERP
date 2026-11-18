import { BuiltinLang } from '../models';

/**
 * Filter words of the inventory lists (owner, 2026-11-17: every list pages and filters). Shared words
 * (`filters.*`) live in `translations-list-filters.ts`; reuse them first.
 * Merged into `BACKOFFICE_TRANSLATIONS`; the till's product list registers it itself.
 */
export const LIST_FILTERS_INVENTORY_TRANSLATIONS: Record<BuiltinLang, Record<string, string>> = {
  fr: {
    'products.filter.tracked': 'Suivi',
    'products.filter.untracked': 'Non suivi',
    'products.filter.stockOk': 'Stock suffisant',

    'transfer.filter.direction': 'Sens',
    'transfer.filter.outgoing': 'Sortants',
    'transfer.filter.incoming': 'Entrants',

    'rent.filter.stage': 'Avancement',
    'rent.filter.set': 'Défini',
    'rent.filter.notSet': 'Non défini',
  },
  ar: {
    'products.filter.tracked': 'متتبَّع',
    'products.filter.untracked': 'غير متتبَّع',
    'products.filter.stockOk': 'مخزون كافٍ',

    'transfer.filter.direction': 'الاتجاه',
    'transfer.filter.outgoing': 'الصادرة',
    'transfer.filter.incoming': 'الواردة',

    'rent.filter.stage': 'مرحلة العقد',
    'rent.filter.set': 'محدَّد',
    'rent.filter.notSet': 'غير محدَّد',
  },
  en: {
    'products.filter.tracked': 'Tracked',
    'products.filter.untracked': 'Not tracked',
    'products.filter.stockOk': 'Enough stock',

    'transfer.filter.direction': 'Direction',
    'transfer.filter.outgoing': 'Outgoing',
    'transfer.filter.incoming': 'Incoming',

    'rent.filter.stage': 'Progress',
    'rent.filter.set': 'Set',
    'rent.filter.notSet': 'Not set',
  },
};
