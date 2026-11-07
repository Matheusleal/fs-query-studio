# Change Proposal: Table Header & Body Column Width Synchronization

## 1. Context & Problem Statement
In F# Query Studio interactive notebooks, query results render in a virtualized data grid (`editors/vscode/src/renderer/index.ts`).
While horizontal scroll synchronization was enabled by placing `.fsq-head` inside `.fsq-viewport`, users observed that **the cell widths of the header do not follow the column widths of the body rows when horizontal scrolling occurs**:
- `.fsq-head` and each `.fsq-row` are rendered as independent flexbox containers (`display: flex`) with `width: max-content; min-width: 100%`.
- Header cells contain only short column identifiers (e.g. `Id`, `Price`, `Date`), while body cells contain diverse, arbitrary-length values (e.g. full names, URLs, descriptions, timestamps).
- Because each flex container resolves its item widths independently based on its own immediate children, header cells end up significantly narrower or wider than the corresponding body columns.
- As the user scrolls horizontally, column headers drift and misalign with the underlying data rows.

## 2. Intent Block
```text
Intent Block:
Goal:        Ensure table header cells and body row cells share identical, synchronized column widths across horizontal scrolling and virtualization.
Expectation: Every column header in `.fsq-head` aligns to the exact pixel boundary of its corresponding body cells in all visible rows of `.fsq-rows`, both with few columns and with 15+ wide columns requiring horizontal scroll.
Action:      Replace independent flexbox row layout with a unified CSS Grid column template (`--fsq-grid-cols` and `--fsq-grid-width`) computed from column schemas and sampled row values (`computeColWidths` / `computeGridColumnsCss` in `grid-core.ts`), applied to `.fsq-head`, `.fsq-sizer`, and `.fsq-row`.
Result:      Pixel-perfect, rock-solid alignment between header columns and data columns throughout horizontal and vertical scrolling.
```
