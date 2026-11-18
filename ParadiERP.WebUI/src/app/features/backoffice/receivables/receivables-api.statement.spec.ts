import { TestBed } from '@angular/core/testing';
import { ApiService } from '../../../core/api.service';
import { ReceivablesApi } from './receivables-api.service';

describe('ReceivablesApi.statement query parameters', () => {
  let api: ReceivablesApi;
  let requestedUrl: string | null = null;
  const fakeApiService = {
    getJson: jasmine.createSpy('getJson').and.callFake((url: string) => {
      requestedUrl = url;
      return Promise.resolve({
        customerId: 'c1',
        customerCode: 'C001',
        customerName: 'Customer 1',
        from: '2026-01-01T00:00:00Z',
        to: '2026-02-01T00:00:00Z',
        openingBalance: 0,
        closingBalance: 0,
        totalDebit: 0,
        totalCredit: 0,
        rows: [],
        pendingNotes: 0,
        totalOwed: 0,
        recordedBalance: 0,
        balanceDifference: 0,
        creditLimit: 0,
        invoicedOutstanding: 0,
        uninvoicedBalance: 0,
        filter: 'all',
      });
    }),
  };

  beforeEach(() => {
    requestedUrl = null;
    fakeApiService.getJson.calls.reset();
    TestBed.configureTestingModule({
      providers: [
        ReceivablesApi,
        { provide: ApiService, useValue: fakeApiService },
      ],
    });
    api = TestBed.inject(ReceivablesApi);
  });

  it('omits kind when omitted or null', async () => {
    await api.statement('c1', null, null);
    expect(fakeApiService.getJson).toHaveBeenCalled();
    expect(requestedUrl).toBe('/api/receivables/statement?customerId=c1');
    expect(requestedUrl).not.toContain('kind=');
  });

  it('omits kind when kind is "all"', async () => {
    await api.statement('c1', null, null, 'all');
    expect(fakeApiService.getJson).toHaveBeenCalled();
    expect(requestedUrl).toBe('/api/receivables/statement?customerId=c1');
    expect(requestedUrl).not.toContain('kind=');
  });

  it('adds kind=sales when kind is "sales"', async () => {
    await api.statement('c1', null, null, 'sales');
    expect(fakeApiService.getJson).toHaveBeenCalled();
    expect(requestedUrl).toBe('/api/receivables/statement?customerId=c1&kind=sales');
    expect(requestedUrl).toContain('kind=sales');
  });

  it('adds kind=rentals when kind is "rentals"', async () => {
    await api.statement('c1', null, null, 'rentals');
    expect(fakeApiService.getJson).toHaveBeenCalled();
    expect(requestedUrl).toBe('/api/receivables/statement?customerId=c1&kind=rentals');
    expect(requestedUrl).toContain('kind=rentals');
  });
});
