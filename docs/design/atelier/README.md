# Atelier — the ParadisERP design (v3, 2026-10-12)

The owner asked for a full redesign of the whole product — menus, cards, popup messages,
tables, every screen — and, shown three directions, told Claude to pick: **Atelier** was chosen.
It replaces the classic teal look and the four "new look" palettes (Atlas, Oasis, Sahara,
Graphite). Light and dark mode stay.

The prototypes live on the screen-designs canvas (https://claude.ai/artifact/5mHXNMf4K4AnA5kTxNNaUV,
page "Nouvelles directions": *A · Atelier* and *A · Atelier — page de vente*). Their style sheets
are copied here as the reference: [`atelier.css`](atelier.css) (back office, every component)
and [`atelier-till.css`](atelier-till.css) (the selling screen). Port their *rules*; the app's own
class names stay (`.btn-primary`, `.bo-panel`, `.bo-data`, `app-status`, `.tile`, …).

## The idea in one line

Warm paper, black ink outlines, hard offset shadows, one signal orange. A workshop look: high
contrast in a bright shop, prints cleanly, and nothing like the soft cards it replaces.

## What does not change

- The three rules: never block the sale; say the state in words; mirror, don't rewrite (logical
  properties only — `inset-inline-*`, `margin-inline`, `padding-inline`, `border-inline-*`,
  `text-align: start|end`).
- Routes, permissions, business logic, data, print layouts (the receipt and printed documents
  stay black on white).
- Country is configuration; every interface string in fr, ar and en.
- Status colours come from `ui/status-tones.ts`; a state is a tone, a mark and a word.

## Tokens

Declared in the `:root` block of `src/styles.scss` (light) and `src/_theme.scss` (dark, under
`html[data-mode='dark']`). Existing names keep their meaning; values change.

