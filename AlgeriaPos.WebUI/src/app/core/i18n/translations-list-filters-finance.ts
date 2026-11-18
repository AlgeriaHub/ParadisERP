import { BuiltinLang } from '../models';

/**
 * Filter words of the finance lists (owner, 2026-11-17: every list pages and filters). Shared words
 * (`filters.*`) live in `translations-list-filters.ts`; reuse them first.
 * Merged into `BACKOFFICE_TRANSLATIONS`.
 */
export const LIST_FILTERS_FINANCE_TRANSLATIONS: Record<BuiltinLang, Record<string, string>> = {
  fr: {
    'reports.filter.withTax': 'Avec {tax}',
    'reports.filter.withoutTax': 'Sans {tax}',
    // A cheque's or transfer's due day (Accounting › Vouchers, Finance › Awaiting collection).
    'treasury.filter.dueFrom': 'Échéance du',
    'treasury.filter.dueTo': 'Échéance au',

    'fa.filter.depreciated': 'Entièrement amorti',

    'valuation.filter.inStock': 'En stock',
    'valuation.filter.zero': 'Stock nul',
    'valuation.filter.negative': 'Stock négatif',
    'valuation.filter.above': 'Coût moyen au-dessus du prix de revient',
    'valuation.filter.below': 'Coût moyen en dessous du prix de revient',
    'valuation.filter.none': 'Sans écart',
    'valuation.filter.withNotes': 'Avec une remarque',
    'valuation.filter.noNotes': 'Sans remarque',

    'lowStock.filter.out': 'En rupture (0 ou moins)',
    'lowStock.filter.low': 'Bas, encore en stock',
  },
  ar: {
    'reports.filter.withTax': 'مع {tax}',
    'reports.filter.withoutTax': 'بدون {tax}',
    'treasury.filter.dueFrom': 'الاستحقاق من',
    'treasury.filter.dueTo': 'الاستحقاق إلى',

    'fa.filter.depreciated': 'مهتلك بالكامل',

    'valuation.filter.inStock': 'في المخزون',
    'valuation.filter.zero': 'مخزون صفر',
    'valuation.filter.negative': 'مخزون سالب',
    'valuation.filter.above': 'التكلفة الوسطية أعلى من سعر التكلفة',
    'valuation.filter.below': 'التكلفة الوسطية أدنى من سعر التكلفة',
    'valuation.filter.none': 'بلا فرق',
    'valuation.filter.withNotes': 'مع ملاحظة',
    'valuation.filter.noNotes': 'بلا ملاحظة',

    'lowStock.filter.out': 'نفد (0 أو أقل)',
    'lowStock.filter.low': 'منخفض، لا يزال متوفراً',
  },
  en: {
    'reports.filter.withTax': 'With {tax}',
    'reports.filter.withoutTax': 'Without {tax}',
    'treasury.filter.dueFrom': 'Due from',
    'treasury.filter.dueTo': 'Due to',

    'fa.filter.depreciated': 'Fully depreciated',

    'valuation.filter.inStock': 'In stock',
    'valuation.filter.zero': 'Zero stock',
    'valuation.filter.negative': 'Negative stock',
    'valuation.filter.above': 'Average cost above cost price',
    'valuation.filter.below': 'Average cost below cost price',
    'valuation.filter.none': 'No difference',
    'valuation.filter.withNotes': 'With a note',
    'valuation.filter.noNotes': 'Without a note',

    'lowStock.filter.out': 'Out of stock (0 or less)',
    'lowStock.filter.low': 'Low, still in stock',
  },
};
