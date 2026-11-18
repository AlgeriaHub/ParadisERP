# Manufacturing — Phase 1: configuration

Branch `feat/manufacturing-phase1`, from `origin/main` at `53a712e` (2026-10-25). It builds on the
Manufacturing & Maintenance audit, Revision 2 (approved in principle,
https://claude.ai/artifact/Ny3Rhfe1J9EnyGm7xWbpDj), and the owner's Phase 1 brief of 2026-10-25.

Phase 1 is **configuration only**. It adds production lines, product roles, units and conversions,
versioned bills of materials (BOMs), stage templates, job roles and per-user manufacturing permissions.
Nothing in it creates or consumes a production order. It does not move or value stock, posts nothing to
the ledger, and touches neither payroll nor `Product.CostPrice`.

## 1. What was built

| Area | What it does |
|---|---|
| Production lines | A line record over a warehouse-type location of its own (`Branch` with `IsWarehouse`, never the main warehouse, never another line's), with code, names (ar/fr/en), description, responsible manager, active flag, members (logins: manager or supervisor), assigned employees with a job role and dates, and the products it makes. Its stock is its location's `ProductStock` rows: nothing is duplicated. Any number of lines. |
| Product roles | `Product.ManufacturingRole`: none (merchandise, every existing product), raw material, consumable, accessory, spare part, semi-finished, finished. |
| Sale policy | `ManufacturingRoles.ForSale`, one rule for every entry point (hardening review, 2026-10-26; §2). Raw materials, consumables and semi-finished goods are not for sale unless the product is marked `AlsoForSale` (surplus sold on); accessories, spare parts, finished goods and ordinary merchandise always are. |
| Till | `ProductDto.HiddenAtTill` (server-derived from the sale policy) keeps what is not for sale out of the till's grid, search, scanner and price list. Sales are never refused: a till's sale of such a product is recorded, with a warning in the push and the server log. |
| Units | A units table with seven fixed system units (pcs, kg, g, m, cm, mm, l) and configurable ones. Conversion runs within one dimension through each unit's factor, or through a factor written on the product (1 carton = 25 kg). Anything else is refused in words. |
| Base unit | `Product.BaseUnitId`, null until manufacturing sets it. Setting it writes the unit code into `Product.Unit`, so screens and receipts keep printing the same word. Once the product has stock, movements or a recipe, it never changes. |
| BOMs | Versions per product and line (no line = every line): draft → active → retired. Active and retired versions are immutable. A change is a new version, copied whole, and activating it retires the previous one in the same transaction. Lines hold component, quantity per batch, unit, expected waste % and specification; stages are the BOM's own copy, with one output stage. A requirements calculation works for any planned quantity. |
| Stage templates | Reusable, optionally product-specific: name, sequence, minutes, workers, job roles with counts, quality checkpoints. A BOM copies them, so a later edit to a template never rewrites an approved BOM. A future order will copy them again from its BOM. |
| Specifications | Configurable specification fields (length, width, thickness, diameter, grade seeded; more added in the settings), each a measurement in a unit of its dimension or a word. A product and a BOM line carry any of them, optionally, beside the free-text specification. |
| Job roles | A short manufacturing list (welder, cutter…), because HR has only a free-text job title. No pay is stored. |
| Access | Five permission keys in a new `manufacturing` module. The Admin holds all five by role. Anyone else holds them as per-user grants, given on the Manufacturing › Access page until AUTH-1's roles screens exist. |

## 2. Rules worth knowing

- **Line reach.** A caller with company scope reaches every line. Anyone else reaches only a line they
  are a member of, a line they are the responsible manager of, or the line on the branch they work at.
  A line out of reach, and every BOM, stock row or employee read on it, answers **404**.
- **Who may change what.** Creating and editing a line, and naming its members, needs company scope and
  `manufacturing.lines.manage`. Assigning employees and products also needs `manufacturing.lines.manage`,
  and the caller must have company scope or manage that line. Writing a draft needs
  `manufacturing.bom.manage` on a line the caller reaches; a BOM for every line needs company scope.
  Activating or retiring needs `manufacturing.bom.approve`, as a manager of the line (company scope for
  every-line BOMs).
- **Employees stay on their payroll branch.** Assigning one to a line never touches `Employee.BranchId`.
  The candidates list carries number, name, branch and job title, never pay. A branch manager picks
  from their own branch's and the line location's staff, in keeping with P0.1's branch scope.
- **Validation is explicit.** A BOM is refused when a component has no base unit, its unit does not
  convert, its role is not a component role, waste is not 0–<100 %, or it would make a product from
  itself through active recipes (cycle). The output must be semi-finished or finished, with a base unit,
  and on the line's products when the BOM names a line.
- **Sale policy (hardening review, 2026-10-26).** Every way a sale is made, and what it does:
  - *Till, online or queued offline* (`POST /api/sync/push`, the only path that creates a sale; barcode
    and search are the till's own lookups): the till does not offer what is not for sale. A sale that
    names one anyway — a till whose catalogue predates the role, a basket queued offline — is the money
    taken and the receipt printed, so it is **recorded, never refused** (CLAUDE.md, rule one) — payment,
    receipt and stock movement untouched. Owner's decision 1 (2026-10-26): the violation is stored, one
    `ManufacturingSaleException` row per sale line in the sale's own transaction — product, SKU, branch,
    till, receipt number, quantity, sold and received times, cashier, the session that pushed it, the
    role, when the product was last withdrawn from sale, and `SoldBeforeWithdrawal` (stale offline data:
    sold while it was still for sale). The push result also says so and the server logs it.
  - *Refund* of such a sale: accepted as any refund, with no policy warning; it gives back what was sold.
  - *Invoice from a recorded sale or rental movement*: allowed — that sale already happened.
  - *Invoice typed for a customer*: closed at the route since 2026-10-10; the service also refuses a line
    that is not for sale, so no back-office path can start a new sale of one.
  - *Rentals* are hire, not sale, and are left exactly as they were (brief: no rental changes).
  - Nothing already recorded is re-judged: reports, statements and invoices read sales as they were made.
- **Sale-status audit (owner's decision 2, 2026-10-26).** The sold-as-well mark stays under
  `manufacturing.settings`. Every change to a product's role or mark writes a `ProductSaleStatusChange`
  row in the same save: user id and name, time, old and new role, old and new mark, and the policy's
  answer before and after. Saving with nothing changed writes none.
- **Granting the manufacturing permissions.** The page needs `users.manage` (the Admin's today). On top of
  it, a caller gives or takes away only a manufacturing key they hold themselves (`grant_beyond_own`),
  and a caller without company scope sees and changes only users of their own branch, never a
  company-wide account (404). A key outside the five is refused. A switched-off role gives nothing, so a
  grant to its member is stored. The users screen still writes the five legacy keys only: a
  manufacturing key sent through it gives nothing.
- **Specifications.** Optional throughout. Refused: a measured field without a figure above zero, a unit
  of another dimension (a thickness in kg), a word in a measured field or a figure in a text field, a
  field given twice, more than 20. A field or unit a value uses keeps what it measures. A switched-off
  field only leaves the pickers; values already written with it stay valid. A BOM line's figures are
  frozen with the BOM and copied to its next version.
- **Requirements.** batches = planned (converted to the batch unit) ÷ batch quantity. For each line,
  net = quantity × batches, waste = net × waste %, total = net + waste. The total is converted to the
  component's base unit and kept to 6 decimal places; totals are summed per component.

## 3. Schema (migration `ManufacturingPhase1`, SQLite and SQL Server)

New tables, all server-only, every foreign key Restrict except a document's own children:
`UnitsOfMeasure` (6 rows seeded), `ProductUnits`, `ManufacturingJobRoles`, `ProductionLines`,
`ProductionLineMembers`, `ProductionLineEmployees`, `ProductionLineProducts`, `StageTemplates`,
`StageTemplateSteps`, `StageTemplateStepRoles`, `BillsOfMaterials`, `BomLines`, `BomStages`,
`BomStageRoles`.

Existing table, additive only: `Products.ManufacturingRole` (int, not null, default 0) and
`Products.BaseUnitId` (nullable, foreign key to `UnitsOfMeasure`).

Migration `ManufacturingSpecsAndSalePolicy` (hardening review, 2026-10-26), additive only:
`Products.AlsoForSale` (bit, not null, default false); `SpecAttributes` (5 rows seeded, unique code);
`ProductSpecs` and `BomLineSpecs` (one row per field per owner, unique; value (28,10) and unit for a
measurement, text for a word; a line's values cascade with the line); the system unit `mm`.
Its Down drops the three tables, the column and the `mm` row.

Migration `ManufacturingSaleAudit` (2026-10-26), additive only: `ProductSaleStatusChanges` and
`ManufacturingSaleExceptions` (unique per sale line; indexed by branch and by product with the sale
time, for the Phase 2 report). Every foreign key Restrict. Its Down drops both tables. Rolled back and re-applied on SQL Server
(LocalDB) with products kept. Once a product, a product unit or a recipe counts in `mm`, the Down
cannot delete that row and stops: move those to another unit first, or roll back the code only.

Factors and recipe quantities use precision (28,10); waste % uses (9,4). Uniqueness the engines cannot
both hold is also enforced in code: a BOM's next version is drawn under the version lock, because
neither engine's unique index covers a BOM with no line.

**Rollback.** Revert the code. The migration's Down drops the new tables and the two columns. No
existing row is changed, so nothing else needs restoring.

## 4. API (`/api/manufacturing`, `ManufacturingEndpoints.cs`)

| Route | Permission |
|---|---|
| `GET units`, `GET units/convert` | view or settings |
| `POST units`, `PUT units/{id}` | settings |
| `GET materials`, `GET materials/{productId}` | view or settings |
| `PUT materials/{productId}` | settings |
| `GET job-roles`, `GET stage-templates`, `GET stage-templates/{id}` | view or settings |
| `POST/PUT job-roles`, `POST/PUT stage-templates` | settings |
| `GET lines`, `GET lines/{id}`, `GET lines/{id}/stock` | view |
| `POST lines`, `PUT lines/{id}`, `PUT lines/{id}/members`, `PUT lines/{id}/products`, `POST lines/{id}/employees`, `POST lines/{id}/employees/{assignmentId}/end`, `GET lines/{id}/employee-candidates`, `GET line-locations`, `GET line-user-candidates` | lines.manage |
| `GET boms`, `GET boms/{id}`, `GET boms/{id}/requirements` | view |
| `POST boms`, `PUT boms/{id}`, `DELETE boms/{id}`, `POST boms/{id}/new-version` | bom.manage |
| `POST boms/{id}/activate`, `POST boms/{id}/retire` | bom.approve |
| `GET spec-fields` | view or settings |
| `POST spec-fields`, `PUT spec-fields/{id}` | settings |
| `GET access`, `PUT access/{userId}` | users.manage, within the caller's own keys and reach |

A refusal is a ProblemDetails with a stable `code`; the screens translate `mfg.error.<code>`.

## 5. Screens (sidebar › Manufacturing)

Production lines (list, then each line's page with its overview, employees, products, logins and
warehouse stock); Bills of materials (list, editor, requirements calculator); Materials and units
(product classification, the sold-as-well mark and the product's specification); Stage templates; Job
roles; Units of measure (with a converter); Specification fields; Manufacturing access (Admin).
A BOM line edits its figures under its free-text specification. All screens are in Arabic, French and English, use the Riaya patterns
(toolbar filters, card tables, ⋯ menus, dialogs, status pills from `status-tones.ts`) and load in their
own lazily loaded chunk.

## 6. Known limitations

- No production orders, issues, consumption, output, costing or postings. Those are Phase 2 onward.
- Specification figures are stored and checked but nothing calculates with them yet (cutting plans,
  sheet nesting, weight from dimensions). The free text stays beside them, and existing text is not
  parsed into figures.
- A sale of a manufacturing-only item from a till with an old catalogue is recorded and its exception
  stored, not blocked. The back-office exception report ("Manufacturing materials sold at POS") is
  Phase 2; until then the rows are read in the database. A product classified before the audit existed
  has no withdrawal time, so its exceptions say "not known" rather than stale or not.
- Rental contracts do not check the sale policy (nor `IsRentable`, as before): renting is outside this phase.
- The grant writer of the users screen now reconciles the five legacy keys only, so it no longer
  removes a manufacturing grant. Manufacturing grants are managed on their own page until the roles
  screens exist.
- A line's location is chosen from existing warehouses; the line form does not create one.
- `Product.Unit` stays free text for products without a base unit. Receiving, transfers and the till
  still count in the base unit only: alternate units are recorded but no document reads them yet.

## 7. Suggested Phase 2

Production orders on the line's location: immediate start with reservations against free line stock
(audit section 7), material issues and returns, consumption against the issued balance, the leftover
count before completion, partial output releases approved by a production manager, and completion
approval (D22, D36, D41, D42). Each order would copy its BOM's lines and stages at creation. Phase 2
also brings the in-production and reserved columns in the item status report, and the daily
production log with labour bookings (amendments 5–7). Costing and postings wait for the weighted
average (P0.4) and the accountant's sign-off (D9, D13, D33, D43).
