# Delta Specification: Demo Projects with Expanded Columns and 100k+ Rows per Data Source

## ADDED REQUIREMENTS

### REQ-DEMO-COLS-01 — Column Schema Expansion Across All Data Sources
When sample data is generated via `SampleDataGenerator.generateAllSamples`,
the generator shall create tables and file feeds containing additional realistic attributes and metrics across all 8 local data sources:
- `SampleStore.db`: Expanded columns for `Categories`, `Products`, `Customers`, `Orders`, and `OrderItems` (including financial breakdowns, status codes, and timestamps).
- `InventoryStore.db`: Expanded columns for `Warehouses`, `StockLevels`, and `Suppliers` (including location codes, financial values, and safety levels).
- `SalesFeed.csv`: Expanded columns including channel, payment method, gross margin, discount, and representative.
- `CustomersFeed.csv`: Expanded columns including contact info, location, account status, and order totals.
- `RegionalTargets.xlsx`: Expanded columns in `RegionalTargets` and `QuarterlyBudget` worksheets.
- `CommerceOps.xlsx`: Expanded columns across `Customers`, `ProductsStock`, `Sales`, and `DeliveryLogs`.
- `ProductCatalog.json`: Expanded attributes across `Products` and `Reviews`.
- `LogisticsHub.xml`: Expanded attributes and sub-elements across `Carriers` and `Shipments`.

### REQ-DEMO-SCALE-02 — 100,000+ Rows Generation per Data Source
When sample data is generated via `SampleDataGenerator.generateAllSamples`,
the generator shall populate at least 100,000 rows in the primary/transactional entity of each of the 8 local data sources:
1. `SampleStore` (`Orders` >= 100,000 rows, `OrderItems` >= 100,000 rows)
2. `InventoryStore` (`StockLevels` >= 100,000 rows)
3. `SalesFeed` (>= 100,000 rows)
4. `CustomersFeed` (>= 100,000 rows)
5. `RegionalTargets` (`RegionalTargets` sheet >= 100,000 rows)
6. `CommerceOps` (`Sales` sheet >= 100,000 rows, `DeliveryLogs` sheet >= 100,000 rows)
7. `ProductCatalog` (`Products` array >= 100,000 elements, `Reviews` array >= 100,000 elements)
8. `LogisticsHub` (`Shipments` elements >= 100,000 items)

### REQ-DEMO-PERF-03 — High-Performance Streaming & Batching Generation
When populating large datasets (100,000+ rows),
the generator shall use buffered streaming writers and SQLite transactions with synchronous optimizations to complete the total generation of all 8 data sources within seconds, without exceeding memory limits or hanging test execution.

### REQ-DEMO-TEST-04 — Full E2E & Preview Limit Validation
When running `sample_demo.fsqnb` and `sample_demo_pt.fsqnb` across the 100k+ datasets,
all 15 cells in each notebook shall execute without errors, produce valid `Table` or `Scalar` outcomes, respect `PreviewRowLimit` (500 rows preview), and pass both backend and frontend test suites.

