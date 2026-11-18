import { ApiError } from '../../../core/api.service';
import { adminErrorMessage, problemCode } from '../../../core/admin.service';
import type { PosUser } from '../../../core/models';

/**
 * What the manufacturing screens share: the five permission keys as the server spells them
 * (`PermissionCatalog`), read from the capabilities the login answered — never from the role, so a
 * branch manager given a grant sees exactly what the server will let them do — and the refusal a
 * request came back with, in the reader's language.
 */
export const MFG = {
  view: 'manufacturing.view',
  lines: 'manufacturing.lines.manage',
  bom: 'manufacturing.bom.manage',
  approve: 'manufacturing.bom.approve',
  settings: 'manufacturing.settings',
  // Phase 2: production orders.
  ordersCreate: 'manufacturing.orders.create',
  ordersStartShort: 'manufacturing.orders.start_short',
  ordersManage: 'manufacturing.orders.manage',
  materialsMove: 'manufacturing.materials.move',
  logSubmit: 'manufacturing.log.submit',
  logApprove: 'manufacturing.log.approve',
  labourOverride: 'manufacturing.labour.override',
  outputRelease: 'manufacturing.output.release',
  completionApprove: 'manufacturing.completion.approve',
  reports: 'manufacturing.reports.view',
  // Costing, Phase A: read-only cost previews.
  costsView: 'manufacturing.costs.view',
  costSnapshotCreate: 'manufacturing.costs.snapshot.create',
  costsRatesView: 'manufacturing.costs.rates.view',
} as const;

export type MfgKey = (typeof MFG)[keyof typeof MFG];

/** The configuration keys (Phase 1), in the catalogue's order. */
export const MFG_CONFIG_KEYS: readonly MfgKey[] = [MFG.view, MFG.lines, MFG.bom, MFG.approve, MFG.settings];

/** The production keys (Phase 2), in the catalogue's order. */
export const MFG_PRODUCTION_KEYS: readonly MfgKey[] = [
  MFG.ordersCreate, MFG.ordersStartShort, MFG.ordersManage, MFG.materialsMove, MFG.logSubmit,
  MFG.logApprove, MFG.labourOverride, MFG.outputRelease, MFG.completionApprove, MFG.reports,
];

/** The costing keys (Phase A), in the catalogue's order. */
export const MFG_COSTING_KEYS: readonly MfgKey[] = [MFG.costsView, MFG.costSnapshotCreate, MFG.costsRatesView];

/** Every key in the catalogue's order, as the access screen lists them. */
export const MFG_KEYS: readonly MfgKey[] = [...MFG_CONFIG_KEYS, ...MFG_PRODUCTION_KEYS, ...MFG_COSTING_KEYS];

export function mfgCan(user: Pick<PosUser, 'capabilities'> | null | undefined, key: MfgKey): boolean {
  return user?.capabilities?.includes(key) ?? false;
}

/**
 * Sees pay rates, and with them every per-order cost (owner, 2026-11-03; the server's CostPrivacy): the rates key and
 * payroll.manage together. Presentation only — the server withholds the figures itself.
 */
export function mfgSeesPay(user: Pick<PosUser, 'capabilities' | 'permissions'> | null | undefined): boolean {
  return mfgCan(user, MFG.costsRatesView) && (user?.permissions ?? []).includes('payroll.manage');
}

/** May open the manufacturing module at all: any one of its keys reads its configuration. */
export function mfgAny(user: Pick<PosUser, 'capabilities'> | null | undefined): boolean {
  return MFG_KEYS.some((key) => mfgCan(user, key));
}

/**
 * A refusal in words: the server's stable `code` translated (`mfg.error.<code>`) when the dictionary
 * has it — the server's own sentence is English and would put a foreign line on an Arabic screen —
 * else that sentence, else the shared admin message.
 */
export function mfgError(error: unknown, t: (key: string) => string): string {
  const code = problemCode(error);
  if (code) {
    const key = `mfg.error.${code}`;
    const text = t(key);
    if (text !== key) return text;
    const detail = problemDetail(error);
    if (detail) return detail;
  }
  return adminErrorMessage(error);
}

/** The ProblemDetails `detail` sentence, when the refusal carried one. */
function problemDetail(error: unknown): string | null {
  if (!(error instanceof ApiError)) return null;
  try {
    const parsed = JSON.parse(error.message) as { detail?: unknown };
    return typeof parsed?.detail === 'string' && parsed.detail.trim() ? parsed.detail : null;
  } catch {
    return null;
  }
}

/** A quantity as typed, without the float noise of 0.1 + 0.2: up to six places, trailing zeros dropped. */
export function qty(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—';
  return String(Math.round(value * 1_000_000) / 1_000_000);
}
