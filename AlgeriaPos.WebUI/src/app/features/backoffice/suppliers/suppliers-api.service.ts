import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import type { OpeningBalanceRequest, OpeningCorrectionRequest } from '../../../core/admin.service';

export interface SupplierDto {
  id: string;
  code: string;
  name: string;
  phone: string | null;
  address: string | null;
  fiscalIds: Record<string, string>;
  note: string | null;
  balance: number;
  openingBalance: number;
  openingBalanceOn: string | null;
  openingBalanceSetAtUtc: string | null;
  openingBalanceSetBy: string | null;
}

export interface UpsertSupplierRequest {
  name: string;
  phone: string | null;
  address: string | null;
  fiscalIds: Record<string, string> | null;
  note: string | null;
  openingBalance?: OpeningBalanceRequest | null;
}

export interface SupplierOpeningCorrection {
  id: string;
  number: string;
  supplierId: string;
  amount: number;
  effectiveOn: string;
  reason: string;
  createdBy: string | null;
  createdAtUtc: string;
}

export interface SupplierStatementRow {
  date: string;
  sourceType: string;
  sourceId: string;
  documentNumber: string | null;
  description: string | null;
  isReversal: boolean;
  debit: number;
  credit: number;
  balance: number;
}

export interface SupplierStatement {
  supplierId: string;
  supplierCode: string;
  supplierName: string;
  from: string;
  to: string;
  openingBalance: number;
  closingBalance: number;
  totalDebit: number;
  totalCredit: number;
  rows: SupplierStatementRow[];
  recordedBalance: number;
  balanceDifference: number;
}

function query(params: URLSearchParams): string {
  const text = params.toString();
  return text ? `?${text}` : '';
}

@Injectable({ providedIn: 'root' })
export class SuppliersApiService {
  private readonly api = inject(ApiService);

  list(): Promise<SupplierDto[]> {
    return this.api.getJson<SupplierDto[]>('/api/suppliers');
  }

  create(req: UpsertSupplierRequest): Promise<SupplierDto> {
    return this.api.postJson<SupplierDto>('/api/suppliers', req);
  }

  update(id: string, req: UpsertSupplierRequest): Promise<SupplierDto> {
    return this.api.putJson<SupplierDto>(`/api/suppliers/${id}`, req);
  }

  remove(id: string): Promise<void> {
    return this.api.deleteJson<void>(`/api/suppliers/${id}`);
  }

  setOpeningBalance(id: string, req: OpeningBalanceRequest): Promise<SupplierDto> {
    return this.api.postJson<SupplierDto>(`/api/suppliers/${id}/opening-balance`, req);
  }

  correctOpeningBalance(id: string, req: OpeningCorrectionRequest): Promise<SupplierOpeningCorrection> {
    return this.api.postJson<SupplierOpeningCorrection>(`/api/suppliers/${id}/opening-corrections`, req);
  }

  listOpeningCorrections(id: string): Promise<SupplierOpeningCorrection[]> {
    return this.api.getJson<SupplierOpeningCorrection[]>(`/api/suppliers/${id}/opening-corrections`);
  }

  statement(id: string, fromIso?: string | null, toIso?: string | null): Promise<SupplierStatement> {
    const params = new URLSearchParams();
    if (fromIso) params.set('from', fromIso);
    if (toIso) params.set('to', toIso);
    return this.api.getJson<SupplierStatement>(`/api/suppliers/${id}/statement${query(params)}`);
  }
}
