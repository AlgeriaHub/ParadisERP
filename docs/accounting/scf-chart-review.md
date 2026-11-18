# ParadiERP — Chart of Accounts against the SCF (D33)

*Internal review of 2026-11-01, for the owner and developers. It answers D33, which the owner made our own team's responsibility on 2026-10-31, and the numbering in C-04, REN-18 and PAY-02, which the owner made internal on 2026-11-01. Nothing in the system was changed.*

## Source

- **The chart reviewed:** `LedgerAccounts.Default` in `ParadiERP.Domain/LedgerEntities.cs`. These are the 37 system accounts seeded on every company. Code posts to them by number, so they cannot be renumbered from the Accounts screen (`LedgerEndpoints.cs:86-120`). An admin may add accounts of their own; those were not reviewed, because nothing in code posts to them.
- **The SCF:** Arrêté du 26 juillet 2008, *Journal officiel* n° 19 of 25 March 2009 (joradp.dz, `F2009019.pdf`). That arrêté sets the "Nomenclature des comptes à trois chiffres" and the rules for using each account. The quotations below come from that text.
- **Scope:** whether each account sits under the right SCF heading. Valuation, tax and timing stay with the accountant (the IDs in brackets).

The SCF fixes the two- and three-digit accounts. Four-digit sub-accounts (4457, 6061 and so on) are the company's own, but they must sit under the right three-digit account.

## Result in short

- **20 of 37 accounts are correct.**
- **9 are correct with a note.**
- **8 are under the wrong SCF heading:** 310, 318, 4181, 4182, 6061, 6068, 707 and 756.
- **The most serious is 310.** Merchandise is posted to 310, but under the SCF, 31 is for raw materials and merchandise is 30. Once manufacturing raw materials go to 311, as C-04 proposes, merchandise and raw materials would add up under the same heading.
- **756 is the cheapest to fix.** It is scrap sales here, but under the SCF it means recoveries on written-off debts. Nothing posts to it yet (B-F09), so it can change before its first entry.
- **4181 and 4182 hold liabilities under 418, an asset heading.** Under the SCF, 418 is income not yet invoiced. Customers' money held for later goes to 419.
- **The C-04, REN-18 and PAY-02 accounts:** the eight payroll accounts in PAY-02 all match. The manufacturing codes proposed in C-04 match, once 310 is fixed. Of the REN-18 rental accounts, 706 and 757 match, 708 is acceptable, and 707 and 756 are wrong.

## Account by account

✓ correct · ~ correct with a note · ✗ wrong SCF heading

