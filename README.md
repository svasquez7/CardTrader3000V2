# CardTrader3000

Blazor (.NET 10, Interactive Server) + SQLite app that prices sports cards with Claude and tracks eBay inventory.

**Status:** Phase 1 complete (steps 1–5): scaffold, schema, Claude service, import pipeline, and the Import, Inventory and History UIs.

## First run

You need the .NET 10 SDK.

```bash
cd src/CardTrader3000

# 1. EF Core CLI (one time)
dotnet tool install --global dotnet-ef

# 2. Claude API key: stored in user-secrets, never in appsettings.json
dotnet user-secrets set "Claude:ApiKey" "sk-ant-..."

# 3. Create the initial migration (this generates Data/Migrations/)
dotnet ef migrations add InitialCreate --output-dir Data/Migrations

# 4. Run. Migrations are applied automatically on startup and create cardtrader3000.db.
dotnet run
```

If you skip step 3, startup fails with a "pending model changes" error. That's EF Core telling you to add the migration.

Then open **Import**, upload a CSV or paste lines such as `2013 Topps Football, Von Miller, 290, Base`, check the preview, and click **Price & add**.

### Upgrading an existing database

When an update changes the entities, add a migration once from `src/CardTrader3000`, then run as usual (it's applied on startup):

```bash
dotnet ef migrations add TrackOnlyAndReprice --output-dir Data/Migrations   # track-only + re-price
dotnet ef migrations add JsonImport --output-dir Data/Migrations            # JSON import
dotnet ef migrations add EbayListings --output-dir Data/Migrations          # eBay listing
dotnet ef migrations add EbayStoreCategories --output-dir Data/Migrations   # eBay Store categories
```

Add only the ones you haven't added yet; `dotnet ef migrations list` shows what you have.

## eBay listing (sandbox)

Open a card in **Inventory** and click **List on eBay**. This opens a listing screen pre-filled from the card. Review it, add photos, and click **Publish to eBay**.

**Every listing is Buy It Now (Good 'Til Cancelled) with Best Offer.** Offers under **80%** of the price are auto-declined, and offers at **90%** or more are auto-accepted. Each listing is also added to a **2% general Promoted Listings** campaign. All three numbers are defaults you can change in eBay Settings, or on each listing.

### One-time setup

1. **Developer keys.** In the eBay developer portal, open your **Sandbox** keyset.
   - Under **User Tokens → Get a Token from eBay via Your Application**, add an *eBay Redirect URL* (RuName).
   - Set its **auth accepted URL** to `https://localhost:7243/ebay/callback` and its **auth declined URL** to `https://localhost:7243/ebay/declined`.
   - Then, from `src/CardTrader3000`:
     ```bash
     dotnet user-secrets set "Ebay:ClientId" "<App ID>"
     dotnet user-secrets set "Ebay:ClientSecret" "<Cert ID>"
     dotnet user-secrets set "Ebay:RuName" "<RuName>"
     ```
2. **Sandbox seller.** Create a sandbox test user in the developer portal. You'll sign in as this user.
3. **eBay Settings page** (sidebar):
   - **Connect to eBay** and sign in as the sandbox user. Tokens are encrypted with ASP.NET Data Protection; the keys live in `App_Data/keys`.
   - **Policies:** sandbox accounts usually start with none. Click **Enable business policies**, wait a few minutes, then **Create starter policies**. That creates free eBay Standard Envelope shipping, 30-day returns (or no returns) and immediate payment. If you already have policies, just pick the defaults.
   - **Ship-from location:** enter a ZIP code and click **Add**. eBay requires a location on every offer.
   - Save the defaults.

### The listing screen

- **Pre-filled from the card:**
  - title, description, price (the card's estimate), quantity (your stock)
  - condition: Ungraded, Near Mint or Better
  - item specifics: sport, league, player, team, manufacturer, set, season, card number, parallel, rookie and so on
  - your default policies, Best Offer percentages and promotion rate
- **Store category:** pick one of your eBay Store categories. The list is set in eBay Settings and defaults to Additional Sports & TCG, Apparel, Baseball, Basketball, Football and Other. New listings choose Football, Baseball or Basketball from the card's sport; other sports get Additional Sports & TCG. eBay's docs show category paths (`/Football`) while sellers report plain names working, so the app sends `/Name` and retries with `Name` if eBay rejects it. Store categories only work on accounts with an eBay Store subscription, and sandbox test users usually don't have one; choose **(none)** there.
- **Photos:** upload up to 24 (12 MB each), or paste `https://` image URLs. Reorder them; the first is the gallery photo. Uploaded photos are stored in `App_Data/listing-images` and sent to eBay's picture hosting (Media API) when you publish.
- **Live figures:** the Best Offer auto-decline and auto-accept amounts, the promotion fee per sale, and the estimated net after eBay fees, the envelope and the ad fee.
- **Publish** runs these steps:
  1. upload the photos
  2. create the inventory item (condition `USED_VERY_GOOD` = Ungraded, with descriptor `40001` = Card Condition)
  3. create the offer: fixed price, Best Offer terms, policies, location
  4. publish it
  5. add the listing to the promotion campaign
- **After publishing:**
  - The card's status changes to **Listed**.
  - **Update listing on eBay** revises the live listing.
  - **End listing** withdraws it, and the card goes back to In Stock.
  - If promotion fails, the listing still stays live and you get a warning.
- **eBay Listings** (sidebar) lists drafts and published, failed and ended listings, with links to each listing on eBay.

### Sandbox notes

- The **Media API sandbox host** (`apim.sandbox.ebay.com`) has a history of not resolving. If photo upload fails with a "couldn't reach" message, paste image URLs instead. They're sent to eBay as-is. The production Media API works normally.
- **eBay Standard Envelope** is only allowed for items up to $20. The listing screen warns you when the price is higher.
- Item specifics are best-effort defaults. If eBay requires one that's missing, the publish error names it; add it under **Item specifics** and publish again.
- To switch to production later, set `Ebay:Environment` to `Production`, use your production keys and RuName, and connect again. Sandbox and production keep separate settings and listings.

## JSON import

The Import page has a third mode, **JSON file**, alongside CSV and manual entry, which work as before. Each card can carry any of its data points. Only `card_set`, `player` and `card_number` are required:

| Field | Aliases | Notes |
|---|---|---|
| `card_set` | `set`, `set_name` | required |
| `player` | `player_name`, `name` | required |
| `card_number` | `number` | required, a leading `#` is removed |
| `parallel` | `parallel_feature`, `variant` | defaults to `Base` |
| `quantity` | `qty` | 1 to 10,000, default 1 |
| `status` | | `In Stock`, `Listed`, `Sold` |
| `team` | | |
| `is_rookie` | `rookie`, `rc` | inferred from "RC" or "Rookie" in the title when missing |
| `estimated_list_price` | `list_price`, numeric `price` | **when present, the card is stored with this price and no Claude call is made** |
| `sgc_grading_candidate` | `sgc` | `High Prospect`, `Secondary`, `No` |
| `sales_strategy` | | |
| `ebay_title` | `title` | trimmed to 80 characters |
| `ebay_description` | `description`, `description_template` | line breaks kept |
| `price_with_claude` | boolean `price` | for cards **without** a list price: `false` makes the card track-only. Otherwise the page's default applies. |

- **The file can be shaped three ways:** an array of cards, `{ "cards": [...] }`, or a single card. **The Phase 1 prompt's output can be imported as-is**: nested `pricing_and_returns` and `ebay_listing_details` are read automatically. Field names can be snake_case or camelCase.
- **Fee, net, margin and bucket fields in the file are ignored.** They're recalculated from the list price using the configured rates, so the math always matches the rest of the app.
- **Your values win.** When a card goes to Claude because it has no list price, any team, title, description, SGC rating or status in the file overrides what Claude returns.
- Existing cards have their quantity increased, as with CSV, and the file's data points overwrite their details.
- Cards priced from the file show **From file** in History and add nothing to token usage.
- See `samples/cards-sample.json` (all three kinds of card) and `samples/phase1-output-sample.json` (the prompt's output format).

## Track-only cards and re-pricing

Each line takes an optional fifth column, `price`, that is `true` or `false` (`yes`/`no`, `y`/`n` and `1`/`0` also work):

```text
2010 Topps Football, Jimmy Graham, 265, Base, true
1991 Fleer, Common Player, 112, Base, false
1991 Fleer, Another Common, 113, false      ← parallel left out: defaults to Base
```

- **`false` = track only.** The card is added to inventory, or its quantity goes up, with **no Claude call and no AI cost**. A new track-only card shows as *Not priced* and has no price, bucket or SGC rating. An existing priced card keeps its current pricing.
- Lines without the column use the **Price cards with Claude by default** checkbox on the Import page. The preview shows how many cards will be priced and how many are track only.
- If the same card appears on several lines with different flags, it's priced if any of those lines says `true`.
- Batch totals (gross, fees, net, buckets) cover priced cards only. Track-only copies are counted separately.
- **Re-price** sends cards back through Claude for fresh pricing, title, description and SGC rating. **Stock is never changed.** You can run it from:
  - the **Re-price** button on any inventory row (it reads **Price** on a card that hasn't been priced yet)
  - the checkboxes plus **Re-price selected** (asks you to confirm first)
  - the **Re-price** button in the card detail view (asks to confirm when the card already has pricing)
  - Each re-price runs as its own batch with source *Re-price*, so it appears in History with its token usage. Titles and descriptions you edited by hand are replaced.
- The Inventory page has a **Priced / Not priced** filter. The bucket and SGC filters only match priced cards.

## Import pipeline (step 4)

```text
Import page ──▶ ImportService ──▶ ImportQueue ──▶ ImportWorker (background) ──▶ ImportProcessor
   parse + preview   create batch       FIFO           one batch at a time         per chunk: Claude → merge → save
```

- **Parsing** (`CardLineParser`): CsvHelper handles quoted fields such as `"2020 Panini Prizm, Silver"`. It skips a header row if there is one and accepts tab-separated text pasted from a spreadsheet. Values are sanitized (control and zero-width characters, curly quotes, extra whitespace, formula prefixes like `=`) and length-capped. Set, player and number are required, and parallel defaults to `Base`. The limit is 5,000 lines and 5 MB.
- **Dedupe within an import:** identical lines are combined into one card with a quantity, so Claude prices each card once.
- **Merge into inventory:** an existing card (same normalized key) gets its **quantity increased** and, for priced lines, its pricing refreshed. A card previously marked Sold is restocked at the new quantity. New cards are inserted.
- **Durable progress:** every chunk is saved before the next one starts. If the app stops mid-import, the batch resumes from its remaining cards on the next start.
- **Failures:** a card that fails stays on the batch with its error message, and **Retry failed cards** re-queues just those. A missing API key stops the batch early instead of failing every chunk.
- **Totals** are recalculated from the batch's items whenever the batch finishes, so they stay correct after retries. Counts and money are quantity-weighted.

## Pages (step 5)

- **Dashboard:** stock totals, recent imports, and in-stock SGC high prospects.
- **Import:** CSV upload or manual entry, a preview, a live progress bar, the batch summary, and retrying failed cards.
- **Inventory:** paged on the server (25/50/100 rows per page). Search matches player, set, number, parallel, team and title, with every word required to match. You can filter by set, bucket, SGC rating, status and rookies only, and sort by any number column. Totals at the top reflect the current filter. Clicking a row opens the card:
  - the pricing breakdown and sales strategy
  - editable quantity and status (quantity 0 marks the card Sold)
  - the eBay title and description, which you can edit, save and copy with one click
  - the card's import history
  - delete
- **History:** a list of batches filterable by status, with live progress while a batch runs. Clicking a batch (`/history/{id}`) shows its summary, the fee rates used, token counts, every card line (new, merged or failed) with a link to the inventory card, retrying failed cards, and the raw Claude responses.

Links of the form `/inventory?card=123` open a card directly.

## Layout

```text
src/CardTrader3000/
├── Data/
│   ├── AppDbContext.cs            decimals stored as REAL, enums stored as strings
│   ├── CardKey.cs                 duplicate-detection key (set|player|number|parallel)
│   └── Entities/                  InventoryCard, ImportBatch, ImportBatchItem, enums
├── Prompts/card-evaluation.md     the Claude prompt (embedded resource, edit freely)
├── Services/
│   ├── Claude/                    ClaudeCardEvaluator + API wire types + output schema
│   ├── Ebay/                      OAuth, API client (Account/Inventory/Media/Marketing), listing service, image store
│   ├── Import/                    CSV + JSON parsers, ImportService, queue/progress, worker, processor, history queries
│   ├── Inventory/                 InventoryService (search, paging, sort, edits)
│   └── Pricing/                   FeeCalculator, FeeOptions, ListingText (80-char title guard)
├── Components/                    Corona dark layout, pages, shared widgets (badges, pager, card modal)
└── wwwroot/lib/corona/            only the Corona assets the app uses (MIT, BootstrapDash)
```

## Design decisions

- **Claude judges, code calculates.** Claude returns team, rookie flag, list price, SGC rating, strategy, title and description. `FeeCalculator` computes the eBay fee, postage, net, margin, bucket and batch totals using the exact Phase 1 formulas, so the math is always right. Fee rates live in `appsettings.json` under `Fees`.
- **Structured outputs.** Requests use `output_config.format` with a JSON schema (`CardEvaluationSchema.cs`), so responses always parse.
- **Chunking.** Cards go out in chunks (`Claude:ChunkSize`, default 25). If a chunk's output is truncated, the chunk is split in half and retried. A failed chunk only fails its own cards.
- **Retries.** 429, 5xx and 529 (overloaded) responses retry with backoff and honor `retry-after`.
- **Titles.** Titles are trimmed to 80 characters at a word boundary. "RC" and "Rookie Card" are stripped when the card isn't a rookie.
- **Duplicates increase quantity.** `InventoryCard.NormalizedKey` is unique. When a card is re-imported, its `Quantity` goes up and its pricing is refreshed. `ImportBatchItem` snapshots the price at import time, so history survives later re-pricing.
- **Raw responses** from Claude are stored on `ImportBatch.RawResponseJson` for debugging.

## Configuration (`appsettings.json`)

| Key | Default | Notes |
|---|---|---|
| `Claude:Model` | `claude-sonnet-5-5` | Any current Claude model id |
| `Claude:ChunkSize` | 25 | Cards per request |
| `Claude:MaxTokens` | 16000 | Output ceiling per request |
| `Claude:MaxRetries` | 3 | Transient error retries |
| `Fees:*` | 2026 rates | FVF 13.25%, $0.30 fixed fee, $0.78 envelope, $2.99 Bucket 1 threshold |

## Caveat

List prices are Claude's estimates from training knowledge, not live eBay sold data. Treat them as a starting point.
