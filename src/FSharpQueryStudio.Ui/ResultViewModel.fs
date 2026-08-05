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
    let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member _.PropertyChanged = propertyChanged.Publish

    member _.Kind = kind
    member _.DisplayText = displayText
    member _.Schema = schema
    member _.Rows = rows
    member _.IsTruncated = isTruncated
    member _.ElapsedMs = elapsedMs

    member _.Apply(envelope: ResultEnvelope) =
        kind <- envelope.Kind
        displayText <- envelope.DisplayText
        schema <- envelope.Schema
        rows <- envelope.Rows
        isTruncated <- envelope.IsTruncated
        elapsedMs <- envelope.ElapsedMs

        for name in [ "Kind"; "DisplayText"; "Schema"; "Rows"; "IsTruncated"; "ElapsedMs" ] do
            propertyChanged.Trigger(self, PropertyChangedEventArgs(name))
