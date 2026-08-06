module MilestoneBAcceptanceTests

open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.App
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui
open TestHelpers

/// Milestone B's acceptance criterion (architecture spec): "Invalid F#
/// produces useful squiggles, local symbols/type information are
/// discoverable, and stale analysis never overwrites current editor
/// state." Three independent parts against the same real worker process,
/// each with its own isolated view-model state so one part's background
/// debounce timer can never interfere with another's assertions.
[<Fact>]
let ``invalid F# produces squiggles`` () =
    task {
        let client = WorkerClient(workerDllPath ())
        let shell = ShellViewModel()

        shell.CheckAsync <-
            Some(fun (version, source) -> AnalysisService.check client shell.ActiveDocument.Uri version source)

        shell.ActiveDocument.ApplyTextChanged(1, "let x : int = \"oops\"")
        // Past the 400ms debounce window, plus real headroom for a freshly
        // spawned worker's first FCS check (cold reference resolution is
        // known to be slow the first time - see A.4's DEVLOG entry).
        do! Task.Delay(3000)

        Assert.NotEmpty(shell.ActiveDocument.Diagnostics)

        Assert.Contains(
            shell.ActiveDocument.Diagnostics,
            fun (d: Diagnostic) -> d.Severity = DiagnosticSeverity.Error
        )
    }

[<Fact>]
let ``hovering a local symbol shows its type`` () =
    task {
        let client = WorkerClient(workerDllPath ())
        let source = "let total = 42"

        // "total" ends at column 9 (0-based) on line 1.
        let! hover = AnalysisService.hover client "file:///hover.fsx" 1 source 1 9

        match hover with
        | None -> failwith "expected hover info"
        | Some info ->
            Assert.Equal("total", info.DisplayName)
            Assert.Equal("int", info.Type)
    }

[<Fact>]
let ``a stale analysis response never overwrites newer editor state, under a real request/response race`` () =
    task {
        let client = WorkerClient(workerDllPath ())
        let document = DocumentViewModel("file:///race.fsx")
        document.ApplyTextChanged(1, "let y : int = \"still wrong\"")
        document.ApplyTextChanged(2, "let z : int = \"also wrong\"")

        // The newer response (v2) arrives and is applied first.
        let! newerDiagnostics =
            AnalysisService.check client document.Uri 2 "let z : int = \"also wrong\""

        document.ApplyAnalysisResult(2, newerDiagnostics)
        let diagnosticsAfterNewer = document.Diagnostics

        // The older response (v1) - a real round trip through the same
        // worker, artificially delayed by this test only awaiting/applying
        // it after the newer one already landed - must be discarded.
        let! staleDiagnostics =
            AnalysisService.check client document.Uri 1 "let y : int = \"still wrong\""

        document.ApplyAnalysisResult(1, staleDiagnostics)

        Assert.Equal<Diagnostic list>(diagnosticsAfterNewer, document.Diagnostics)
    }
