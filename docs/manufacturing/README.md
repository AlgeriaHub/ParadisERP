# Manufacturing — where it stands

*As of 2026-11-02. Production runs `474178b` (deployed 2026-11-02 12:43 UTC); A0, costing Phase A, B1 and B2 went live with
`d9e1edd` at 01:59 UTC that day. `Manufacturing:StockOperations` and `Manufacturing:Costing` are still off in
production, so the costing screens and the stock operations do not run there yet.*

Each part has its own record; this page only says where each one is.

## Built

| Part | What it does | State | Record |
|---|---|---|---|
| Phase 1, configuration | Production lines, product roles, units and conversions, versioned bills of materials, stage templates, job roles, per-user manufacturing permissions | Live since 2026-10-26 | [phase-1.md](phase-1.md) |
| Phase 2, production orders | Orders, the daily production record, issue, return, scrap, consumption, release and expenses | Live since 2026-10-26, but `Manufacturing:StockOperations` is not set in production: every operational write answers 409 `operations_not_enabled`; reads and reports work | [phase-2.md](phase-2.md) |
| Step A0 | The hours ceiling for labour bookings follows payroll's month rule; the expenses linked to a voucher never exceed its amount | Live since 2026-11-02 (on `main` since 2026-10-28, `42d065b`) | [phase-2.md](phase-2.md) §8 |
| Costing Phase A | Read-only cost previews per order (estimated, to date, final) and the work-in-progress, labour-cost and material-flow reports | Live since 2026-11-02 (on `main` since 2026-10-28, `1a0caa6`); behind `Manufacturing:Costing` (off) | [costing-phase-a.md](costing-phase-a.md) |
| Costing B1 | One immutable cost sheet per order, made on request after completion; later costs are appended and never change it; every sheet stays Provisional | Live since 2026-11-02 (on `main` since 2026-10-29, `2172f11`); its three migrations apply at the next deploy | [Work status](../03-work-status.md), "Manufacturing costing B1" sections |
| Costing B2 | Read-only reconciliation: Manufacturing › Costing (six tabs), the order's "Frozen cost" panel, Excel export | Live since 2026-11-02 (on `main` since 2026-10-29, `3ee4450`); behind `Manufacturing:Costing` (off) | [costing-b2.md](costing-b2.md) |
| Weighted-average shadow (P0.4a + P0.4b) | Every stock movement and sale line records the cost it was valued at; Reports › Stock valuation (trial) shows stock at a moving average beside the cost price. The accountant's open choices (one average per company or per location, a refund's cost, decimals) are admin settings | Live since 2026-11-09 (`cc4260f`; on `main` since 2026-11-08, `d1e9ec8`) | [valuation-shadow.md](valuation-shadow.md) |
| Phase C1, the open choices as settings | The accountant's open costing choices (model, overhead, release rule, employer burden, period-end work in progress, late costs, variances) as admin settings the cost previews follow; the account map, seeded empty; Manufacturing › Costing settings | Live since 2026-11-09 (`cc4260f`; built 2026-11-08, `1415fe9`) | [phase-c.md](phase-c.md) |
| Phase C2, the practice ledger | One month of the entries the chosen model would make, computed on read and posted nowhere, with the R8 checks; Manufacturing › Practice ledger (pay-rate access) | Live since 2026-11-09 (`7c715ec`) | [phase-c.md](phase-c.md) |
| Accountant's panel and Phase D | The last costing questions as settings, the stock-operations switch and the accountant's confirmation in Costing settings; a closed month posted to the ledger from the practice ledger, and reversed | Live since 2026-11-09 (`7c715ec`) | [phase-c.md](phase-c.md) |

Nothing in manufacturing writes `Product.CostPrice`, payroll or the ledger.

## Not built

- The switch to the weighted average (D9/D43) and P0.4c in the ledger (refunds at their sale's cost). The shadow
  that compares the two methods is built (P0.4a/b, above).
- Phase C, a shadow accounting simulation that never touches the ledger. P0.4a and P0.4b are now built; it
  still needs the accountant's answers, or those answers as admin settings (owner, 2026-11-08).
- Phase D, live manufacturing accounting: work in progress, finished goods and variances in the ledger.
- Overhead, recording scrap's recovery value, and correcting an approved daily record.

The plan for all of it is [costing-plan.md](costing-plan.md); each phase waits for its own approval.

## Waiting on

- **The accountant.**
  - The accounting treatment of production, before `Manufacturing:StockOperations` can be set in production
    ([phase-2.md](phase-2.md), decision 1).
  - The costing checklist C-01 to C-18 ([costing-accountant-signoff.md](costing-accountant-signoff.md)); in Arabic
    for the accountant, updated with the owner's decisions of 2026-11-05:
    [costing-accountant-signoff.ar.md](costing-accountant-signoff.ar.md) and its `.ar.pdf`. Phase C starts when
    C-01 to C-17 are answered. The costing screens are on in production from the next deploy (owner, 2026-11-05;
    read-only, nothing posts).
  - B2's open decisions AD-1 to AD-12 ([costing-b2.md](costing-b2.md) §7).
- **The owner.**
  - Phase A's complete privacy remediation: order-level labour money only with rate access, or `costs.view`
    treated as payroll-sensitive ([costing-b2.md](costing-b2.md) §8). The minimal one was applied on
    2026-11-02 (`27406f2`, live).
  - Each next phase's approval.

Done on 2026-11-02 at the owner's go-ahead: the minimal Phase A privacy remediation (`27406f2`) and the Phase 2
drive's race expectations, which now follow the recorded winner (`97d9bd0`).
