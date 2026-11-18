import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { AdminBranch, AdminCustomer, AdminProduct, AdminService, AdminStore } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { Lang } from '../../core/models';
import { RegionalService } from '../../core/regional.service';
import { SecureImageService } from '../../core/secure-image.service';
import { RentalContract, RentalContractLine } from '../backoffice/rentals/rentals-api.service';
import { buildPrintContext } from './print-context';
import { PrintRentalContractComponent } from './print-rental-contract.component';
import { DocumentTextKey, StoreDocumentsDto } from './store-documents-api.service';

/** The made-up contract the preview prints, with the customer and the items it names. */
export interface SampleContract {
  contract: RentalContract;
  customer: AdminCustomer;
  products: AdminProduct[];
}

const SAMPLE_CUSTOMER = 'sample-customer';

/**
 * A contract nobody signed, for the preview: three lines (one an accessory, so the indent and the
 * inherited period show), a deposit, a site and a signatory, so every block of the paper and every
 * field of the wording has something to print. The customer, the site and the signatory are named
 * in the document language and say they are samples; the items carry their names in all three
 * languages, as a real line does, so the company's item-name setting previews as it will print.
 * The figures are plain numbers: the paper formats them in the company's own currency.
 *
 * @param t The dictionary in a given language (`I18nService.tIn`).
 * @param branchId The company's own branch, so the place and the head print a real one.
 */
export function sampleContract(
  t: (lang: Lang, key: string) => string,
  lang: Lang,
  wordingKey: DocumentTextKey,
  branchId: string,
  signedAtUtc: string,
): SampleContract {
  const words = (key: string) => t(lang, 'store.docs.preview.sample.' + key);
  const products = [1, 2, 3].map(
    (n) =>
      ({
        id: `sample-product-${n}`,
        sku: `SMP-00${n}`,
        nameFr: t('fr', `store.docs.preview.sample.item${n}`),
        nameAr: t('ar', `store.docs.preview.sample.item${n}`),
        nameEn: t('en', `store.docs.preview.sample.item${n}`),
        isRentable: true,
      }) as AdminProduct,
  );
  const line = (n: number, qty: number, days: number, rent: number, damage: number, parent: string | null): RentalContractLine => ({
    id: `sample-line-${n}`,
    productId: products[n - 1].id,
    sku: products[n - 1].sku,
    name: products[n - 1].nameFr,
    nameAr: products[n - 1].nameAr,
    qty,
    durationDays: days,
    rentUnitPrice: rent,
    damageUnitPrice: damage,
    // Never printed (answer 7c); the line only needs one.
    overdueUnitPrice: rent,
    parentLineId: parent,
    issuedQty: 0,
    leftToIssueQty: qty,
    settledQty: 0,
    stillOutQty: 0,
    taxRate: 0,
  });
  const customer = {
    id: SAMPLE_CUSTOMER,
    code: 'Cust-000000',
    fullName: words('customer'),
    address: words('address'),
    phone: null,
    taxId: null,
    email: null,
    birthDate: null,
    kind: 'Person',
    idDocumentNumber: null,
    representative: null,
  } as AdminCustomer;
  const contract: RentalContract = {
    id: 'sample-contract',
    number: 'CTR-000000',
    customerId: SAMPLE_CUSTOMER,
    customerName: customer.fullName,
    branchId,
    status: 'Draft',
    isLocked: false,
    lockedAtUtc: null,
    signedAtUtc,
    closedAtUtc: null,
    notes: null,
    lines: [
      line(1, 20, 30, 50, 12000, null),
      line(2, 20, 30, 10, 2500, 'sample-line-1'),
      line(3, 40, 15, 20, 3500, null),
    ],
    depositAmount: 50000,
    signatoryName: words('signatory'),
    signatoryIdNumber: '123456789',
    signatoryIdType: 'nationalCard',
    signatoryRequired: true,
    customerKind: 'Person',
    siteName: words('site'),
    siteAddress: null,
    // The standard wording is the absence of a named one, exactly as on a real contract.
    wordingKey: wordingKey === 'rentalContract' ? null : wordingKey,
  };
  return { contract, customer, products };
}

