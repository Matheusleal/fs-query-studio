module LogViewerViewModelTests

open System
open System.ComponentModel
open Xunit
open FSharpQueryStudio.Logging
open FSharpQueryStudio.Ui

let private entry (offsetMinutes: int) (level: string) (role: string) (message: string) : LogEntry =
    { Timestamp = DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero).AddMinutes(float offsetMinutes)
      Level = level
      ProcessRole = role
      Message = message }

/// A fake `readEntriesSince` keyed by file path: returns the mapped entry
/// list (once, advancing the offset to `1L`) the first time a path is
/// asked for at offset `0L`, and nothing for any other call — enough to
/// drive every `LogViewerViewModel` behavior under test without touching
/// real files or a real clock.
let private fakeReader (byPath: Map<string, LogEntry list>) : string -> int64 -> LogEntry list * int64 =
    fun path offset ->
        if offset = 0L then
            match byPath.TryFind path with
            | Some entries -> entries, 1L
            | None -> [], 0L
        else
            [], offset

let private today = DateOnly.FromDateTime(DateTime.Today)
let private hostPathFor (date: DateOnly) = IO.Path.Combine("logs", $"host-{date:yyyyMMdd}.log")
let private workerPathFor (date: DateOnly) = IO.Path.Combine("logs", $"worker-{date:yyyyMMdd}.log")

[<Fact>]
let ``a freshly-constructed LogViewerViewModel defaults to today, live, with no entries`` () =
    let vm = LogViewerViewModel("logs", fun _ _ -> [], 0L)

    Assert.Equal(today, vm.SelectedDate)
    Assert.True(vm.IsLive)
    Assert.Empty(vm.Entries)

[<Fact>]
let ``Poll merges host and worker entries and sorts them by Timestamp`` () =
    let reader =
        fakeReader (
            Map [ hostPathFor today, [ entry 2 "Information" "host" "later host" ]
                  workerPathFor today, [ entry 1 "Information" "worker" "earlier worker" ] ]
        )

    let vm = LogViewerViewModel("logs", reader)
    vm.Poll()

    Assert.Equal<string list>([ "earlier worker"; "later host" ], vm.Entries |> List.map (fun e -> e.Message))

[<Fact>]
let ``Poll is a no-op once IsLive is false`` () =
    let mutable callCount = 0

    let reader =
        fun (_path: string) (_offset: int64) ->
            callCount <- callCount + 1
            [], 0L

    let vm = LogViewerViewModel("logs", reader)
    vm.SelectedDate <- today.AddDays(-1)
    callCount <- 0 // Reset past the switch's own static read, which does call the reader.

    vm.Poll()

    Assert.Equal(0, callCount)

[<Fact>]
let ``LevelFilter narrows Entries without a fresh read, using All as the no-filter sentinel`` () =
    let reader =
        fakeReader (
            Map [ hostPathFor today,
                  [ entry 1 "Information" "host" "info line"
                    entry 2 "Error" "host" "error line" ] ]
        )

    let vm = LogViewerViewModel("logs", reader)
    vm.Poll()

    Assert.Equal("All", vm.LevelFilter)

    vm.LevelFilter <- "Error"

    Assert.Equal<string list>([ "error line" ], vm.Entries |> List.map (fun e -> e.Message))

[<Fact>]
let ``ProcessFilter narrows Entries to just the selected process`` () =
    let reader =
        fakeReader (
            Map [ hostPathFor today, [ entry 1 "Information" "host" "host line" ]
                  workerPathFor today, [ entry 2 "Information" "worker" "worker line" ] ]
        )

    let vm = LogViewerViewModel("logs", reader)
    vm.Poll()

    vm.ProcessFilter <- "worker"

    Assert.Equal<string list>([ "worker line" ], vm.Entries |> List.map (fun e -> e.Message))

[<Fact>]
let ``SelectedDate set to a past date reads that date's files once, statically, and sets IsLive false`` () =
    let pastDate = today.AddDays(-1)
    let reader = fakeReader (Map [ hostPathFor pastDate, [ entry 0 "Information" "host" "yesterday's entry" ] ])

    let vm = LogViewerViewModel("logs", reader)
    vm.SelectedDate <- pastDate

    Assert.False(vm.IsLive)
    Assert.Equal<string list>([ "yesterday's entry" ], vm.Entries |> List.map (fun e -> e.Message))

[<Fact>]
let ``SelectedDate set back to today resumes live polling`` () =
    let vm = LogViewerViewModel("logs", fun _ _ -> [], 0L)
    vm.SelectedDate <- today.AddDays(-1)
    vm.SelectedDate <- today

    Assert.True(vm.IsLive)

[<Fact>]
let ``PropertyChanged fires for Entries when Poll finds new entries`` () =
    let reader = fakeReader (Map [ hostPathFor today, [ entry 0 "Information" "host" "one" ] ])
    let vm = LogViewerViewModel("logs", reader)
    let raisedFor = ResizeArray<string>()
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    vm.Poll()

    Assert.Contains("Entries", raisedFor)

[<Fact>]
let ``LevelOptions and ProcessOptions both start with the All sentinel`` () =
    let vm = LogViewerViewModel("logs", fun _ _ -> [], 0L)

    Assert.Equal("All", List.head vm.LevelOptions)
    Assert.Equal("All", List.head vm.ProcessOptions)

[<Fact>]
let ``HasEntries and HasNoEntries track Entries, including after a filter empties it`` () =
    let reader = fakeReader (Map [ hostPathFor today, [ entry 0 "Information" "host" "one" ] ])
    let vm = LogViewerViewModel("logs", reader)

    Assert.False(vm.HasEntries)
    Assert.True(vm.HasNoEntries)

    vm.Poll()

    Assert.True(vm.HasEntries)
    Assert.False(vm.HasNoEntries)

    vm.LevelFilter <- "Error" // No entries at this level - filters everything out.

    Assert.False(vm.HasEntries)
    Assert.True(vm.HasNoEntries)
