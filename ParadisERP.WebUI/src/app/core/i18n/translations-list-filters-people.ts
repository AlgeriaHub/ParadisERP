import { BuiltinLang } from '../models';

/**
 * Filter words of the people lists (owner, 2026-11-17: every list pages and filters). Shared words
 * (`filters.*`) live in `translations-list-filters.ts`; reuse them first.
 * Merged into `BACKOFFICE_TRANSLATIONS`.
 */
export const LIST_FILTERS_PEOPLE_TRANSLATIONS: Record<BuiltinLang, Record<string, string>> = {
  fr: {
    // Attendance review: a point to check, named without its minutes.
    'hr.filter.flag.late': 'Retard',
    'hr.filter.flag.earlyLeave': 'Départ anticipé',
    'hr.filter.flag.overtime': 'Heures supplémentaires possibles',
    'users.filter.linked': 'Lié à un employé',
    'roles.filter.custom': "Rôle de l'entreprise",
    'devices.filter.synced': 'Déjà synchronisée',
  },
  ar: {
    'hr.filter.flag.late': 'تأخر',
    'hr.filter.flag.earlyLeave': 'خروج مبكر',
    'hr.filter.flag.overtime': 'ساعات إضافية محتملة',
    'users.filter.linked': 'مرتبط بموظف',
    'roles.filter.custom': 'دور أنشأته الشركة',
    'devices.filter.synced': 'تمت مزامنته',
  },
  en: {
    'hr.filter.flag.late': 'Late',
    'hr.filter.flag.earlyLeave': 'Left early',
    'hr.filter.flag.overtime': 'Possible overtime',
    'users.filter.linked': 'Linked to an employee',
    'roles.filter.custom': "Company's own role",
    'devices.filter.synced': 'Synced at least once',
  },
};
