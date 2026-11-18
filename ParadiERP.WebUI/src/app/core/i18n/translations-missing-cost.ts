import { BuiltinLang } from '../models';

/**
 * Back-office vocabulary for Reports › Sales without a cost (SAL-02, reports/sold-without-cost).
 * Merged into `BACKOFFICE_TRANSLATIONS`.
 */
export const MISSING_COST_TRANSLATIONS: Record<BuiltinLang, Record<string, string>> = {
  fr: {
    'missingCost.navLabel': 'Ventes sans coût',
    'missingCost.lead':
      "Les articles vendus alors que leur coût était à 0. La vente n'est jamais bloquée, mais sa marge reste incomplète tant que le coût manque. Ouvrez l'article pour saisir son coût.",
    'missingCost.from': 'Du',
    'missingCost.to': 'Au',
    'missingCost.branch': 'Succursale',
    'missingCost.show': 'Afficher',
    'missingCost.uncostedHt': 'Ventes sans coût (HT)',
    'missingCost.share': 'Part des ventes HT',
    'missingCost.shareOf': 'sur {total}',
    'missingCost.sales': 'Ventes concernées',
    'missingCost.productsWithoutCost': 'Articles encore sans coût',
    'missingCost.item': 'Article',
    'missingCost.qty': 'Quantité vendue',
    'missingCost.revenueHt': 'Ventes HT',
    'missingCost.salesCount': 'Ventes',
    'missingCost.lastSold': 'Dernière vente',
    'missingCost.costNow': "Coût aujourd'hui",
    'missingCost.valueAtCostNow': 'Coût estimé',
    'missingCost.state.missing': 'Coût manquant',
    'missingCost.state.set': 'Coût saisi depuis',
    'missingCost.estimateNote':
      "Le coût estimé applique le coût d'aujourd'hui aux quantités vendues. Il n'est pas comptabilisé : ces ventes restent sans coût dans les comptes.",
    'missingCost.empty': 'Aucune vente sans coût sur cette période.',
  },
  ar: {
    'missingCost.navLabel': 'مبيعات بلا تكلفة',
    'missingCost.lead':
      'الأصناف التي بيعت وتكلفتها 0. لا يُمنع البيع أبداً، لكن هامشه يبقى ناقصاً ما دامت التكلفة غائبة. افتح الصنف لإدخال تكلفته.',
    'missingCost.from': 'من',
    'missingCost.to': 'إلى',
    'missingCost.branch': 'الفرع',
    'missingCost.show': 'عرض',
    'missingCost.uncostedHt': 'مبيعات بلا تكلفة (بدون رسم)',
    'missingCost.share': 'حصتها من المبيعات بدون رسم',
    'missingCost.shareOf': 'من {total}',
    'missingCost.sales': 'المبيعات المعنية',
    'missingCost.productsWithoutCost': 'أصناف ما زالت بلا تكلفة',
    'missingCost.item': 'الصنف',
    'missingCost.qty': 'الكمية المباعة',
    'missingCost.revenueHt': 'المبيعات بدون رسم',
    'missingCost.salesCount': 'عدد المبيعات',
    'missingCost.lastSold': 'آخر بيع',
    'missingCost.costNow': 'التكلفة اليوم',
    'missingCost.valueAtCostNow': 'التكلفة التقديرية',
    'missingCost.state.missing': 'التكلفة غائبة',
    'missingCost.state.set': 'أُدخلت التكلفة لاحقاً',
    'missingCost.estimateNote':
      'التكلفة التقديرية تطبّق تكلفة اليوم على الكميات المباعة. لا تُسجَّل في الحسابات: تبقى هذه المبيعات بلا تكلفة في الدفاتر.',
    'missingCost.empty': 'لا توجد مبيعات بلا تكلفة في هذه الفترة.',
  },
  en: {
    'missingCost.navLabel': 'Sales without a cost',
    'missingCost.lead':
      'Items sold while their cost was 0. The sale is never blocked, but its margin stays incomplete while the cost is missing. Open the item to enter its cost.',
    'missingCost.from': 'From',
    'missingCost.to': 'To',
    'missingCost.branch': 'Branch',
    'missingCost.show': 'Show',
    'missingCost.uncostedHt': 'Sales without a cost (before tax)',
    'missingCost.share': 'Share of sales before tax',
    'missingCost.shareOf': 'of {total}',
    'missingCost.sales': 'Sales affected',
    'missingCost.productsWithoutCost': 'Items still without a cost',
    'missingCost.item': 'Item',
    'missingCost.qty': 'Quantity sold',
    'missingCost.revenueHt': 'Sales before tax',
    'missingCost.salesCount': 'Sales',
    'missingCost.lastSold': 'Last sold',
    'missingCost.costNow': 'Cost today',
    'missingCost.valueAtCostNow': 'Estimated cost',
    'missingCost.state.missing': 'Cost missing',
    'missingCost.state.set': 'Cost set since',
    'missingCost.estimateNote':
      "The estimated cost applies today's cost to the quantities sold. It is not posted: these sales stay without a cost in the books.",
    'missingCost.empty': 'No sale without a cost in this period.',
  },
};
