import { BuiltinLang } from '../models';

/**
 * Back-office vocabulary for the money side of gift cards (SAL-17): a card sold for cash at a branch, a paid
 * top-up, a cash refund of the balance, a correction. Merged into `BACKOFFICE_TRANSLATIONS`.
 */
export const GIFT_CARD_CASH_TRANSLATIONS: Record<BuiltinLang, Record<string, string>> = {
  fr: {
    'giftCards.cash.branch': 'Succursale qui encaisse',
    'giftCards.cash.issueNote':
      "La carte est payée en espèces : le montant entre dans la caisse de cette succursale et reste dû au client jusqu'à ce que la carte soit utilisée.",
    'giftCards.cash.ownBranch': 'Encaissé dans la caisse de votre succursale : {branch}.',
    'giftCards.kind': "Type d'opération",
    'giftCards.kind.choose': '— Choisir —',
    'giftCards.kind.topUp': 'Recharge payée',
    'giftCards.kind.cashOut': 'Remboursement en espèces',
    'giftCards.kind.correctionAdd': 'Correction : ajouter',
    'giftCards.kind.correctionRemove': 'Correction : retirer',
    'giftCards.kindHint.topUp': "Espèces reçues : elles entrent dans la caisse de la succursale.",
    'giftCards.kindHint.cashOut': 'Espèces rendues : elles sortent de la caisse de la succursale.',
    'giftCards.kindHint.correction': "Aucun argent : rien n'est passé en comptabilité.",
    'giftCards.adjustAmount': 'Montant',
    'giftCards.reason.topup': 'Recharge payée',
    'giftCards.reason.cashout': 'Remboursement en espèces',
    'acc.src.gift-card-sale': 'Vente de carte cadeau',
    'acc.src.gift-card-cash-out': 'Remboursement de carte cadeau',
  },
  ar: {
    'giftCards.cash.branch': 'الفرع الذي يقبض',
    'giftCards.cash.issueNote':
      'تُدفع البطاقة نقداً: يدخل المبلغ صندوق هذا الفرع ويبقى ديناً للزبون إلى أن تُستعمل البطاقة.',
    'giftCards.cash.ownBranch': 'يُقبض في صندوق فرعك: {branch}.',
    'giftCards.kind': 'نوع العملية',
    'giftCards.kind.choose': '— اختر —',
    'giftCards.kind.topUp': 'شحن مدفوع',
    'giftCards.kind.cashOut': 'استرجاع نقدي',
    'giftCards.kind.correctionAdd': 'تصحيح: إضافة',
    'giftCards.kind.correctionRemove': 'تصحيح: خصم',
    'giftCards.kindHint.topUp': 'نقد مقبوض: يدخل صندوق الفرع.',
    'giftCards.kindHint.cashOut': 'نقد مُعاد: يخرج من صندوق الفرع.',
    'giftCards.kindHint.correction': 'بلا نقد: لا يُسجَّل شيء في الحسابات.',
    'giftCards.adjustAmount': 'المبلغ',
    'giftCards.reason.topup': 'شحن مدفوع',
    'giftCards.reason.cashout': 'استرجاع نقدي',
    'acc.src.gift-card-sale': 'بيع بطاقة هدية',
    'acc.src.gift-card-cash-out': 'استرجاع من بطاقة هدية',
  },
  en: {
    'giftCards.cash.branch': 'Branch taking the cash',
    'giftCards.cash.issueNote':
      "The card is paid in cash: the amount goes into this branch's cash box and is owed to the customer until the card is used.",
    'giftCards.cash.ownBranch': "Taken into your branch's cash box: {branch}.",
    'giftCards.kind': 'Kind of change',
    'giftCards.kind.choose': '— Choose —',
    'giftCards.kind.topUp': 'Paid top-up',
    'giftCards.kind.cashOut': 'Cash refund',
    'giftCards.kind.correctionAdd': 'Correction: add',
    'giftCards.kind.correctionRemove': 'Correction: take away',
    'giftCards.kindHint.topUp': "Cash received: it goes into the branch's cash box.",
    'giftCards.kindHint.cashOut': "Cash handed back: it leaves the branch's cash box.",
    'giftCards.kindHint.correction': 'No money: nothing is booked.',
    'giftCards.adjustAmount': 'Amount',
    'giftCards.reason.topup': 'Paid top-up',
    'giftCards.reason.cashout': 'Cash refund',
    'acc.src.gift-card-sale': 'Gift card sale',
    'acc.src.gift-card-cash-out': 'Gift card cash refund',
  },
};
