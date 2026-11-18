import { BuiltinLang } from '../models';

/**
 * Filter words of the maintenance lists (owner, 2026-11-17: every list pages and filters). Shared words
 * (`filters.*`) live in `translations-list-filters.ts`; reuse them first.
 * Merged into `BACKOFFICE_TRANSLATIONS`.
 */
export const LIST_FILTERS_MAINTENANCE_TRANSLATIONS: Record<BuiltinLang, Record<string, string>> = {
  fr: {
    // Waiting for repair › In maintenance, by item: units still free to repair.
    'maint.filter.free': 'Unités libres',
    'maint.filter.someFree': 'Avec des unités libres',
    'maint.filter.allHeld': 'Toutes réservées par des travaux',
    // Workshop: a person who moved, without naming the branch (the row says it).
    'maint.filter.moved': 'Muté dans une autre succursale',
    // Practice ledger › Customer jobs' invoices.
    'maint.filter.matched': 'Rattachée à un travail',
  },
  ar: {
    'maint.filter.free': 'الوحدات الحرة',
    'maint.filter.someFree': 'بها وحدات حرة',
    'maint.filter.allHeld': 'كلها محجوزة لأعمال',
    'maint.filter.moved': 'نُقل إلى فرع آخر',
    'maint.filter.matched': 'مرتبطة بعمل',
  },
  en: {
    'maint.filter.free': 'Free units',
    'maint.filter.someFree': 'Some units free',
    'maint.filter.allHeld': 'All held by jobs',
    'maint.filter.moved': 'Moved to another branch',
    'maint.filter.matched': 'Names its job',
  },
};