| Code | What ParadiERP uses it for | SCF account | Verdict |
|---|---|---|---|
| 890 | Opening balance (equity) | Class 8: no SCF account. §312.3: "classes 0, 8 et 9 … utilisées librement par les entités" for "opérations particulières qui n'auraient pas leur place dans les comptes des classes 1 à 7" | ~ Acceptable as a temporary account at go-live. An opening balance does belong in classes 1–7 (capital 101, reserves 106, retained earnings 11), so 890 must be cleared into class 1 before any financial statement (GL-10). |
| 181 | Inter-branch account, computed, never posted | 181 "Comptes de liaison entre établissements" | ✓ |
| 310 | Merchandise stock, including rental equipment | 31 "Matières premières et fournitures". The SCF: "les marchandises achetées en vue d'être revendues en l'état (compte 30)" | ✗ Merchandise belongs in **30**. Rental equipment leaves stock for class 2 anyway (REN-12, approved). |
| 318 | Scrap (damaged beyond use) | Under 31 (raw materials). The SCF has 358 "Produits résiduels ou matières de récupération (déchets, rebuts)" for scrap from the company's own production, and **36** "Stocks provenant d'immobilisations" for parts "démontés ou récupérés des immobilisations corporelles" | ✗ Not under 31. Each kind of scrap has its own SCF home: from rental equipment → **36** (once it is a fixed asset, REN-12); from manufacturing → **358**; damaged merchandise stays in **30**, written down through 390. The value is the accountant's (REN-10, STK-04). |
| 401 | Suppliers | 401 "Fournisseurs de stocks et services" | ✓ Equipment bought as fixed assets will need 404 "Fournisseurs d'immobilisations" (REN-12). |
| 411 | Customers | 411 "Clients" | ✓ |
| 413 | Uncleared cheques and transfers | 413 "Clients, effets à recevoir" | ~ Acceptable. Instruments handed to the bank for collection are 511 "Valeurs à l'encaissement"; that choice goes with TRS-06. |
| 4181 | Gift-card money (a liability) | 418 "Clients - produits non encore facturés": an asset, "débité … des créances … pour lesquelles les pièces justificatives … n'ont pas encore été établies" | ✗ A liability under an asset heading. The SCF home is **419**: "les avances ou acomptes reçus … sur les commandes à livrer … ou les services à rendre". Fits SAL-17 (approved). |
| 4182 | Loyalty points (a liability) | 418, as above | ✗ Same problem. A **419** sub-account. How points are accounted for is SAL-16 (accountant). |
| 419 | Customer deposits (nothing posts yet) | 419 "Clients créditeurs, avances reçues, RRR à accorder et autres avoirs à établir" | ~ Correct for advances on rent. A returnable guarantee is 165 "Dépôts et cautionnements reçus" (REN-13). |
| 421 | Wages payable | 421 "Personnel, rémunérations dues" | ✓ |
| 425 | Staff advances | 425 "Personnel, avances et acomptes accordés" | ✓ |
| 431 | Social security (CNAS) | 431 "Sécurité sociale" | ✓ |
| 442 | Income tax withheld (IRG) | 442 "Etat, impôts et taxes recouvrables sur des tiers" | ✓ |
| 447 | Other payroll taxes payable | 447 "Autres impôts, taxes et versements assimilés" | ✓ |
| 4457 | VAT collected | Under 445 "Etat, taxes sur le chiffre d'affaires" | ✓ Its partner 4456 (deductible VAT) is missing (TAX-05). |
| 471 | Suspense; typed as an asset | 47 "Comptes transitoires ou d'attente": "imputée au compte définitif dans les délais les plus brefs ; ce compte ne figure pas sur les états financiers" | ~ The code is right. Under the SCF it must be **zero** before any financial statement. Today the balance sheet shows it as an asset. Clearing it is GL-09. |
| 512 | Bank | 512 "Banques comptes courants" | ✓ A CCP account would be 517 "Autres organismes financiers" (TRS-03). |
| 530 | Cash | 53 "Caisse" | ✓ |
| 600 | Cost of goods sold | 600 "Achats de marchandises vendues" | ✓ |
| 615 | Repairs on own equipment | 615 "Entretien, réparations et maintenance" | ✓ |
| 624 | Transport | 624 "Transports de biens et transport collectif du personnel" | ✓ |
| 630 | Old payroll heading; nothing posts | 63 "Charges de personnel"; 630 itself is not an SCF account | ~ Not an SCF account, but it is unused and always zero. Leave it. |
| 631 | Wages | 631 "Rémunérations du personnel" | ✓ |
| 635 | Employer social contributions | 635 "Cotisations aux organismes sociaux" | ✓ |
| 641 | Employer taxes on wages | 641 "Impôts, taxes et versements assimilés sur rémunérations" | ~ The code is right. The temporary use for "other" deductions (D-F05) is not a tax on wages; that is PAY-03. |
| 657 | Stock losses, and today also count gains | 657 "Charges exceptionnelles de gestion courante" | ~ Acceptable for losses (STK-02). Count gains are income and cannot be credited to a charge account (B-F08); 757 is the SCF's nearest account. |
| 658 | Other operating expenses | 658 "Autres charges de gestion courante" | ✓ |
| 6061 | Fuel | 606 is not an SCF account. SCF 607 "Achats non stockés de matières et fournitures" | ✗ A French PCG code. Belongs under **607**. |
| 6068 | Sundry purchases not stocked | As 6061 | ✗ Belongs under **607**. |
| 700 | Merchandise sales | 700 "Ventes de marchandises" | ✓ |
| 706 | Rental income | 706 "Autres prestations de services" | ✓ Service income (SAL-01) belongs here too, in a sub-account. |
| 707 | Repairs charged to the customer | No 707 in the SCF: 70 lists 700–706, 708, 709 | ✗ A repair is a service: a **706** sub-account. |
| 708 | Late-return income | 708 "Produits des activités annexes" | ~ Acceptable. If REN-06 says late days are extra rent, a 706 sub-account fits better. |
| 756 | Scrap sales (nothing posts yet) | 756 "Rentrées sur créances amorties" (recoveries on debts already written off) | ✗ Wrong meaning. Scrap sales are **703** "Ventes de produits résiduels". If the item is still a fixed asset, the gain on disposal is 752. |
| 757 | Damage and loss compensation | 757 "Produits exceptionnels sur opérations de gestion" | ✓ Its tax is REN-08. |
| 758 | Rounding, both ways | 758 "Autres produits de gestion courante" | ~ Gains only. A rounding loss is a charge (658), not a debit to an income account (SAL-04). |

