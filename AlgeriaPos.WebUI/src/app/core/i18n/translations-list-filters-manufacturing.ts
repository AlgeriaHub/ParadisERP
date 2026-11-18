import { BuiltinLang } from '../models';

/**
 * Filter words of the manufacturing lists (owner, 2026-11-17: every list pages and filters). Shared words
 * (`filters.*`) live in `translations-list-filters.ts`; reuse them first.
 * Merged into `BACKOFFICE_TRANSLATIONS`.
 */
export const LIST_FILTERS_MANUFACTURING_TRANSLATIONS: Record<BuiltinLang, Record<string, string>> = {
  fr: {
    'mfg.filter.scope': 'Portée',
    'mfg.filter.lineOwn': 'Propre à une ligne',
    'mfg.filter.late': 'En retard',
    'mfg.filter.hasManager': 'Avec un responsable',
    'mfg.filter.hasActiveBom': 'Avec une nomenclature active',
    'mfg.filter.oneProduct': 'Un produit précis',
    'mfg.filter.baseUnitSet': 'Définie',
    'mfg.filter.baseUnitMissing': 'Pas encore définie',
    'mfg.filter.provisional': 'Chiffres provisoires',
    'mfg.filter.ended': 'Affectation terminée',
    'mfg.filter.history.event': 'Événements',
    'mfg.filter.history.movement': 'Mouvements de matière',
    'mfg.filter.history.consumption': 'Consommations',
    'mfg.filter.history.release': 'Mises en stock',
  },
  ar: {
    'mfg.filter.scope': 'النطاق',
    'mfg.filter.lineOwn': 'خاصة بخط واحد',
    'mfg.filter.late': 'متأخر',
    'mfg.filter.hasManager': 'له مسؤول',
    'mfg.filter.hasActiveBom': 'له وصفة معتمدة',
    'mfg.filter.oneProduct': 'منتج محدد',
    'mfg.filter.baseUnitSet': 'محددة',
    'mfg.filter.baseUnitMissing': 'لم تُحدَّد بعد',
    'mfg.filter.provisional': 'أرقام مؤقتة',
    'mfg.filter.ended': 'انتهى التعيين',
    'mfg.filter.history.event': 'الأحداث',
    'mfg.filter.history.movement': 'حركات المواد',
    'mfg.filter.history.consumption': 'الاستهلاك',
    'mfg.filter.history.release': 'الإدخال إلى المخزون',
  },
  en: {
    'mfg.filter.scope': 'Scope',
    'mfg.filter.lineOwn': "One line's own",
    'mfg.filter.late': 'Late',
    'mfg.filter.hasManager': 'Has a manager',
    'mfg.filter.hasActiveBom': 'Has an active bill of materials',
    'mfg.filter.oneProduct': 'A specific product',
    'mfg.filter.baseUnitSet': 'Set',
    'mfg.filter.baseUnitMissing': 'Not set yet',
    'mfg.filter.provisional': 'Provisional figures',
    'mfg.filter.ended': 'Assignment ended',
    'mfg.filter.history.event': 'Events',
    'mfg.filter.history.movement': 'Material movements',
    'mfg.filter.history.consumption': 'Consumption',
    'mfg.filter.history.release': 'Releases',
  },
};
