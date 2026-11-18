import { PosUser } from './models';

/** The server capability that decides who may create a customer (Domain/Capabilities.cs). */
export const CUSTOMERS_CREATE = 'customers.create';

/**
 * Whether this user may create a customer from the screen they are on (FR-1, the owner,
 * 2026-10-03: "if the customer is new, create them from the screen itself").
 *
 * - **The server decides, the screen reads.** The login reports `customers.create` from the same
 *   decision `POST /api/catalog/customers` enforces; no role name is read here. A session signed
 *   in before the login carried capabilities has none, which reads as "not allowed" until the
 *   next online sign-in — never as an error.
 * - **Online only.** A customer is a server record the tills replicate; there is no offline
 *   creation and no queue for one, so with no connection the action is not offered at all.
 */
export function canCreateCustomer(user: Pick<PosUser, 'capabilities'> | null | undefined, online: boolean): boolean {
  return online && (user?.capabilities?.includes(CUSTOMERS_CREATE) ?? false);
}

/**
 * The till's half after a customer was created on the server: pull the change into the device's
 * copy, then pick the customer for the sale — only if the pull brought it, so the till never
 * selects a customer it cannot show. Nothing else in the sale is touched.
 */
export async function selectCreatedCustomer(
  id: string,
  refresh: () => Promise<unknown>,
  has: (id: string) => boolean,
  select: (id: string) => void,
): Promise<boolean> {
  await refresh();
  if (!has(id)) return false;
  select(id);
  return true;
}