/**
 * The rental contract as it would print, from the company settings (owner, 2026-10-08: "the admin
 * can create the rental contract template and preview it").
 *
 * <p>It is the real paper — `PrintRentalContractComponent` in the real frame, over the real company
 * head, logo, fiscal identifiers and regional settings — with one difference: the contract is
 * {@link sampleContract}, built here, so no contract is read and nothing is written. The wording is
 * whatever the host hands in (the editor's unsaved draft), so previewing never spends one of the
 * year's changes.</p>
 *
 * <p>The paper's styles come from `_paper.scss`, nested under this component's host
 * (contract-preview.scss): the print route keeps its global sheet, and not one paper rule reaches
 * the back office around the dialog. The sheet carries its own `dir` and `lang`, as on the print
 * route, so an Arabic preview mirrors while the settings page stays in the reader's direction.</p>
 */
@Component({
  selector: 'app-contract-preview',
  standalone: true,
  imports: [PrintRentalContractComponent],
  styleUrl: './contract-preview.scss',
  template: `
    @if (ctx(); as c) {
      <div class="print-page" [attr.dir]="c.dir" [attr.lang]="c.lang">
        <app-print-rental-contract [ctx]="c" [contract]="sample().contract" [products]="sample().products" [copies]="1" />
      </div>
    } @else {
      <p class="notice" role="status">{{ i18n.t('common.loading') }}</p>
    }
  `,
})
export class ContractPreviewComponent {
  readonly i18n = inject(I18nService);
  private readonly regional = inject(RegionalService);
  private readonly admin = inject(AdminService);
  private readonly images = inject(SecureImageService);

  /** The printed-document settings as the editor holds them, the wording being edited included. */
  readonly documents = input.required<StoreDocumentsDto>();
  /** The wording previewed: `rentalContract` or a named one's key. */
  readonly wordingKey = input.required<DocumentTextKey>();
  /** The document language, which is not the reader's: an Arabic preview from a French screen. */
  readonly lang = input.required<Lang>();

  private readonly company = signal<{ store: AdminStore | null; branches: AdminBranch[] } | null>(null);
  /** The logo as paper carries it: inline, fetched once per link. */
  private readonly logo = signal<string | null>(null);
  /** The day the preview opened: a sample signed today. */
  private readonly today = new Date().toISOString();

  constructor() {
    // The company's own head, read as the print route reads it; a part that cannot be had prints
    // without it, as there.
    void Promise.all([
      this.admin.getStore().catch(() => null),
      this.admin.listBranches().catch((): AdminBranch[] => []),
    ]).then(([store, branches]) => this.company.set({ store, branches }));

    effect(() => {
      const link = this.documents().logoUrl ?? null;
      void this.images.dataUrl(link).then((src) => {
        if ((this.documents().logoUrl ?? null) === link) this.logo.set(src);
      });
    });
  }

  /** The main branch that trades, else the first: the place a real contract would name. */
  private readonly branchId = computed(() => {
    const branches = this.company()?.branches ?? [];
    return (branches.find((b) => b.isMain && !b.isWarehouse) ?? branches.find((b) => !b.isWarehouse) ?? branches[0])?.id ?? '';
  });

  readonly sample = computed(() =>
    sampleContract((lang, key) => this.i18n.tIn(lang, key), this.lang(), this.wordingKey(), this.branchId(), this.today),
  );

  readonly ctx = computed(() => {
    const company = this.company();
    if (!company) return null;
    const sample = this.sample();
    return buildPrintContext(this.i18n, this.regional.settings(), this.lang(), {
      store: company.store,
      documents: { ...this.documents(), logoUrl: this.logo() },
      branches: company.branches,
      customers: [sample.customer],
      methods: [],
      branchId: sample.contract.branchId || null,
    });
  });
}
