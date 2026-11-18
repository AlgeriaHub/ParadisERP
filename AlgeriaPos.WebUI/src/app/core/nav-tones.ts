/**
 * The section tone each back-office module wears (owner, 2026-11-12; classes in src/_tones.scss).
 * Each module wears a fixed accent on its menu icon, page icon, toolbar wash, card headers, table
 * headers, count pills, active tab and stat tiles. Buttons, links, inputs, status pills and
 * pagination stay in the brand colour. Class names are never built at run time.
 */
export type ToneClass =
  | 'tone-brand'
  | 'tone-sales'
  | 'tone-rentals'
  | 'tone-finance'
  | 'tone-stock'
  | 'tone-people'
  | 'tone-admin';

export const TONE_CLASSES: readonly ToneClass[] = [
  'tone-brand',
  'tone-sales',
  'tone-rentals',
  'tone-finance',
  'tone-stock',
  'tone-people',
  'tone-admin',
];

export const MODULE_TONES: Readonly<Record<string, ToneClass>> = {
  dashboard: 'tone-brand',
  pos: 'tone-brand',
  reports: 'tone-brand',
  sales: 'tone-sales',
  customers: 'tone-sales',
  rentals: 'tone-rentals',
  finance: 'tone-finance',
  inventory: 'tone-stock',
  manufacturing: 'tone-stock',
  maintenance: 'tone-stock',
  employees: 'tone-people',
  admin: 'tone-admin',
};

/** A module's tone class, whole (never assembled from parts); the brand for a page outside every module. */
export function moduleTone(moduleKey: string | null | undefined): ToneClass {
  return moduleKey && Object.prototype.hasOwnProperty.call(MODULE_TONES, moduleKey)
    ? MODULE_TONES[moduleKey]
    : 'tone-brand';
}
