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
dotnet ef migrations add TrackOnlyAndReprice --output-dir Data/Migrations
```

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
│   ├── Import/                    parser, ImportService, queue/progress, worker, processor, history queries
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
