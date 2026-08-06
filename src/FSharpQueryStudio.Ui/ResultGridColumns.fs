namespace FSharpQueryStudio.Ui

open FSharpQueryStudio.Protocol

/// One virtualized `DataGrid` column derived from a `ResultEnvelope`'s
/// schema — see C.12. UI-framework-agnostic on purpose: `MainWindow`'s
/// code-behind is what turns this into a real Avalonia `DataGridColumn`
/// (an indexed `Binding` into `Result.Rows`'s `obj array` rows), keeping
/// the derivation itself testable without an Avalonia application
/// context.
type ResultColumn = { Header: string; BindingPath: string }

module ResultGridColumns =
    /// One column per `Column`, in schema order, bound by position —
    /// `Rows` is `obj array array` with no named properties to bind by
    /// name, so `"[0]"`, `"[1]"`, ... is how Avalonia's `Binding` reaches
    /// each cell.
    let build (schema: Column list) : ResultColumn list =
        schema |> List.mapi (fun i column -> { Header = column.Name; BindingPath = $"[{i}]" })
