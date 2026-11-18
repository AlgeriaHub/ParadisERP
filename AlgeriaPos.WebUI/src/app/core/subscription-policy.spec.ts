import { canOperate } from './subscription-policy';
import { SubscriptionAccess } from './models';

describe('offline subscription access', () => {
  const lease: SubscriptionAccess = {
    plan: 'Standard', status: 'Active', canOperate: true,
    accessUntilUtc: '2026-10-08T00:00:00Z', checkedAtUtc: '2026-10-01T00:00:00Z',
    maxUsers: 5, maxBranches: 2, maxDevices: 3,
  };
  it('stops new work exactly at the cached grace deadline', () => {
    expect(canOperate(lease, Date.parse(lease.accessUntilUtc!) - 1)).toBeTrue();
    expect(canOperate(lease, Date.parse(lease.accessUntilUtc!))).toBeFalse();
  });
  it('honors suspension even with time remaining', () => {
    expect(canOperate({ ...lease, status: 'Suspended', canOperate: false }, 0)).toBeFalse();
  });
  it('rejects missing or invalid expiry for subscribed plans', () => {
    expect(canOperate({ ...lease, accessUntilUtc: null })).toBeFalse();
    expect(canOperate({ ...lease, accessUntilUtc: 'invalid' })).toBeFalse();
  });
  it('preserves legacy installations before their first upgraded pull', () => {
    expect(canOperate(undefined)).toBeTrue();
  });
});
