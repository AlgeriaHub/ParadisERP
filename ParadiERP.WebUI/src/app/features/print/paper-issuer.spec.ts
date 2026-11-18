import { TestBed } from '@angular/core/testing';
import { AdminBranch, AdminStore } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { Lang } from '../../core/models';
import { DEFAULT_REGIONAL } from '../../core/regional.service';
import { ContractDocuments, IssueNote, ReturnNote } from '../backoffice/rentals/rentals-api.service';
import { sampleContract } from './contract-preview.component';
import { buildPrintContext, documentBranchId } from './print-context';
import { PrintDocumentComponent } from './print-document.component';
import { PrintIssueNoteComponent } from './print-issue-note.component';
import { PrintReturnNoteComponent } from './print-return-note.component';

/**
 * Owner, 2026-10-20: "all files printed from one branch must contain its information; where it has
 * none, the main branch's." An issue note raised at Skikda printed the company's name and the head
 * office's address above its own "goods out from Skikda".
 */
describe('a paper raised at a branch speaks as that branch', () => {
  const store = {
    nameFr: 'Company', nameAr: 'الشركة', nameEn: 'Company',
    address: 'Company address', phone: '0300', receiptFooter: '', fiscalIds: { nif: 'company-nif', rc: 'company-rc' },
  } as unknown as AdminStore;
  const main = {
    id: 'main', isMain: true, nameFr: 'Main branch', nameAr: 'الفرع الرئيسي',
    address: 'Main address', phone: '0200', fiscalIds: { nif: 'main-nif' },
  } as unknown as AdminBranch;
  const own = {
    id: 'own', nameFr: 'Local branch', nameAr: 'الفرع المحلي', nameEn: 'Local branch',
    addressFr: 'Local address', addressAr: 'عنوان الفرع المحلي', phone: '0100',
    fiscalIds: { rc: 'local-rc', nif: '  ' },
  } as unknown as AdminBranch;
  const warehouse = {
    id: 'warehouse', isMain: true, isWarehouse: true, nameFr: 'Warehouse',
    address: 'Warehouse address', phone: '0900', fiscalIds: { nif: 'warehouse-nif' },
  } as unknown as AdminBranch;

  /** The context the print page builds: the document's branch read off the document itself. */
  function context(lang: Lang, document: object) {
    const i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);
    return buildPrintContext(i18n, DEFAULT_REGIONAL, lang, {
      store, branches: [warehouse, main, own], customers: [], methods: [], documents: null,
      branchId: documentBranchId(document),
    });
  }

  /** A contract signed at the main branch, so a note at `own` cannot borrow the contract's branch. */
  function contractDocs(lang: Lang): ContractDocuments {
    const i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);
    const sample = sampleContract((l, key) => i18n.tIn(l, key), lang, 'rentalContract', 'main', '2026-10-19');
    return { contract: sample.contract, issueNotes: [], returnNotes: [], extensions: [], settlement: null } as unknown as ContractDocuments;
  }

  function expectLocalHead(el: HTMLElement, lang: Lang): void {
    const head = el.querySelector('.print-company')?.textContent ?? '';
    expect(el.querySelector('.print-company__name')?.textContent?.trim()).toBe(lang === 'ar' ? 'الفرع المحلي' : 'Local branch');
    expect(head).toContain(lang === 'ar' ? 'عنوان الفرع المحلي' : 'Local address');
    expect(head).toContain('0100');
    expect(head).toContain('local-rc');
    // The branch's NIF is blank: the main branch's, never the company's.
    expect(head).toContain('main-nif');
    expect(head).not.toContain('company-nif');
    expect(el.textContent).not.toContain('Company address');
  }

  for (const lang of ['ar', 'fr'] as Lang[]) {
    it(`prints an issue note's own branch in its ${lang} head`, () => {
      const docs = contractDocs(lang);
      const note = {
        id: 'n1', number: 'ISS-000032', contractId: docs.contract.id, branchId: 'own',
        issuedAtUtc: '2026-10-20T08:00:00Z', userName: 'admin', note: null, periodRent: 0, lines: [],
        customerTransport: true,
      } as IssueNote;
      const fixture = TestBed.createComponent(PrintIssueNoteComponent);
      fixture.componentRef.setInput('ctx', context(lang, { kind: 'issue-note', docs, note, products: [] }));
      fixture.componentRef.setInput('docs', docs);
      fixture.componentRef.setInput('note', note);
      fixture.detectChanges();
      expectLocalHead(fixture.nativeElement as HTMLElement, lang);
      fixture.destroy();
    });

    it(`prints a return note's own branch in its ${lang} head`, () => {
      const docs = contractDocs(lang);
      const note = {
        id: 'r1', number: 'RET-000007', contractId: docs.contract.id, branchId: 'own',
        returnedAtUtc: '2026-10-22T08:00:00Z', userName: 'admin', note: null, unusedRent: 0,
        reliefDebtReduction: 0, reliefPaymentVoucher: 0, chargeTotal: 0, contractClosed: false,
        lines: [], charges: [], transfers: [], customerTransport: true,
      } as ReturnNote;
      const fixture = TestBed.createComponent(PrintReturnNoteComponent);
      fixture.componentRef.setInput('ctx', context(lang, { kind: 'return-note', docs, note, products: [] }));
      fixture.componentRef.setInput('docs', docs);
      fixture.componentRef.setInput('note', note);
      fixture.detectChanges();
      expectLocalHead(fixture.nativeElement as HTMLElement, lang);
      fixture.destroy();
    });
  }

  function sheet(document: object): HTMLElement {
    const fixture = TestBed.createComponent(PrintDocumentComponent);
    fixture.componentRef.setInput('ctx', context('fr', document));
    fixture.componentRef.setInput('kind', null);
    fixture.componentRef.setInput('title', 'Paper');
    fixture.componentRef.setInput('number', 'X-1');
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('prints the branch on every paper that names one, a transfer as its sending branch', () => {
    expectLocalHead(sheet({ kind: 'invoice', invoice: { branchId: 'own', number: 'INV-1' } }), 'fr');
    expectLocalHead(sheet({ kind: 'payment-voucher', voucher: { branchId: 'own' } }), 'fr');
    expectLocalHead(sheet({ kind: 'stock-transfer', transfer: { fromBranchId: 'own', toBranchId: 'main' } }), 'fr');
  });

  it("keeps the company's identity on a paper of no branch", () => {
    const el = sheet({ kind: 'statement', statement: { customerId: 'c' }, ageing: null, asOf: null });
    expect(el.querySelector('.print-company__name')?.textContent?.trim()).toBe('Company');
    expect(el.querySelector('.print-company')?.textContent).toContain('Company address');
    expect(el.querySelector('.print-company')?.textContent).toContain('company-nif');
  });
});
