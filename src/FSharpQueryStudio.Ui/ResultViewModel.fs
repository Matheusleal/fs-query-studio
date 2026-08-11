namespace FSharpQueryStudio.Ui

open System.ComponentModel
open FSharpQueryStudio.Protocol

/// See Section 5's core view models. Holds the most recent execution's
/// `ResultEnvelope`; rendering it as a table/output view is A.16's job.
type ResultViewModel() as self =
    let mutable kind = ResultKind.NoValue
    let mutable displayText: string option = None
    let mutable schema: Column list = []
    let mutable rows: obj array array = [||]
    let mutable isTruncated = false
    let mutable elapsedMs = 0L
    let mutable output: OutputLine list = []
    let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member _.PropertyChanged = propertyChanged.Publish

    member _.Kind = kind

    /// Unwrapped, never the raw `string option` - `ResultsView.axaml`
    /// binds this straight to a `TextBlock.Text`, which needs a real
    /// `string`. Binding to the option directly used to fall back to
    /// Avalonia calling `.ToString()` on the boxed `FSharpOption<string>`
    /// itself, rendering the literal text "Some(null)"/"None" instead of
    /// unwrapping it.
    member _.DisplayText = displayText |> Option.defaultValue ""
    member _.Schema = schema
    member _.Rows = rows
    member _.IsTruncated = isTruncated
    member _.ElapsedMs = elapsedMs
    member _.Output = output

    /// Derived from `Kind` for the results view's XAML to switch between a
    /// row-oriented and a single display-text presentation without a
    /// custom value converter.
    member _.IsTable = kind = ResultKind.Table

    member _.IsScalarOrText =
        kind = ResultKind.Scalar || kind = ResultKind.Text || kind = ResultKind.Error

    /// The "basic" (non-virtualized) table preview for A.16's Results tab —
    /// one comma-joined display string per row. C.12 replaces this with a
    /// real virtualized `DataGrid` bound to `Schema`/`Rows` directly.
    member _.FormattedRows: string list =
        rows |> Array.map (fun row -> row |> Array.map string |> String.concat ", ") |> Array.toList

    /// The current result as plain text for the OS clipboard — see C.15.
    /// Tab-separated with a header row for `Table` (pastes cleanly into
    /// a spreadsheet), otherwise just `DisplayText`.
    member _.ClipboardText: string =
        if kind = ResultKind.Table then
            let cell (value: obj) = if isNull value then "" else string value

            let header = schema |> List.map (fun c -> c.Name) |> String.concat "\t"

            let dataLines =
                rows |> Array.map (fun row -> row |> Array.map cell |> String.concat "\t") |> Array.toList

            header :: dataLines |> String.concat "\n"
        else
            displayText |> Option.defaultValue ""

    member _.Apply(envelope: ResultEnvelope) =
        kind <- envelope.Kind
        displayText <- envelope.DisplayText
        schema <- envelope.Schema
        rows <- envelope.Rows
        isTruncated <- envelope.IsTruncated
        elapsedMs <- envelope.ElapsedMs
        output <- envelope.Output

        for name in
            [ "Kind"
              "DisplayText"
              "Schema"
              "Rows"
              "IsTruncated"
              "ElapsedMs"
              "Output"
              "IsTable"
              "IsScalarOrText"
              "FormattedRows" ] do
            propertyChanged.Trigger(self, PropertyChangedEventArgs(name))
