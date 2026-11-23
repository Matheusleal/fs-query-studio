# Delta Specification: Aba de Raw Query e Parâmetros na Janela de Resultados da Query

## 1. ADDED Requirements

### [REQ-RAWQUERY-PROTO-001] Transporte de Raw Query e Parâmetros no Envelope de Resultados
```text
When a query or evaluation produces a `ResultEnvelope` with kind `Table`,
the backend worker shall populate `RawQuery` with the formatted SQL text and `Parameters` with the ordered list of parameter names and values executed against the database, or assign `None` and an empty list when no relational SQL query is associated with the result.
```

### [REQ-RAWQUERY-TRACK-002] Rastreamento de Query SQL e Parâmetros em Pushdown e Execuções Diretas
```text
When an expression involving `SqlPushdownQueryable`, `ConnectorQueryResult`, `rawQuery`, or database connector queries is projected or evaluated,
the projection engine and execution context shall capture the generated SQL statement and bound parameters and associate them with the resulting `ResultEnvelope`.
```

### [REQ-RAWQUERY-PAYLOAD-003] Projeção de Raw Query no TablePayload do Renderizador
```text
When the extension maps a `Table`-kind `ResultEnvelope` to a `TablePayload` for the notebook renderer,
the `buildTablePayload` function shall propagate `rawQuery` and `parameters` to the renderer payload.
```

### [REQ-RAWQUERY-TAB-004] Aba "Raw Query" na Barra de Ferramentas de Resultados
```text
When a tabular query result is rendered in the notebook output panel,
the results grid toolbar shall render a segmented button for "Raw Query" (`data-view="rawQuery"`) alongside the "Data" and "Schema" buttons.
```

### [REQ-RAWQUERY-VIEW-005] Exibição Formatada de SQL, Tabela de Parâmetros e Cópia Rápida
```text
When the user activates the "Raw Query" tab in the results panel,
the renderer shall hide the data viewport and schema view, display the formatted SQL query in a monospace code container, display an itemized table of bound parameters (name and value) when parameters exist, provide a one-click copy button with visual feedback, and display an explanatory message when no SQL query was executed for the result.
```

---

## 2. MODIFIED Requirements

### [REQ-RENDERER-TAB-TOGGLE] Alternância Entre Visualizações de Resultados
- **MODIFIED FROM**: Alternância exclusiva de 2 estados entre `Data` e `Schema`.
- **MODIFIED TO (GEARS)**:
```text
When the user clicks any view button in the results toolbar (`Data`, `Schema`, or `Raw Query`),
the renderer shall set the selected button to active, deactivate the other two buttons, and display exclusively the corresponding panel (`.fsq-viewport` + `.fsq-head`, `.fsq-schema`, or `.fsq-raw-query`).
```
