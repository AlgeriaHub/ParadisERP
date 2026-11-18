import { TestBed } from '@angular/core/testing';
import { ApiService } from '../../../core/api.service';
import {
  ItemStatusApiService,
  ItemStatusQuery,
  itemStatusQueryString,
} from './item-status-api.service';

describe('ItemStatusApiService and query string builder', () => {
  let service: ItemStatusApiService;
  let apiSpy: jasmine.SpyObj<ApiService>;

  beforeEach(() => {
    apiSpy = jasmine.createSpyObj<ApiService>('ApiService', ['getJson', 'getBlob']);
    TestBed.configureTestingModule({
      providers: [
        ItemStatusApiService,
        { provide: ApiService, useValue: apiSpy },
      ],
    });
    service = TestBed.inject(ItemStatusApiService);
  });

  describe('itemStatusQueryString', () => {
    it('omits empty, null and undefined values', () => {
      const q: ItemStatusQuery = {
        branchId: null,
        q: '',
        productIds: undefined,
        itemType: null,
        status: null,
        customerId: '',
        contractId: null,
        includeInactive: false,
      };
      expect(itemStatusQueryString(q)).toBe('');
    });

    it('joins productIds when passed as an array and trims whitespace', () => {
      const q: ItemStatusQuery = {
        productIds: ['prod-1', 'prod-2', 'prod-3'],
      };
      expect(itemStatusQueryString(q)).toBe('productIds=prod-1%2Cprod-2%2Cprod-3');
    });

    it('keeps productIds when passed as a comma-separated string', () => {
      const q: ItemStatusQuery = {
        productIds: 'prod-1,prod-2',
      };
      expect(itemStatusQueryString(q)).toBe('productIds=prod-1%2Cprod-2');
    });

    it('sends the in-transit filter as status=transit', () => {
      expect(new URLSearchParams(itemStatusQueryString({ status: 'transit' })).get('status')).toBe('transit');
    });

    it('serializes all active filters and extra parameters', () => {
      const q: ItemStatusQuery = {
        branchId: 'b-1',
        q: 'drill',
        itemType: 'rental',
        status: 'available',
        customerId: 'c-1',
        contractId: 'cnt-1',
        includeInactive: true,
      };
      const qs = itemStatusQueryString(q, { from: '2026-01-01', to: '2026-01-31', lang: 'fr' });
      const params = new URLSearchParams(qs);
      expect(params.get('branchId')).toBe('b-1');
      expect(params.get('q')).toBe('drill');
      expect(params.get('itemType')).toBe('rental');
      expect(params.get('status')).toBe('available');
      expect(params.get('customerId')).toBe('c-1');
      expect(params.get('contractId')).toBe('cnt-1');
      expect(params.get('includeInactive')).toBe('true');
      expect(params.get('from')).toBe('2026-01-01');
      expect(params.get('to')).toBe('2026-01-31');
      expect(params.get('lang')).toBe('fr');
    });
  });

  describe('API calls', () => {
    it('calls summary with query string', async () => {
      apiSpy.getJson.and.resolveTo({} as any);
      await service.summary({ q: 'hammer' });
      expect(apiSpy.getJson).toHaveBeenCalledWith('/api/reports/inventory/items?q=hammer');
    });

    it('calls detail with product id and query parameters', async () => {
      apiSpy.getJson.and.resolveTo({} as any);
      await service.detail('p-123', { branchId: 'b-1' }, '2026-01-01', '2026-01-31');
      expect(apiSpy.getJson).toHaveBeenCalledWith(
        '/api/reports/inventory/items/p-123?branchId=b-1&from=2026-01-01&to=2026-01-31',
      );
    });

    it('calls exportSummary with lang', async () => {
      apiSpy.getBlob.and.resolveTo(new Blob());
      await service.exportSummary({ branchId: 'b-1' }, 'ar');
      expect(apiSpy.getBlob).toHaveBeenCalledWith(
        '/api/reports/inventory/items/export?branchId=b-1&lang=ar',
      );
    });

    it('calls exportDetail with dates and lang', async () => {
      apiSpy.getBlob.and.resolveTo(new Blob());
      await service.exportDetail('p-123', {}, '2026-01-01', '2026-01-31', 'en');
      expect(apiSpy.getBlob).toHaveBeenCalledWith(
        '/api/reports/inventory/items/p-123/export?from=2026-01-01&to=2026-01-31&lang=en',
      );
    });
  });
});
