import { I18nService } from '../../../core/i18n.service';
import { qty } from './manufacturing-shared';
import { CostGap } from './production-api.service';

/** Gap and change codes whose value is money; the others carry hours, a quantity, days or a count. */
const MONEY = new Set(['note_expense_unsettled', 'expense_added', 'expense_removed']);

/**
 * The words for costing B2's gap and source-change codes (`ProductionCostHistory`): each says what it is in the
 * reader's language, with its figure. A withheld figure (single-worker hours) is simply not said.
 */
export function costWords(i18n: I18nService) {
  const figure = (g: CostGap): string => (g.value === null ? '' : MONEY.has(g.code) ? i18n.money(g.value) : qty(g.value));
  return {
    gap(g: CostGap): string {
      if (g.code === 'check_failed') return i18n.t('mfg.b2.gap.check_failed', { check: i18n.t('mfg.b2.check.' + (g.detail ?? '')) });
      if (g.code.startsWith('element_')) {
        return i18n.t('mfg.b2.gap.element', {
          element: i18n.t('mfg.cost.el.' + (g.element ?? '')),
          status: i18n.t('mfg.cost.elementStatus.' + g.code.slice('element_'.length)),
        });
      }
      // Engine notes reuse Phase A's sentences (mfg.cost.note.<code>); a single worker's withheld hours get their own.
      if (g.code.startsWith('note_')) {
        if (g.element === 'labour' && g.value === null) return i18n.t('mfg.b2.gap.' + g.code + '_withheld');
        return i18n.t('mfg.cost.note.' + g.code.slice('note_'.length), { value: figure(g) });
      }
      return i18n.t('mfg.b2.gap.' + g.code, { value: figure(g) });
    },
    change(c: CostGap): string {
      // Hours on an order one employee worked are withheld: the change is then said without them.
      const withheld = c.value === null && c.code.startsWith('booking_') ? '_withheld' : '';
      return i18n.t('mfg.b2.change.' + c.code + withheld, { value: figure(c) });
    },
  };
}
