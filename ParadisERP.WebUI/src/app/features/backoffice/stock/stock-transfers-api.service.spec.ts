import { TestBed } from '@angular/core/testing';
import { ApiService } from '../../../core/api.service';
import {
  CancelTransferRequest,
  DeclareLostTransferRequest,
  DispatchTransferRequest,
  ReceiveTransferRequest,
  ShortfallDecisionRequest,
  StockTransfer,
  StockTransferFilter,
  StockTransfersApi,
} from './stock-transfers-api.service';

describe('StockTransfersApi', () => {
  let service: StockTransfersApi;
  let apiSpy: jasmine.SpyObj<ApiService>;

  beforeEach(() => {
    apiSpy = jasmine.createSpyObj<ApiService>('ApiService', ['getJson', 'postJson']);
    TestBed.configureTestingModule({
      providers: [
        StockTransfersApi,
        { provide: ApiService, useValue: apiSpy },
      ],
    });
    service = TestBed.inject(StockTransfersApi);
  });

  describe('list', () => {
    it('calls without query string when filter is empty', async () => {
      apiSpy.getJson.and.resolveTo([] as StockTransfer[]);
      await service.list();
      expect(apiSpy.getJson).toHaveBeenCalledWith('/api/catalog/stock/transfers');
    });

    it('serializes all filter properties into the query string', async () => {
      apiSpy.getJson.and.resolveTo([] as StockTransfer[]);
      const filter: StockTransferFilter = {
        branchId: 'b-source',
        status: 'shortfall',
        productId: 'p-1',
        from: '2026-10-01',
        to: '2026-10-12',
      };
      await service.list(filter);
      const url = apiSpy.getJson.calls.mostRecent().args[0] as string;
      expect(url).toContain('/api/catalog/stock/transfers?');
      const params = new URLSearchParams(url.split('?')[1]);
      expect(params.get('branchId')).toBe('b-source');
      expect(params.get('status')).toBe('shortfall');
      expect(params.get('productId')).toBe('p-1');
      expect(params.get('from')).toBe('2026-10-01');
      expect(params.get('to')).toBe('2026-10-12');
    });

    it('omits null and undefined filter values', async () => {
      apiSpy.getJson.and.resolveTo([] as StockTransfer[]);
      await service.list({ branchId: null, status: 'open', productId: undefined });
      const url = apiSpy.getJson.calls.mostRecent().args[0] as string;
      expect(url).toBe('/api/catalog/stock/transfers?status=open');
    });
  });

  describe('get', () => {
    it('encodes the id in the URL', async () => {
      apiSpy.getJson.and.resolveTo({ id: 't-1' } as StockTransfer);
      await service.get('transfer/123');
      expect(apiSpy.getJson).toHaveBeenCalledWith('/api/catalog/stock/transfers/transfer%2F123');
    });
  });

  describe('dispatch', () => {
    it('posts to the transfers root endpoint', async () => {
      const req: DispatchTransferRequest = {
        fromBranchId: 'b-1',
        toBranchId: 'b-2',
        note: 'urgent delivery',
        lines: [{ productId: 'p-1', qty: 5 }],
      };
      apiSpy.postJson.and.resolveTo({ id: 't-1' } as StockTransfer);
      await service.dispatch(req);
      expect(apiSpy.postJson).toHaveBeenCalledWith('/api/catalog/stock/transfers', req);
    });
  });

  describe('receive', () => {
    it('posts to the receive endpoint with quantities and shortReason', async () => {
      const req: ReceiveTransferRequest = {
        lines: [{ lineId: 'l-1', qty: 3, short: 1 }],
        note: 'damaged box',
        shortReason: '1 item broken on arrival',
      };
      apiSpy.postJson.and.resolveTo({ id: 't-1' } as StockTransfer);
      await service.receive('t-1', req);
      expect(apiSpy.postJson).toHaveBeenCalledWith('/api/catalog/stock/transfers/t-1/receive', req);
    });
  });

  describe('declareLost', () => {
    it('posts to the lost endpoint with reasons', async () => {
      const req: DeclareLostTransferRequest = {
        lines: [{ lineId: 'l-1', qty: 2 }],
        reason: 'fell off truck',
      };
      apiSpy.postJson.and.resolveTo({ id: 't-1' } as StockTransfer);
      await service.declareLost('t-1', req);
      expect(apiSpy.postJson).toHaveBeenCalledWith('/api/catalog/stock/transfers/t-1/lost', req);
    });
  });

  describe('cancel', () => {
    it('posts to the cancel endpoint with optional reason', async () => {
      const req: CancelTransferRequest = { reason: 'wrong destination' };
      apiSpy.postJson.and.resolveTo({ id: 't-1' } as StockTransfer);
      await service.cancel('t-1', req);
      expect(apiSpy.postJson).toHaveBeenCalledWith('/api/catalog/stock/transfers/t-1/cancel', req);
    });
  });

  describe('shortfall decisions', () => {
    it('posts confirmShortfall to /shortfall/confirm', async () => {
      const req: ShortfallDecisionRequest = {
        lines: [{ lineId: 'l-1', qty: 1 }],
        reason: 'confirmed with carrier',
      };
      apiSpy.postJson.and.resolveTo({ id: 't-1' } as StockTransfer);
      await service.confirmShortfall('t-1', req);
      expect(apiSpy.postJson).toHaveBeenCalledWith('/api/catalog/stock/transfers/t-1/shortfall/confirm', req);
    });

    it('posts rejectShortfall to /shortfall/reject', async () => {
      const req: ShortfallDecisionRequest = {
        lines: [{ lineId: 'l-1', qty: 1 }],
        reason: 'package was accounted for',
      };
      apiSpy.postJson.and.resolveTo({ id: 't-1' } as StockTransfer);
      await service.rejectShortfall('t-1', req);
      expect(apiSpy.postJson).toHaveBeenCalledWith('/api/catalog/stock/transfers/t-1/shortfall/reject', req);
    });
  });
});
