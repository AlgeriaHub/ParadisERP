import { Component, computed, input } from '@angular/core';
import { PrintDocumentComponent } from './print-document.component';
import { PrintContext } from './print-context';
import { REPORT_TITLES, ReportPaper, periodText, reportMeta } from './print-reports';
import { PrintRepAccountStatementComponent } from './print-rep-account-statement.component';
import { PrintRepBalanceSheetComponent } from './print-rep-balance-sheet.component';
import { PrintRepJournalComponent } from './print-rep-journal.component';
import { PrintRepProfitLossComponent } from './print-rep-profit-loss.component';
import { PrintRepSalesComponent } from './print-rep-sales.component';
import { PrintRepTrialBalanceComponent } from './print-rep-trial-balance.component';
import { PrintRepItemStatusComponent } from './print-rep-item-status.component';

/**
 * One report on paper (print-reports.ts): the company's head, the report's formal title, the
 * period where the invoice has its number, the scope and the print time under it, the report's
 * own body, and two signature lines — prepared by, checked by — because a set of books handed to
 * an accountant or a bank is signed by whoever produced it.
 *
 * <p>No footer: a report is the company's own paper, so it carries neither the terms the owner
 * set for customer documents nor the receipt's thank-you line.</p>
 */
@Component({
  selector: 'app-print-report',
  standalone: true,
  imports: [
    PrintDocumentComponent,
    PrintRepTrialBalanceComponent,
    PrintRepProfitLossComponent,
    PrintRepBalanceSheetComponent,
    PrintRepAccountStatementComponent,
    PrintRepJournalComponent,
    PrintRepSalesComponent,
    PrintRepItemStatusComponent,
  ],
  template: `
    @if (ctx(); as c) {
      <app-print-sheet
        [ctx]="c"
        [kind]="null"
        [title]="c.t(titleKey())"
        [titleKey]="titleKey()"
        [number]="period()"
        [numberIsText]="true"
        [thanks]="false"
        [meta]="meta()"
      >
        @switch (report().name) {
          @case ('trial-balance') {
            <app-print-rep-trial-balance [ctx]="c" [report]="report()" />
          }
          @case ('profit-loss') {
            <app-print-rep-profit-loss [ctx]="c" [report]="report()" />
          }
          @case ('balance-sheet') {
            <app-print-rep-balance-sheet [ctx]="c" [report]="report()" />
          }
          @case ('account-statement') {
            <app-print-rep-account-statement [ctx]="c" [report]="report()" />
          }
          @case ('journal') {
            <app-print-rep-journal [ctx]="c" [report]="report()" />
          }
          @case ('sales') {
            <app-print-rep-sales [ctx]="c" [report]="report()" />
          }
          @case ('item-status') {
            <app-print-rep-item-status [ctx]="c" [report]="report()" />
          }
        }

        <div class="print-signatures">
          <div class="print-signature">{{ c.t('print.rep.preparedBy') }}</div>
          <div class="print-signature">{{ c.t('print.rep.checkedBy') }}</div>
        </div>
      </app-print-sheet>
    }
  `,
})
export class PrintReportComponent {
  readonly ctx = input.required<PrintContext>();
  readonly report = input.required<ReportPaper>();

  readonly titleKey = computed(() => REPORT_TITLES[this.report().name]);
  readonly period = computed(() => periodText(this.ctx(), this.report()));
  readonly meta = computed(() => reportMeta(this.ctx(), this.report()));
}