## C-04, REN-18 and PAY-02

These three ask the accountant to confirm account numbers, as D33 did. The SCF text answers the numbering part:

- **PAY-02 — all eight match the SCF:** 421, 425, 431, 442, 447, 631, 635 and 641. Nothing to change. What is still open is the use of 641 for "other" deductions (PAY-03) and whether CNAS and IRG are right (PAY-01). Those are not numbering questions.
- **REN-18:** 706 and 757 are correct; 708 is acceptable (REN-06 may move it); 707 and 756 are wrong (see the table).
- **C-04:** the sign-off sheet's codes match the SCF: 311 raw materials, 321/322 consumables, 331 work in progress, 351/355 intermediate and finished products, 358 scrap, 601/602 consumption, 701 sales of finished products. Two notes:
  - **358, not 318.**
  - **72 has two SCF accounts:** 723 for work in progress and 724 for finished products.
  - **First, merchandise must leave 310.** Otherwise 31 would mix merchandise and raw materials.

What stays with the accountant is how manufacturing is accounted for (C-03, Model N or W), not the numbers.

**Owner, 1 Nov 2026:** the numbering in C-04, REN-18 and PAY-02 is internal, like D33. The three questions are no longer in the questionnaire; their accounting questions stay with the accountant.

## Accounts the approved decisions will need

None of these is in the chart yet. Each one sits under its SCF heading:

| For | Accounts |
|---|---|
| Official books and year-end (GL-01, CLO-08) | 101 capital, 106 reserves, 11 retained earnings, 12 result for the year |
| Rental equipment as fixed assets (REN-12) | 215 or 218 equipment, 28 depreciation (2815 / 2818), 681 depreciation charge, 404 suppliers of fixed assets, 652 / 752 loss or gain on disposal, 36 parts recovered from fixed assets |
| Gift cards (SAL-17) | A 419 sub-account instead of 4181 |
| Deductible VAT (TAX-05, after the accountant) | 4456 under 445 |
| Write-downs of stock or scrap (REN-10, STK-04) | 390 impairment of merchandise, 685 / 785 charge and reversal |
| A rent adjustment at the period end, if the accountant asks for one (REN-02) | 487 "Produits constatés d'avance" |
| Manufacturing (C-04) | The codes above, after 310 moves to 30 |
| Bank and cash (TRS-02 to TRS-05) | 627 bank charges and card commission, 581 cash in transit between the box and the bank, 511 cheques sent for collection, 517 CCP |
| Supplier advances (PUR-08) | 409 |
| Bad debts (SAL-18) | 416 doubtful debts, 491 impairment of customers, 654 irrecoverable debts |

## How the mismatches would be fixed

**Owner, 1 Nov 2026: all eight wait for the cutover**, 756 included, so they are fixed together. Each fix still needs its own go-ahead. Code posts to these accounts by number, so a fix is a software change:
- add the SCF account;
- point the code at it;
- add a migration for both SQLite and SQL Server;
- move any balance with one dated entry: Dr new / Cr old.

The old account then keeps its history at zero. Each fix needs its own go-ahead.

- **Live balances** (read-only check of 2026-11-01, whole history, all branches): 310 holds 59,534,750 DA, 6068 holds 16,100 DA and 6061 holds 1,000 DA. 318, 4181, 4182, 707 and 756 have never been posted to, so they need no transfer entry; they only need nothing to post to them before the cutover. Check again just before the cutover.
- **756 → 703:** nothing posts to it yet, so it needs no transfer entry. It waits for the cutover with the rest (owner), so nothing must start posting to it before then.
- **All eight:** move them on one date, the first day of a financial year. That is the date the accountant is already being asked about for the stock-method switch (D43) and for moving rental equipment out of 310 (REN-12). Moving the chart at the same time means the balances move once and the statements stay comparable within the year.
- **Gift cards:** the SAL-17 build can open on the 419 sub-account directly.
