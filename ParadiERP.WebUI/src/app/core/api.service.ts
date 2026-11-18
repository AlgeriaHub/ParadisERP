import { Injectable } from '@angular/core';
import { getMeta } from './db';
import { LoginResponse, PullResponse, PushResponse, Sale } from './models';

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/**
 * Thin fetch wrapper. Deliberately not HttpClient: every call here is one the sync engine
 * makes, and it needs plain promises plus a hard timeout so a half-open connection on a
 * weak line cannot hang the register.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  /** Typed passthrough used by the back-office admin screens (online-only CRUD). */
  getJson<T>(path: string): Promise<T> {
    return this.request<T>(path, { method: 'GET' });
  }

  postJson<T>(path: string, body: unknown): Promise<T> {
    return this.request<T>(path, { method: 'POST', body: JSON.stringify(body) });
  }

  putJson<T>(path: string, body: unknown): Promise<T> {
    return this.request<T>(path, { method: 'PUT', body: JSON.stringify(body) });
  }

  deleteJson<T>(path: string): Promise<T> {
    return this.request<T>(path, { method: 'DELETE' });
  }

  /** A file up as multipart; the browser writes the boundary, so no content type is set here. */
  postForm<T>(path: string, form: FormData, timeoutMs = 60_000): Promise<T> {
    return this.request<T>(path, { method: 'POST', body: form }, timeoutMs);
  }

  /** A file down, with the same token as every call — an `<img>` cannot send it (SecureImageService). */
  getBlob(path: string, timeoutMs = 30_000): Promise<Blob> {
    return this.send(path, { method: 'GET' }, timeoutMs, (response) => response.blob());
  }

  private request<T>(path: string, init: RequestInit = {}, timeoutMs = 30_000): Promise<T> {
    return this.send(path, init, timeoutMs, async (response) =>
      response.status === 204 ? (undefined as T) : ((await response.json()) as T));
  }

  private async send<T>(
    path: string,
    init: RequestInit,
    timeoutMs: number,
    read: (response: Response) => Promise<T>,
  ): Promise<T> {
    const meta = await getMeta();
    const base = (meta.apiBaseUrl || '').replace(/\/+$/, '');

    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    const headers: Record<string, string> = {
      ...(init.body instanceof FormData ? {} : { 'Content-Type': 'application/json' }),
      ...((init.headers as Record<string, string>) ?? {}),
    };
    if (meta.token) headers['Authorization'] = `Bearer ${meta.token}`;

    try {
      const response = await fetch(`${base}${path}`, {
        ...init,
        headers,
        cache: 'no-store',
        signal: controller.signal,
      });

      if (!response.ok) {
        const body = await response.text().catch(() => '');
        throw new ApiError(body || `${response.status} ${response.statusText}`, response.status);
      }
      return await read(response);
    } catch (error) {
      if (error instanceof ApiError) throw error;
      if ((error as Error)?.name === 'AbortError') throw new ApiError('Request timed out.', 0);
      throw new ApiError((error as Error)?.message ?? 'Network error', 0);
    } finally {
      clearTimeout(timer);
    }
  }

  /**
   * The sign-in. With two-step login on, the server answers 401 `two_factor_required` until the
   * second step arrives: the authenticator's code (or a recovery code), or a "trust this computer"
   * token an earlier sign-in here answered with.
   */
  login(
    username: string,
    password: string,
    deviceCode: string,
    second: { twoFactorCode?: string; trustToken?: string; rememberDevice?: boolean } = {},
  ): Promise<LoginResponse> {
    return this.request<LoginResponse>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ username, password, deviceCode, ...second }),
    });
  }

  /**
   * The signed-in user's own new password (owner, 2026-10-05). Sent with the session's token like
   * any call; the server answers with a fresh session in the sign-in's shape and revokes this one.
   */
  changePassword(currentPassword: string, newPassword: string): Promise<LoginResponse> {
    return this.request<LoginResponse>('/api/auth/password', {
      method: 'POST',
      body: JSON.stringify({ currentPassword, newPassword }),
    });
  }

  pull(since: number, deviceCode: string): Promise<PullResponse> {
    const params = new URLSearchParams({ since: String(since), deviceCode });
    return this.request<PullResponse>(`/api/sync/pull?${params.toString()}`, { method: 'GET' }, 60_000);
  }

  push(deviceCode: string, sales: Sale[]): Promise<PushResponse> {
    const payload = {
      deviceCode,
      sales: sales.map((sale) => ({
        id: sale.id,
        reference: sale.reference,
        localSeq: sale.localSeq,
        soldAtUtc: sale.soldAtUtc,
        customerId: sale.customerId,
        cashierUserId: sale.cashierUserId,
        cashierName: sale.cashierName,
        subTotalHt: sale.subTotalHt,
        taxTotal: sale.taxTotal,
        discountTotal: sale.discountTotal,
        grandTotalTtc: sale.grandTotalTtc,
        roundingAdjustment: sale.roundingAdjustment ?? 0,
        paidAmount: sale.paidAmount,
        changeAmount: sale.changeAmount,
        paymentMethod: sale.paymentMethod,
        // Absent on sales queued before the choice existed; the server reads that as taxed.
        taxMode: sale.taxMode ?? 'Taxed',
        status: sale.status,
        refundOfSaleId: sale.refundOfSaleId,
        note: sale.note,
        lines: sale.lines,
        payments: sale.payments,
      })),
    };
    return this.request<PushResponse>('/api/sync/push', {
      method: 'POST',
      body: JSON.stringify(payload),
    }, 60_000);
  }
}