| Token | Light | Dark | Role |
|---|---|---|---|
| `--bg` | `#f3efe6` | `#161512` | Paper: the page ground |
| `--surface` | `#fffcf5` | `#1f1d19` | Cards, panels, inputs, dialogs |
| `--surface-2` | `#f8f3e8` | `#26241f` | Recessed, hovered, zebra rows |
| `--surface-tint` | `#fde2d4` | `#4a2416` | Selected row, active page, hover on a menu item |
| `--border` | `#ddd5c3` | `#3a362f` | Hairlines inside a card (row rules, separators) |
| `--border-strong` | `#1b1a17` | `#a39d90` | **The outline**: every card, control and dialog edge |
| `--hard` (new) | `#1b1a17` | `#000000` | Colour of the hard offset shadow |
| `--text` | `#1b1a17` | `#f3efe6` | Body, headings, figures |
| `--text-2` (new) | `#3a3833` | `#d9d3c5` | Strong secondary text |
| `--muted` | `#645f54` | `#a8a194` | Labels, meta, table headers on paper |
| `--primary` | `#1b1a17` | `#f3efe6` | The committing action is **ink-filled** |
| `--on-primary` (new) | `#fffcf5` | `#161512` | Text on `--primary` (replaces every `#fff` on a primary fill) |
| `--primary-dark` | `#33312c` | `#ffffff` | Primary hover; money figures |
| `--primary-soft` | `#fde2d4` | `#4a2416` | Active tab ground, amount due ground |
| `--accent` (new) | `#e4572e` | `#e4572e` | Signal orange: pay key, active-tab underline, focus ring, key figure tile, brand mark |
| `--on-accent` (new) | `#1b1a17` | `#1b1a17` | Text on orange (white fails contrast on it) |
| `--ink` | `#1b1a17` | `#0d0c0a` | The top bar and the till bar |
| `--ink-2` | `#2b2925` | `#1a1916` | Hover on the bar |
| `--on-ink` | `#fffcf5` | `#f3efe6` | Text on the bar |
| `--on-ink-muted` | `#bdb6a6` | `#a8a194` | Secondary text on the bar |
| `--on-ink-line` | `rgba(255, 252, 245, 0.16)` | same | Hairlines on the bar |
| `--ink-accent` / `--on-ink-accent` | `var(--accent)` / `var(--on-accent)` | same | Figures and keys on the bar |
| `--ok` / `--ok-soft` | `#2e6a2e` / `#dcebcf` | `#8fd18f` / `#22331f` | |
| `--warn` / `--warn-soft` | `#965400` / `#fbe6bf` | `#f5b94d` / `#3a2a10` | |
| `--danger` / `--danger-soft` | `#b42318` / `#f9d9d3` | `#f49a8c` / `#3d1a15` | |
| `--danger-fill` (new) | `#b42318` | `#b42318` | Fill of a destructive button (white text) |
| `--info` / `--info-soft` | `#1f4e8c` / `#dbe5f4` | `#9cc0f2` / `#18263b` | |
| `--radius` | `6px` | | Controls, buttons, tags |
| `--radius-sm` | `4px` | | Status tags, small buttons, menu items |
| `--radius-lg` | `10px` | | Dialogs |
| `--radius-card` | `8px` | | Cards, panels, metric tiles (back office **and** till) |
| `--radius-field` | `6px` | | Inputs, selects, notices, check tiles |
| `--radius-pill` | `999px` | | Category chips on the till only |
| `--line` (new) | `1.5px` | | Width of every outline |
| `--shadow` | `4px 4px 0 var(--hard)` | | Cards that float a little: panels, metric tiles, menus |
| `--shadow-sm` (new) | `3px 3px 0 var(--hard)` | | Product tiles, hovered buttons |
| `--shadow-pop` | `10px 10px 0 var(--hard)` | | Dialogs, the drawer |
| `--font-display` (new) | `"Alexandria", var(--font-ui, var(--font))` | | Page titles, card titles, key figures, dialog titles |
| `--font-mono` (new) | `"IBM Plex Mono", ui-monospace, monospace` | | Money and quantities (`.mono`, `.num`) |
| `--font` | `"IBM Plex Sans Arabic", "Segoe UI", system-ui, sans-serif` | | Body text (the company's `uiFont` still overrides via `--font-ui`) |
| `--t-fast` | `0.14s` | | Colour, border, shadow, transform |
| `--t-slide` | `0.22s` | | Dialog, drawer, toast entrances |

The rest of `:root` (spacing `--sp-*`, type scale `--fs-*`, control heights, z-index, widths)
keeps its values.

## Components

**Outline and shadow are the system.** A card, control or dialog is `var(--line) solid
var(--border-strong)`. What floats casts a *hard* shadow (no blur) toward inline-end and down;
in RTL the shadow mirrors: use `box-shadow: 4px 4px 0 var(--hard)` in LTR and
`-4px 4px 0 var(--hard)` under `[dir='rtl']` (or a `--shadow-x` variable set per direction).

- **Buttons** — height `--control-h-sm` (36px) in the back office, `--control-h` (44px) on the
  till; radius `--radius`; outline `var(--line) solid var(--border-strong)`; weight 600.
  *Primary*: `--primary` fill, `--on-primary` text. *Ghost/secondary*: `--surface` fill, ink
  outline. *Danger*: `--danger-fill`, white text. *Accent* (`.btn-accent`, new): `--accent` fill,
  `--on-accent` text — the till's pay key and nothing else. Hover: `translate(-2px, -2px)` and
  `--shadow-sm`; active: no transform, no shadow. No pill buttons anywhere.
- **Fields** — `--surface` ground, `var(--line) solid var(--border-strong)`, radius
  `--radius-field`, height 42px in the back office and 44px on the till. Focus: no outline glow;
  `box-shadow: 3px 3px 0 var(--accent)` (mirrored in RTL). Invalid: `--danger` outline and
  `3px 3px 0 var(--danger-soft)`, plus the message in words. Labels: 13px, weight 600, `--text`.
  The required/optional pill: `--surface-2`, `--radius-sm`, 12px, weight 600.
- **Cards and panels** (`.card`, `.bo-panel`, `.ui-surface`) — `--surface`, outline, radius
  `--radius-card`, `--shadow`. The head is divided from the body by `var(--line) solid
  var(--border-strong)`, its title in `--font-display` 20px weight 600.
- **Metric tiles** (`.bo-metric`, `.ui-metric`, the dashboard `.block`, the sales `.stat`) — a card;
  label weight 600 `--text-2`; figure in `--font-display` 34px weight 600, letter-spacing -0.03em;
  an icon square (34px, `--primary-soft` fill, outline). One tile per screen may be the *key*
  tile: `--accent` fill, `--on-accent` text.
- **Tables** (`table`, `.bo-data`, `.ui-table`) — header row on `--ink` with `--on-ink` text,
  13px weight 600, no capitals; cells 14px, padded `14px 18px`; row rules `1px solid
  var(--border)`; even rows `--surface-2`; hover `--surface-tint`. Money columns `--font-mono`,
  `text-align: end`. The table sits inside a card; the card's foot (pager) is divided by an
  ink line.
- **Status** (`app-status`, `.bo-state`, `.state`, `.badge`) — an outlined tag: 26px tall,
  radius `--radius-sm`, `var(--line) solid currentColor`, the tone's soft fill, a **square**
  7px mark (`border-radius: 1px`) and the word, 12.5px weight 600.
- **Tabs and sub-navigation** — a row of links; the current one is `--text` weight 700 with a
  3px `--text` underline; others `--muted` weight 500. Segmented filters (`.tabs`, `.ui-tabs`,
  period pickers) are a single outlined strip; the pressed segment is ink-filled.
- **Dropdown menus** (`app-menu`) — `--surface`, outline, radius `--radius-card`, `--shadow`;
  items 40px, radius `--radius-sm`, hover/current `--surface-tint`; danger items `--danger`;
  separators 1.5px `--border`. Entrance: 140ms fade + 4px rise.
- **Dialogs** (`app-dialog`, `.till-dialog .modal`, `.bo-modal`, confirm) — `--surface`, 2px
  outline, radius `--radius-lg`, `--shadow-pop`; title in `--font-display` 24px weight 700; the
  foot divided by a 1.5px dashed `--border`; backdrop `rgba(27, 26, 23, 0.55)`, no blur.
  Entrance: 220ms rise + fade.
- **Toasts / popup messages** — `--ink` ground, `--on-ink` text, radius `--radius-card`,
  `4px 4px 0 var(--accent)`; a 30px square icon tile in `--accent` (success `#7bd389`, error
  `#f49a8c` with ink glyph); title weight 700, body 13px `--on-ink-muted`. Entrance: 220ms rise.
- **Notices and banners** (`.notice`) — outline `var(--line) solid var(--border-strong)`, radius
  `--radius-card`, the tone's soft ground, a 34px ink square holding the icon. No coloured
  border-inline-start strip.
- **Navigation, back office** — no sidebar. A 60px `--ink` bar across the top: brand mark
  (34px `--accent` square, ink letters) and name, then the modules as text links (current module
  `--on-ink` weight 600 with a 3px `--accent` underline; their icons only from 1600px), then the
  language and the user's initials menu. Under it, a context row on `--surface` with an ink rule
  below: the current module's pages as tabs, and at the inline end the working branch and the
  connection state in words. Below 1000px the bar keeps the menu button, the brand, the
  connection state and the user; the drawer holds the branch and the module and page list.
- **Navigation, till** — the same 60px ink bar: mark, branch and till code, the till's pages as
  text links with the orange underline, sync count as an orange tag, connection in words, the
  back-office link, the user.
- **Page head** — an eyebrow (the breadcrumb) in `--text-2` weight 600 with `--accent` `›`
  separators — `--accent` on paper is only 3.1:1, so it is never small text; the title in
  `--font-display` 28–40px weight 700, letter-spacing -0.03em; the lead in `--muted` 15px.
- **Icons** — decision D1 is lifted: stroke icons (24-unit grid, `stroke-width: 2`, round caps,
  `currentColor`, `aria-hidden`) may sit beside a label on buttons, menu items, metric tiles and
  notices. An icon never carries a state alone and never replaces a word. A directional icon
  (chevron, arrow) mirrors in RTL with `.ic-dir { transform: scaleX(-1) }` under `[dir='rtl']`.
- **Motion** — colour, border, shadow and transform move at `--t-fast`; entrances at `--t-slide`.
  An entrance animates the separate `translate` property (and opacity where the element may start
  hidden), never `transform`: with `animation-fill-mode: both`, a keyframe's final
  `transform: none` would undo a dialog's centring transform. A menu is opaque the moment it is
  placed. Everything is switched off under `prefers-reduced-motion: reduce`.
- **Paper** — the receipt (`table.lines`) and the A4 documents (`.print-table`) keep their own
  black-on-white look: the ink table header, zebra rows and mono figures apply on screen only and
  skip those tables.

## The till

See `atelier-till.css`. Product tiles are outlined cards with `--shadow-sm` (lift on hover), the
price in `--font-display` 21px, stock in words (`--warn` when low, `--danger` "Rupture de stock"
when out), the SKU in `--font-mono`, and a 46px ink **+** (orange on hover) — still the only control
that adds. The cart sits on `--surface` behind a 2px ink rule; quantities in an outlined stepper;
the grand total in `--font-display` 34px; the pay key is the one `.btn-accent`: 66px, 2px outline,
`--shadow`. The payment dialog's amount due is an `--accent` block; methods are one outlined
segmented strip; change is an `--ok` outlined block, a shortfall a `--danger` one.
