import { Component, computed, input } from '@angular/core';
import { StockTransfer } from '../backoffice/stock/stock-transfers-api.service';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext } from './print-context';

/**
 * The transfer note on A4 (owner, 2026-10-12): the paper that travels with the goods from the
 * branch that dispatched them to the one that receives them.
 *
 * <p>Number, dispatch date and the two branches at the head; every line with what left, what
 * arrived, what was declared lost and what is still in transit; the steps in order; two signature
 * boxes. No price and no cost: the driver and the receiving clerk read quantities. The state is
 * printed in words, so a note printed at dispatch never passes for proof of receipt.</p>
 *
 * <p>A shortfall (owner, 2026-10-13): the lines the destination reported short, with what was
 * reported, confirmed as lost, rejected and still waiting for an answer — and "shortfall reported"
 * beside the state while anything waits. Who reported, confirmed or rejected, when and why, is in
 * the steps.</p>
 */
@Component({
  selector: 'app-print-stock-transfer',
  standalone: true,
  imports: [PrintDocumentComponent],
  template: `
    @if (ctx(); as c) {
      <app-print-sheet
        [ctx]="c"
        [kind]="null"
        [title]="c.t('print.transfer.title')"
        titleKey="print.transfer.title"
        [number]="transfer().number"
        [meta]="meta()"
        [thanks]="false"
      >
        <div class="print-parties">
          <div class="print-block">
            <span class="print-block__title">{{ c.t('print.transfer.from') }}</span>
            <div class="print-line--lead">{{ branchName(transfer().fromBranchId, transfer().fromBranchName) }}</div>
            <div>
              <span class="print-line__label">{{ c.t('print.transfer.dispatchedBy') }}</span>
              <bdi>{{ transfer().dispatchedByName || '—' }}</bdi>
            </div>
          </div>
          <div class="print-block">
            <span class="print-block__title">{{ c.t('print.transfer.to') }}</span>
            <div class="print-line--lead">{{ branchName(transfer().toBranchId, transfer().toBranchName) }}</div>
          </div>
        </div>

        <p class="print-sentence print-sentence--strong">
          <span class="print-line__label">{{ c.t('print.transfer.status') }}</span>&ngsp;{{ c.t('transfer.status.' + transfer().status) }}
          @if (transfer().qtyShortPending > 0) {
            — {{ c.t('transfer.shortfall.tag') }}
          }
        </p>
        @if (transfer().note) {
          <p class="print-sentence"><span class="print-line__label">{{ c.t('transfer.col.note') }}</span>&ngsp;<bdi>{{ transfer().note }}</bdi></p>
        }

        <table class="print-table">
          <thead>
            <tr>
              <th>{{ c.t('backoffice.products.sku') }}</th>
              <th>{{ c.t('receipt.item') }}</th>
              <th class="num">{{ c.t('transfer.col.dispatched') }}</th>
              <th class="num">{{ c.t('transfer.col.received') }}</th>
              <th class="num">{{ c.t('transfer.col.lost') }}</th>
              <th class="num">{{ c.t('transfer.col.inTransit') }}</th>
            </tr>
          </thead>
          <tbody>
            @for (line of transfer().lines; track line.id) {
              <tr>
                <td class="mono">{{ line.sku }}</td>
                <td>{{ c.name(line) }}</td>
                <td class="num mono">{{ c.qty(line.qtyDispatched) }}</td>
                <td class="num mono">{{ c.qty(line.qtyReceived) }}</td>
                <td class="num mono">{{ c.qty(line.qtyLost) }}</td>
                <td class="num mono">{{ c.qty(line.inTransit) }}</td>
              </tr>
            }
          </tbody>
        </table>

        @if (shortfallLines().length) {
          <p class="print-sentence print-sentence--strong">{{ c.t('print.transfer.shortfalls') }}</p>
          <table class="print-table">
            <thead>
              <tr>
                <th>{{ c.t('backoffice.products.sku') }}</th>
                <th>{{ c.t('receipt.item') }}</th>
                <th class="num">{{ c.t('transfer.shortfall.col.reported') }}</th>
                <th class="num">{{ c.t('transfer.shortfall.col.confirmed') }}</th>
                <th class="num">{{ c.t('transfer.shortfall.col.rejected') }}</th>
                <th class="num">{{ c.t('transfer.shortfall.col.pending') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (line of shortfallLines(); track line.id) {
                <tr>
                  <td class="mono">{{ line.sku }}</td>
                  <td>{{ c.name(line) }}</td>
                  <td class="num mono">{{ c.qty(line.qtyShortReported) }}</td>
                  <td class="num mono">{{ c.qty(line.qtyShortConfirmed) }}</td>
                  <td class="num mono">{{ c.qty(line.qtyShortRejected) }}</td>
                  <td class="num mono">{{ c.qty(line.qtyShortPending) }}</td>
                </tr>
              }
            </tbody>
          </table>
        }

        @if (transfer().events.length) {
          <p class="print-sentence print-sentence--strong">{{ c.t('print.transfer.events') }}</p>
          <ul class="print-meta">
            @for (e of transfer().events; track e.id) {
              <li>
                <span class="print-line__label">{{ c.day(e.atUtc) }}</span>
                <span>{{ c.t('transfer.event.' + e.kind) }} — <bdi>{{ e.userName || '—' }}</bdi>@if (e.reason) { : <bdi>{{ e.reason }}</bdi> }</span>
              </li>
            }
          </ul>
        }

        <div class="print-signatures">
          <div class="print-signature">{{ c.t('print.transfer.sign.dispatchedBy') }}</div>
          <div class="print-signature">{{ c.t('print.transfer.sign.receivedBy') }}</div>
        </div>
      </app-print-sheet>
    }
  `,
})
export class PrintStockTransferComponent {
  readonly ctx = input.required<PrintContext>();
  readonly transfer = input.required<StockTransfer>();

  readonly meta = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    return [{ label: c.t('print.transfer.dispatchedAt'), value: c.day(this.transfer().dispatchedAtUtc) }];
  });

  /** The lines with anything ever reported short — answered or still waiting. */
  readonly shortfallLines = computed(() => this.transfer().lines.filter((l) => l.qtyShortReported > 0));

  /** The branch in the document language when the session knows it; the server's name otherwise. */
  branchName(id: string, fallback: string): string {
    const branch = this.ctx().branches.find((b) => b.id === id);
    return branch ? this.ctx().name(branch) : fallback;
  }
}
