# Delta Specification: Table Header & Body Column Width Synchronization

## 1. System Requirements (GEARS)

### ADDED Requirements

```text
When rendering a table payload in the notebook output grid,
the renderer shall compute content-proportional column widths for each column based on header names and sampled row values, clamped between 96px and 480px.

When mounting the grid viewport,
the renderer shall define unified CSS Grid column track specifications (--fsq-grid-cols) and total layout width (--fsq-grid-width) on the viewport container.

Where the sticky table header (.fsq-head) is positioned over vertically scrolled rows in .fsq-viewport,
the table header shall render with an opaque solid background-color (var(--vscode-editorWidget-background, var(--vscode-editor-background))) and elevated z-index (z-index: 10), guaranteeing that rows scrolling behind top: 0 are completely obscured and never bleed into or overlap the header text.

While scrolling horizontally or vertically in .fsq-viewport,
the table header (.fsq-head) and every data row (.fsq-row) shall utilize display: grid with the identical track template (--fsq-grid-cols), guaranteeing exact column alignment.

When additional rows are received via continuation streaming,
the renderer shall re-evaluate column widths if incoming data exceeds current widths and update the grid template dynamically.
```

### MODIFIED Requirements

```text
Where table data rows and headers are displayed,
the .fsq-head and .fsq-rows .fsq-row containers shall use CSS Grid layout rather than independent flexbox containers, ensuring that cell widths are locked to shared track sizes across the entire table.

Where table cells (.fsq-cell) are laid out in a grid,
the cells shall have min-width: 0 with text-overflow: ellipsis and overflow: hidden, permitting standard CSS text truncation within fixed-width grid tracks.
```

### REMOVED Requirements
None.
