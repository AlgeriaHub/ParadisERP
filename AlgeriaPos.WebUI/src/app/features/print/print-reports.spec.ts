import { TestBed } from '@angular/core/testing';
import { AccountStatement, LedgerAccount, LedgerEntry } from '../../core/ledger.service';
import { PrintContext } from './print-context';
import { PrintRepAccountStatementComponent } from './print-rep-account-statement.component';
import { PrintRepJournalComponent } from './print-rep-journal.component';
import { PrintRepItemStatusComponent } from './print-rep-item-status.component';
import { STATEMENT_PAPER_TAKE, ReportPaper, isReportName, lastCoveredDay, periodText, shareOf, sideWord, sourceWord } from './print-reports';
import { PrintService } from './print.service';

/**
 * The reports on paper (print-reports.ts). What is tested is what the screen print got wrong or
 * never said: the last day a window covers, the side a balance sits on, a cut statement saying it
 * is cut, and a journal read oldest first in the document's language.
 */
describe('print reports', () => {
  const ctx = {
    lang: 'ar',
    branches: [{ id: 'b-oran', nameAr: 'وهران', nameFr: 'Oran', nameEn: 'Oran' }],
    t: (key: string, params?: Record<string, string | number>) =>
      key === 'acc.src.unknown-module' ? key : params ? `«${key}:${Object.values(params).join('|')}»` : `«${key}»`,
    day: (iso: string) => iso.slice(0, 10),
    money: (v: number) => v.toFixed(2),
    name: (x: { nameAr: string; nameFr: string }) => x.nameAr || x.nameFr,
  } as unknown as PrintContext;

  describe('the window', () => {
    it('states the last day covered, not the exclusive bound the wire carries', () => {
      expect(lastCoveredDay('2026-10-09T00:00:00Z').slice(0, 10)).toBe('2026-10-08');
      // SQL Server rows can come back without the Z; read as UTC all the same.
      expect(lastCoveredDay('2026-11-01T00:00:00').slice(0, 10)).toBe('2026-10-31');
    });

    it('says a period for a trial balance and one day for a balance sheet', () => {
      const trial = { name: 'trial-balance', branchId: null, printedAt: '', trial: { from: '2026-10-01T00:00:00Z', to: '2026-10-09T00:00:00Z' } };
      expect(periodText(ctx, trial as unknown as ReportPaper)).toBe('«print.rep.period:2026-10-01|2026-10-08»');
      const sheet = { name: 'balance-sheet', branchId: null, printedAt: '', sheet: { asOf: '2026-10-09T00:00:00Z' } };
      expect(periodText(ctx, sheet as unknown as ReportPaper)).toBe('«print.rep.asOf:2026-10-08»');
      const itemStatus = { name: 'item-status', branchId: null, printedAt: '', report: { asOfUtc: '2026-10-12T15:00:00Z' }, detail: null };
      expect(isReportName('item-status')).toBeTrue();
      expect(periodText(ctx, itemStatus as unknown as ReportPaper)).toBe('«print.rep.asOf:2026-10-12»');
    });
  });

  describe('the words', () => {
    it('puts a positive balance on the account’s own side and a negative one on the other', () => {
      expect(sideWord(ctx, { type: 'Asset', balance: 10 })).toBe('«acc.debit»');
      expect(sideWord(ctx, { type: 'Asset', balance: -10 })).toBe('«acc.credit»');
      expect(sideWord(ctx, { type: 'Income', balance: 10 })).toBe('«acc.credit»');
      expect(sideWord(ctx, { type: 'Liability', balance: -1 })).toBe('«acc.debit»');
    });

    it('prints an unknown document type as itself, never as a translation key', () => {
      expect(sourceWord(ctx, 'sale')).toBe('«acc.src.sale»');
      expect(sourceWord(ctx, 'unknown-module')).toBe('unknown-module');
    });

    it('states a share of the total, isolated so "%" stays after the figure on an Arabic sheet', () => {
      expect(shareOf(25, 100)).toBe('⁦25 %⁩');
      expect(shareOf(0, 100)).toBe('');
      expect(shareOf(5, 0)).toBe('');
    });
  });

  describe('the account statement', () => {
    function draw(rows: number, branchId: string | null): PrintRepAccountStatementComponent {
      const fixture = TestBed.createComponent(PrintRepAccountStatementComponent);
      const statement = {
        accountId: 'a',
        code: '512',
        nameAr: 'البنك',
        nameFr: 'Banque',
        nameEn: 'Bank',
        type: 'Asset',
        from: '2026-10-01T00:00:00Z',
        to: '2026-10-09T00:00:00Z',
        branchId,
        opening: 100,
        closing: 160,
        rows: Array.from({ length: rows }, (_, i) => ({
          entryId: `e${i}`,
          entryDate: '2026-10-02T09:00:00Z',
          branchId: 'b-oran',
          sourceType: 'sale',
          sourceId: 's',
          description: null,
          isReversal: false,
          debit: 10,
          credit: 4,
          runningBalance: 100 + 6 * (i + 1),
        })),
      } as AccountStatement;
      const report = { name: 'account-statement', branchId, printedAt: '', statement, capped: rows >= STATEMENT_PAPER_TAKE };
      fixture.componentRef.setInput('ctx', ctx);
      fixture.componentRef.setInput('report', report as ReportPaper);
      fixture.detectChanges();
      return fixture.componentInstance;
    }

    it('calls the last figure the closing balance when every movement came back', () => {
      const c = draw(10, 'b-oran');
      expect(c.capped()).toBeFalse();
      expect(c.debit()).toBe(100);
      expect(c.credit()).toBe(40);
    });

    it('says it is cut, and calls the last figure the balance at that row, when the ceiling is reached', () => {
      expect(draw(STATEMENT_PAPER_TAKE, null).capped()).toBeTrue();
    });

    it('draws a branch column for the group only', () => {
      expect(draw(1, null).leadCols()).toBe(3);
      expect(draw(1, 'b-oran').leadCols()).toBe(2);
    });
  });

  describe('the journal', () => {
    const line = (id: string, accountId: string, debit: number, credit: number) => ({
      id,
      accountId,
      accountCode: accountId === 'a-512' ? '512' : '707',
      accountNameFr: accountId === 'a-512' ? 'Banque' : 'Ventes',
      accountType: 'Asset',
      branchId: 'b-oran',
      debit,
      credit,
      memo: null,
    });
    const entry = (id: string, entryDate: string): LedgerEntry =>
      ({
        id,
        entryDate,
        createdAtUtc: entryDate,
        branchId: 'b-oran',
        branchName: 'Oran',
        sourceType: 'sale',
        sourceId: 's',
        description: null,
        isReversal: false,
        isInterBranch: false,
        counterpartBranchId: null,
        createdBy: null,
        total: 50,
        lines: [line(`${id}-1`, 'a-512', 50, 0), line(`${id}-2`, 'a-707', 0, 50)],
      }) as LedgerEntry;
    const chart = [{ id: 'a-512', code: '512', nameAr: 'البنك', nameFr: 'Banque', nameEn: 'Bank' } as LedgerAccount];

    function draw(entries: LedgerEntry[]): PrintRepJournalComponent {
      const fixture = TestBed.createComponent(PrintRepJournalComponent);
      const report = { name: 'journal', branchId: 'b-oran', printedAt: '', from: '', to: '', source: null, entries, accounts: chart, capped: false };
      fixture.componentRef.setInput('ctx', ctx);
      fixture.componentRef.setInput('report', report as ReportPaper);
      fixture.detectChanges();
      return fixture.componentInstance;
    }

    it('reads oldest first, though the server sends the newest first', () => {
      const c = draw([entry('late', '2026-10-05T10:00:00Z'), entry('early', '2026-10-02T10:00:00Z')]);
      expect(c.entries().map((e) => e.id)).toEqual(['early', 'late']);
    });

    it('totals both sides from the lines', () => {
      const c = draw([entry('one', '2026-10-02T10:00:00Z'), entry('two', '2026-10-03T10:00:00Z')]);
      expect(c.debit()).toBe(100);
      expect(c.credit()).toBe(100);
    });

    it('names a line from the chart in the document language, and keeps the French name otherwise', () => {
      const c = draw([entry('one', '2026-10-02T10:00:00Z')]);
      const [bank, sales] = c.entries()[0].lines;
      expect(c.account(bank)).toBe('البنك');
      expect(c.account(sales)).toBe('Ventes');
    });
  });

  describe('item movement and status', () => {
    function draw(hasDetail: boolean): PrintRepItemStatusComponent {
      const fixture = TestBed.createComponent(PrintRepItemStatusComponent);
      const report = {
        name: 'item-status',
        branchId: 'b-oran',
        printedAt: '2026-10-12T15:00:00Z',
        report: {
          asOfUtc: '2026-10-12T15:00:00Z',
          scope: { kind: 'company', locationId: null, othersAvailableShown: true },
          rows: [
            {
              productId: 'p1',
              sku: 'SKU-001',
              barcode: '123456',
              nameAr: 'صنف 1',
              nameFr: 'Article 1',
              nameEn: 'Item 1',
              unit: 'U',
              isRentable: true,
              isActive: true,
              total: 10,
              warehouses: 2,
              branches: 3,
              available: 5,
              rented: 3,
              maintenance: 1,
              scrap: 1,
              stillOut: 3,
              unexplainedOnRent: 0,
              leftToIssue: 0,
              otherAvailable: 2,
            },
          ],
          totals: {
            total: 10,
            warehouses: 2,
            branches: 3,
            available: 5,
            rented: 3,
            maintenance: 1,
            scrap: 1,
            stillOut: 3,
            unexplainedOnRent: 0,
            leftToIssue: 0,
            otherAvailable: 2,
          },
        },
        detail: hasDetail
          ? {
              asOfUtc: '2026-10-12T15:00:00Z',
              scope: { kind: 'company', locationId: null, othersAvailableShown: true },
              item: {
                productId: 'p1',
                sku: 'SKU-001',
                barcode: '123456',
                nameAr: 'صنف 1',
                nameFr: 'Article 1',
                nameEn: 'Item 1',
                unit: 'U',
                isRentable: true,
                isActive: true,
                total: 10,
                warehouses: 2,
                branches: 3,
                available: 5,
                rented: 3,
                maintenance: 1,
                scrap: 1,
                stillOut: 3,
                unexplainedOnRent: 0,
                leftToIssue: 0,
                otherAvailable: 2,
              },
              locations: [
                {
                  branchId: 'b-oran',
                  code: 'ORAN',
                  nameAr: 'وهران',
                  nameFr: 'Oran',
                  nameEn: 'Oran',
                  isWarehouse: false,
                  isMain: true,
                  isActive: true,
                  availableOnly: false,
                  available: 5,
                  onRent: 3,
                  maintenance: 1,
                  scrap: 1,
                  total: 10,
                  stillOut: 3,
                  unexplainedOnRent: 0,
                },
              ],
              rented: [],
              leftToIssue: [],
              movements: {
                from: '2026-10-01',
                to: '2026-10-12',
                opening: 5,
                closing: 10,
                unrecordedDifference: null,
                truncated: false,
                rows: [],
              },
            }
          : null,
      };
      fixture.componentRef.setInput('ctx', ctx);
      fixture.componentRef.setInput('report', report as unknown as ReportPaper);
      fixture.detectChanges();
      return fixture.componentInstance;
    }

    it('renders summary and scope sentence', () => {
      const c = draw(false);
      expect(c.scopeSentence()).toBe('«itemStatus.scope.company»');
      expect(c.itemReport()?.rows.length).toBe(1);
      expect(c.detail()).toBeNull();
    });

    it('renders detail when present', () => {
      const c = draw(true);
      expect(c.detail()).not.toBeNull();
      expect(c.detail()?.locations.length).toBe(1);
    });
  });

  describe('the link', () => {
    it('carries the branch, the account and the kind of document a report was run with', () => {
      const url = new PrintService().url('report', 'account-statement', {
        lang: 'ar',
        from: '2026-10-01T00:00:00.000Z',
        to: '2026-10-09T00:00:00.000Z',
        branch: 'b-oran',
        account: 'a-512',
        source: null,
      });
      const query = new URLSearchParams(url.split('?').pop());
      expect(url).toContain('#/print/report/account-statement?');
      expect(query.get('branch')).toBe('b-oran');
      expect(query.get('account')).toBe('a-512');
      expect(query.has('source')).toBeFalse();
      expect(query.get('to')).toBe('2026-10-09T00:00:00.000Z');
    });

    it('carries filters and item for item-status report', () => {
      const url = new PrintService().url('report', 'item-status', {
        lang: 'fr',
        filters: 'branchId=b-oran&itemType=rental',
        item: 'p1',
      });
      const query = new URLSearchParams(url.split('?').pop());
      expect(url).toContain('#/print/report/item-status?');
      expect(query.get('filters')).toBe('branchId=b-oran&itemType=rental');
      expect(query.get('item')).toBe('p1');
    });
  });
});
