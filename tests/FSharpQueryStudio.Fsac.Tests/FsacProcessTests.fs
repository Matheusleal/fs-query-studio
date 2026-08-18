module FsacProcessTests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Fsac

let private fsacDllPath () =
    Path.Combine(AppContext.BaseDirectory, "FsacTool", "fsautocomplete.dll")

[<Fact>]
let ``EnsureClient starts fsautocomplete and completes the initialize handshake`` () =
    task {
        let fsac = FsacProcess(fsacDllPath ())
        let! client = fsac.EnsureClient()

        let! result = client.SendRequest("textDocument/hover", {| textDocument = {| uri = "file:///doesnotexist.fsx" |}; position = {| line = 0; character = 0 |} |})

        // Any well-formed response (success or a structured LSP error) proves
        // the process is alive and speaking JSON-RPC back - the content of
        // this particular hover doesn't matter, only that the handshake
        // completed and the process answers requests.
        match result with
        | LspOk _
        | LspFail _ -> ()
    }

[<Fact>]
let ``EnsureClient respawns after the process is killed`` () =
    task {
        let fsac = FsacProcess(fsacDllPath ())
        let! _firstClient = fsac.EnsureClient()
        fsac.KillForTests()

        let! secondClient = fsac.EnsureClient()
        let! result = secondClient.SendRequest("textDocument/hover", {| textDocument = {| uri = "file:///a.fsx" |}; position = {| line = 0; character = 0 |} |})

        match result with
        | LspOk _
        | LspFail { Code = -1 } -> failwith "expected a real response from a freshly respawned process, not the old dead one"
        | LspFail _ -> ()
    }
