import type { FiscalIdDefinition, Lang } from './models';

/** A record that can speak for a paper: a branch, or the company settings. */
export interface IssuerSource {
  nameAr?: string | null;
  nameFr?: string | null;
  nameEn?: string | null;
  address?: string | null;
  addressAr?: string | null;
  addressFr?: string | null;
  addressEn?: string | null;
  phone?: string | null;
  fiscalIds?: Record<string, string> | null;
}

export interface IssuerBranch extends IssuerSource {
  id: string;
  isMain: boolean;
  isWarehouse?: boolean;
}

/** Who a paper is from, as its head prints it. */
export interface DocumentIssuer {
  name: string;
  address: string;
  phone: string;
  /** `label` is the identifier's code (NIF, RC…); only those with a value. */
  fiscalIds: { label: string; value: string }[];
}

/**
 * The identity a paper prints (owner, 2026-10-20: "all files printed from one branch must contain
 * its information; where it has none, the main branch's"): the branch it was raised at, then the
 * main trading branch (never the warehouse), then the company settings. Each field is resolved on
 * its own, each fiscal identifier by key, and whitespace counts as missing — a branch that filled
 * in only its register still prints the main branch's NIF.
 *
 * <p>`fiscalIds` lists the identifiers to resolve, in order: every one for an A4 paper, the
 * `showOnReceipt` ones for the 80 mm slip.</p>
 */
export function documentIssuer(
  branchId: string | null | undefined,
  branches: readonly IssuerBranch[],
  store: IssuerSource | null | undefined,
  lang: Lang,
  fiscalIds: readonly Pick<FiscalIdDefinition, 'key' | 'code'>[] = [],
): DocumentIssuer {
  const sources = [
    branchId ? branches.find((b) => b.id === branchId) : undefined,
    branches.find((b) => b.isMain && !b.isWarehouse),
    store ?? undefined,
  ];
  const first = (values: (string | null | undefined)[]) => values.find((v) => v?.trim())?.trim() ?? '';
  return {
    name: first(sources.map((s) => s && first([
      lang === 'ar' ? s.nameAr : lang === 'en' ? s.nameEn : s.nameFr,
      s.nameFr, s.nameEn, s.nameAr,
    ]))),
    address: first(sources.map((s) => s && first([
      lang === 'ar' ? s.addressAr : lang === 'en' ? s.addressEn : s.addressFr,
      s.addressFr, s.address, s.addressEn, s.addressAr,
    ]))),
    phone: first(sources.map((s) => s?.phone)),
    fiscalIds: fiscalIds
      .map((id) => ({ label: id.code, value: first(sources.map((s) => s?.fiscalIds?.[id.key])) }))
      .filter((line) => line.value),
  };
}
