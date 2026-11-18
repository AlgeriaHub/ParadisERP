/** Screen-local copy; shared rental and money wording remains in the interface dictionaries. */
const copy = {
  en: { items: 'Rental items', add: 'Add item', done: 'Done', edit: 'Edit', actions: 'Actions', units: 'Total units', count: 'Items', added: 'Item added', review: 'Review this line', search: 'Scan a barcode or search by item name or code', linked: 'Linked to', remove: 'Remove', editor: 'Edit rental item', noPrice: 'No price yet', notePrice: '{v} a day', noteStock: '{v} in stock', noteJoin: ' · ', empty: 'No items yet: scan a barcode or search above.', needOne: 'Add at least one item.' },
  fr: { items: 'Articles loués', add: 'Ajouter un article', done: 'Terminé', edit: 'Modifier', actions: 'Actions', units: 'Unités totales', count: 'Articles', added: 'Article ajouté', review: 'Vérifier cette ligne', search: 'Scanner un code-barres ou rechercher par nom ou code', linked: 'Lié à', remove: 'Supprimer', editor: 'Modifier l’article loué', noPrice: 'Pas encore de prix', notePrice: '{v} par jour', noteStock: '{v} en stock', noteJoin: ' · ', empty: 'Aucun article pour l’instant : scannez un code-barres ou cherchez ci-dessus.', needOne: 'Ajoutez au moins un article.' },
  ar: { items: 'الأصناف المؤجرة', add: 'إضافة بند', done: 'تم', edit: 'تعديل', actions: 'الإجراءات', units: 'إجمالي الوحدات', count: 'الأصناف', added: 'تمت إضافة الصنف', review: 'راجع بيانات البند', search: 'امسح الباركود أو ابحث باسم أو كود الصنف', linked: 'مرتبط بـ', remove: 'حذف', editor: 'تعديل البند المؤجر', noPrice: 'بلا سعر بعد', notePrice: 'اليوم بـ {v}', noteStock: 'المتوفر {v}', noteJoin: ' — ', empty: 'لا أصناف بعد: امسح الباركود أو ابحث في الأعلى.', needOne: 'أضف صنفاً واحداً على الأقل.' },
};
export type BuilderCopyKey = keyof typeof copy.en;
export function builderCopy(language: string, key: BuilderCopyKey): string {
  return (copy[language as keyof typeof copy] ?? copy.en)[key];
}
