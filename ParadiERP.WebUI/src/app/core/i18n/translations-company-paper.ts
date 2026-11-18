import { BuiltinLang } from '../models';

/**
 * Back-office vocabulary added for the owner's paperwork answers of 2026-09-30, kept in its own
 * file so several hands can add keys at once. Merged into `BACKOFFICE_TRANSLATIONS`.
 *
 * <p>This part: each branch's own fiscal identifiers (answer 2b), the company logo on printed
 * documents (answer 3) and the identity paper of a customer and a representative (answer 10).</p>
 */
export const COMPANY_PAPER_TRANSLATIONS: Record<BuiltinLang, Record<string, string>> = {
  fr: {
    'branchIds.title': 'Identifiants fiscaux de la succursale',
    'branchIds.hint':
      "Ceux que la succursale imprime sur ses propres documents. Une case vide imprime l'identifiant de l'entreprise, affiché en grisé dans la case.",
    'branchIds.own': 'Identifiants propres',

    'logo.title': 'Logo sur les documents',
    'logo.hint':
      "Imprimé en tête de chaque document, à côté du nom de l'entreprise. Sur une imprimante noir et blanc, choisissez la version noir et blanc du logo : c'est elle qui s'imprime le mieux.",
    'logo.choose': 'Choisir un fichier',
    'logo.replace': 'Remplacer',
    'logo.remove': 'Retirer le logo',
    'logo.none': "Aucun logo : l'en-tête imprime le nom de l'entreprise seul.",
    'logo.types': 'PNG, JPEG, WebP ou SVG, {kb} Ko au plus.',
    'logo.badType': "Ce fichier n'est pas une image PNG, JPEG, WebP ou SVG.",
    'logo.tooBig': 'Ce fichier fait {size} Ko ; le logo est limité à {kb} Ko.',
    'logo.unreadable': 'Ce fichier ne peut pas être lu.',
    'logo.pending': "Pas encore enregistré : cliquez sur Enregistrer pour l'appliquer aux documents.",

    'idDoc.type': 'Type de pièce',
    'idDoc.type.none': 'Non précisé',
    'idDoc.type.nationalCard': "Carte nationale d'identité",
    'idDoc.type.drivingLicence': 'Permis de conduire',
    'idDoc.type.passport': 'Passeport',
    'idDoc.number': 'N° de la pièce',
    'idDoc.issuer': 'Délivrée par',
    'idDoc.issuerHint': "L'autorité inscrite sur la pièce.",
    'idDoc.problem.number': 'Le numéro ne contient que des lettres, des chiffres, des espaces, « - » ou « / », et au moins un chiffre.',
    'idDoc.problem.issuer': "Indiquez l'autorité ou le lieu, pas seulement des chiffres.",
    'idDoc.problem.future': 'Cette date ne peut pas être dans le futur.',
    'idDoc.problem.tooOld': 'Cette date ne peut pas être avant 1900.',
    'idDoc.problem.beforeBirth': 'La pièce ne peut pas avoir été délivrée avant la naissance de son titulaire.',
    'idDoc.issuedOn': 'Délivrée le',
    'idDoc.contractHint':
      "Facultatif ici. Le contrat de location demande la pièce du signataire, son émetteur et sa date : ce qui est saisi ici les remplit d'avance.",
  },
  ar: {
    'branchIds.title': 'المعرفات الجبائية للفرع',
    'branchIds.hint':
      'التي يطبعها الفرع على مستنداته. الخانة الفارغة تطبع معرّف الشركة، وهو ظاهر باللون الباهت داخل الخانة.',
    'branchIds.own': 'معرفات خاصة',

    'logo.title': 'الشعار على المستندات',
    'logo.hint':
      'يُطبع في رأس كل مستند بجانب اسم الشركة. إذا كانت الطابعة بالأبيض والأسود فاختر نسخة الشعار بالأبيض والأسود، فهي التي تُطبع بأفضل وضوح.',
    'logo.choose': 'اختيار ملف',
    'logo.replace': 'استبدال',
    'logo.remove': 'إزالة الشعار',
    'logo.none': 'لا يوجد شعار: رأس المستند يطبع اسم الشركة وحده.',
    'logo.types': 'PNG أو JPEG أو WebP أو SVG، بحد أقصى {kb} كيلوبايت.',
    'logo.badType': 'هذا الملف ليس صورة PNG أو JPEG أو WebP أو SVG.',
    'logo.tooBig': 'حجم الملف {size} كيلوبايت، والحد الأقصى للشعار {kb} كيلوبايت.',
    'logo.unreadable': 'تعذّرت قراءة هذا الملف.',
    'logo.pending': 'لم يُحفظ بعد: اضغط «حفظ» ليُطبّق على المستندات.',

    'idDoc.type': 'نوع الوثيقة',
    'idDoc.type.none': 'غير محدد',
    'idDoc.type.nationalCard': 'بطاقة التعريف الوطنية',
    'idDoc.type.drivingLicence': 'رخصة السياقة',
    'idDoc.type.passport': 'جواز السفر',
    'idDoc.number': 'رقم الوثيقة',
    'idDoc.issuer': 'جهة الإصدار',
    'idDoc.issuerHint': 'الجهة المكتوبة على الوثيقة.',
    'idDoc.problem.number': 'الرقم يحتوي على حروف وأرقام ومسافات و« - » أو « / » فقط، وفيه رقم واحد على الأقل.',
    'idDoc.problem.issuer': 'اكتبوا الجهة أو المكان، وليس أرقاماً فقط.',
    'idDoc.problem.future': 'لا يمكن أن يكون هذا التاريخ في المستقبل.',
    'idDoc.problem.tooOld': 'لا يمكن أن يكون هذا التاريخ قبل سنة 1900.',
    'idDoc.problem.beforeBirth': 'لا يمكن أن تكون الوثيقة صادرة قبل تاريخ ميلاد صاحبها.',
    'idDoc.issuedOn': 'تاريخ الإصدار',
    'idDoc.contractHint':
      'اختياري هنا. عقد الإيجار يطلب وثيقة الموقّع وجهة إصدارها وتاريخه، وما يُدخل هنا يملؤها مسبقاً.',
  },
  en: {
    'branchIds.title': "Branch's fiscal identifiers",
    'branchIds.hint':
      "The ones this branch prints on its own documents. A blank field prints the company's identifier, shown greyed inside the field.",
    'branchIds.own': 'Own identifiers',

    'logo.title': 'Logo on documents',
    'logo.hint':
      "Printed at the head of every document, beside the company name. On a black-and-white printer, choose the black-and-white version of the logo: it is the one that prints best.",
    'logo.choose': 'Choose a file',
    'logo.replace': 'Replace',
    'logo.remove': 'Remove the logo',
    'logo.none': 'No logo: the head prints the company name alone.',
    'logo.types': 'PNG, JPEG, WebP or SVG, {kb} KB at most.',
    'logo.badType': 'This file is not a PNG, JPEG, WebP or SVG image.',
    'logo.tooBig': 'This file is {size} KB; the logo is limited to {kb} KB.',
    'logo.unreadable': 'This file cannot be read.',
    'logo.pending': 'Not saved yet: press Save to apply it to the documents.',

    'idDoc.type': 'Document type',
    'idDoc.type.none': 'Not said',
    'idDoc.type.nationalCard': 'National identity card',
    'idDoc.type.drivingLicence': 'Driving licence',
    'idDoc.type.passport': 'Passport',
    'idDoc.number': 'Document no.',
    'idDoc.issuer': 'Issued by',
    'idDoc.issuerHint': 'The authority written on the document.',
    'idDoc.problem.number': 'The number holds only letters, digits, spaces, “-” or “/”, and at least one digit.',
    'idDoc.problem.issuer': 'Name the authority or the place, not only figures.',
    'idDoc.problem.future': 'This date cannot be in the future.',
    'idDoc.problem.tooOld': 'This date cannot be before 1900.',
    'idDoc.problem.beforeBirth': 'The document cannot have been issued before its holder was born.',
    'idDoc.issuedOn': 'Issued on',
    'idDoc.contractHint':
      "Optional here. The rental contract asks for the signatory's document, its issuer and its date: what is typed here fills them in advance.",
  },
};
