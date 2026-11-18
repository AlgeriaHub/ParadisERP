import { Lang } from './models';

export interface MultilingualName {
  nameAr: string;
  nameFr: string;
  nameEn: string;
}

/**
 * The reader's name for a record, falling back through the languages that do have one.
 *
 * <p>Lives in core rather than beside the back office because the product picker is shared
 * with the till, and a shared control reaching into a feature folder for a formatting rule is
 * how the rule ends up copied instead. `backoffice-format` re-exports it, so the fourteen
 * screens that already import it from there are unaffected.</p>
 */
export function localizedName(value: Partial<MultilingualName> | null | undefined, lang: Lang): string {
  if (!value) return '';
  const chosen = lang === 'ar' ? value.nameAr : lang === 'en' ? value.nameEn : value.nameFr;
  return chosen || value.nameFr || value.nameEn || value.nameAr || '';
}
