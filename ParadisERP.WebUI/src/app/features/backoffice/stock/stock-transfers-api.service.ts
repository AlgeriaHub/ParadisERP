import { Injectable } from '@angular/core';
import { ApiService } from '../../../core/api.service';

/** `StockTransferStatus` on the server. */
export type StockTransferStatus = 'Dispatched' | 'PartlyReceived' | 'Received' | 'Cancelled';
/** `StockTransferEventKind` on the server. */
export type StockTransferEventKind =
  | 'Dispatch'
  | 'Receive'
  | 'Loss'
  | 'Cancel'
  | 'ShortfallReported'
  | 'ShortfallConfirmed'
  | 'ShortfallRejected';

export interface StockTransferLine {
  id: string;
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  qtyDispatched: number;
  qtyReceived: number;
  /** Declared lost, or reported short and confirmed (`qtyShortConfirmed` is that part). */
  qtyLost: number;
  qtyReturned: number;
  /** Still in transit, including what is reported short and not yet answered. */
  inTransit: number;
  unitCost: number;
  /** Reported short by the destination and waiting for the source's answer (part of `inTransit`). */
  qtyShortPending: number;
  /** Everything reported short on this line, over every receipt. */
  qtyShortReported: number;
  /** Of that, confirmed and written off. */
  qtyShortConfirmed: number;
  /** Of that, rejected by the source: plain in transit again. */
  qtyShortRejected: number;
}

export interface StockTransferEvent {
  id: string;
  kind: StockTransferEventKind;
  atUtc: string;
  userName: string | null;
  reason: string | null;
}

export interface StockTransfer {
  id: string;
  number: string;
  status: StockTransferStatus;
  fromBranchId: string;
  fromBranchName: string;
  toBranchId: string;
  toBranchName: string;
  note: string | null;
  dispatchedAtUtc: string;
  dispatchedByName: string | null;
  closedAtUtc: string | null;
  qtyDispatched: number;
  inTransit: number;
  lines: StockTransferLine[];
  events: StockTransferEvent[];
  /** Reported short and waiting for an answer, over every line. */
  qtyShortPending: number;
  /**
   * Whether the signed-in user may confirm or reject that shortfall — decided by the server with the
   * same rule its routes enforce (the source branch, or a write-off holder; never the destination).
   */
  canDecideShortfall: boolean;
}

export interface StockTransferFilter {
  branchId?: string | null;
  /** A status, `open` (dispatched or partly received) or `shortfall` (a reported shortfall not yet answered). */
  status?: 'Dispatched' | 'PartlyReceived' | 'Received' | 'Cancelled' | 'open' | 'shortfall' | null;
  productId?: string | null;
  from?: string | null;
  to?: string | null;
}

export interface DispatchTransferLine {
  productId: string;
  qty: number;
}

export interface DispatchTransferRequest {
  fromBranchId: string;
  toBranchId: string;
  note?: string | null;
  lines: DispatchTransferLine[];
}

export interface ReceiveTransferLine {
  lineId: string;
  /** What arrived. */
  qty: number;
  /** What the destination reports short of what is left; stays in transit until the source answers. */
  short?: number;
}

export interface ReceiveTransferRequest {
  lines: ReceiveTransferLine[];
  note?: string | null;
  /** Required when any line reports something short. */
  shortReason?: string | null;
}

export interface ShortfallDecisionLine {
  lineId: string;
  qty: number;
}

/** The source's answer: the quantities shown as reported short, and why (required to reject). */
export interface ShortfallDecisionRequest {
  lines: ShortfallDecisionLine[];
  reason?: string | null;
}

export interface DeclareLostTransferLine {
  lineId: string;
  qty: number;
}

export interface DeclareLostTransferRequest {
  lines: DeclareLostTransferLine[];
  reason: string;
}

export interface CancelTransferRequest {
  reason?: string | null;
}

@Injectable({ providedIn: 'root' })
export class StockTransfersApi {
  constructor(private readonly api: ApiService) {}

  list(filter: StockTransferFilter = {}): Promise<StockTransfer[]> {
    const params = new URLSearchParams();
    if (filter.branchId) params.set('branchId', filter.branchId);
    if (filter.status) params.set('status', filter.status);
    if (filter.productId) params.set('productId', filter.productId);
    if (filter.from) params.set('from', filter.from);
    if (filter.to) params.set('to', filter.to);
    const query = params.toString();
    return this.api.getJson<StockTransfer[]>(`/api/catalog/stock/transfers${query ? '?' + query : ''}`);
  }

  get(id: string): Promise<StockTransfer> {
    return this.api.getJson<StockTransfer>(`/api/catalog/stock/transfers/${encodeURIComponent(id)}`);
  }

  dispatch(req: DispatchTransferRequest): Promise<StockTransfer> {
    return this.api.postJson<StockTransfer>('/api/catalog/stock/transfers', req);
  }

  receive(id: string, req: ReceiveTransferRequest): Promise<StockTransfer> {
    return this.api.postJson<StockTransfer>(`/api/catalog/stock/transfers/${encodeURIComponent(id)}/receive`, req);
  }

  declareLost(id: string, req: DeclareLostTransferRequest): Promise<StockTransfer> {
    return this.api.postJson<StockTransfer>(`/api/catalog/stock/transfers/${encodeURIComponent(id)}/lost`, req);
  }

  cancel(id: string, req: CancelTransferRequest): Promise<StockTransfer> {
    return this.api.postJson<StockTransfer>(`/api/catalog/stock/transfers/${encodeURIComponent(id)}/cancel`, req);
  }

  /** The source confirms a reported shortfall: written off as lost at the source. */
  confirmShortfall(id: string, req: ShortfallDecisionRequest): Promise<StockTransfer> {
    return this.api.postJson<StockTransfer>(`/api/catalog/stock/transfers/${encodeURIComponent(id)}/shortfall/confirm`, req);
  }

  /** The source rejects a reported shortfall: it stays in transit for the destination to receive. */
  rejectShortfall(id: string, req: ShortfallDecisionRequest): Promise<StockTransfer> {
    return this.api.postJson<StockTransfer>(`/api/catalog/stock/transfers/${encodeURIComponent(id)}/shortfall/reject`, req);
  }
}
