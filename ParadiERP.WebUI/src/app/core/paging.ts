/**
 * One page of a list the server pages (`PageResult<T>` in ParadiERP.Application/Contracts).
 * `total` counts every row the filters keep; `skip` is the offset the server used (a page past
 * the end comes back as the last page); `summary` is the list's figures over all its rows.
 */
export interface PageResult<T, S = unknown> {
  items: T[];
  total: number;
  skip: number;
  take: number;
  summary?: S | null;
}

/**
 * `?skip=…&take=…&…` for a page request. A filter left at "all", empty or null is not sent, so the
 * server applies only the filters that are set.
 */
export function pageQueryString(params: Record<string, string | number | boolean | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [name, value] of Object.entries(params)) {
    if (value === null || value === undefined || value === '' || value === 'all') continue;
    search.set(name, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}

export interface PageQuery<F> {
  skip: number;
  take: number;
  filters: F;
}
