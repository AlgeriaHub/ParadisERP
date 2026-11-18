/**
 * The notes of a job's cost (Phase M4) in the reader's language. The cost engine's own codes reuse manufacturing's sentences
 * (`mfg.cost.note.<code>`); the ones that read differently for a job — or that only a job has — are `maint.cost.note.<code>`.
 */
const OWN = new Set([
  'labour_excluded', 'labour_workshop_rate', 'costing_policy_not_approved', 'material_reference_cost', 'overhead_not_set',
]);
const MONEY = new Set(['expense_unsettled', 'labour_workshop_rate']);
const HOURS = new Set(['labour_standard_rate', 'labour_unvalued', 'labour_overtime_without_premium', 'labour_excluded']);

export function costNote(
  code: string,
  value: number | null,
  t: (key: string, params?: Record<string, string | number>) => string,
  money: (n: number) => string,
  qty: (n: number) => string,
): string {
  const amount = value === null ? '' : MONEY.has(code) ? money(value) : HOURS.has(code) ? qty(value) : String(value);
  return t((OWN.has(code) ? 'maint.cost.note.' : 'mfg.cost.note.') + code, { value: amount });
}
