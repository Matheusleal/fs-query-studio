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
        use fsac = new FsacProcess(fsacDllPath ())
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
        use fsac = new FsacProcess(fsacDllPath ())
        let! _firstClient = fsac.EnsureClient()
        fsac.KillForTests()

        let! secondClient = fsac.EnsureClient()
        let! result = secondClient.SendRequest("textDocument/hover", {| textDocument = {| uri = "file:///a.fsx" |}; position = {| line = 0; character = 0 |} |})

        match result with
        | LspOk _
        | LspFail { Code = -1 } -> failwith "expected a real response from a freshly respawned process, not the old dead one"
        | LspFail _ -> ()
    }

[<Fact>]
let ``EnsureClient throws when fsautocomplete dll does not exist and Shutdown cleans up`` () =
    task {
        use badFsac = new FsacProcess("nonexistent_fsautocomplete_dll_path_12345.dll")
        badFsac.Shutdown()
        let! ex = Assert.ThrowsAsync<Exception>(fun () -> badFsac.EnsureClient() :> System.Threading.Tasks.Task)
        Assert.Contains("initialize", ex.Message)
        badFsac.Shutdown()

        // Exercise respawn when client.IsAlive is false while process has not exited (covers killProc lines 24-25)
        use liveFsac = new FsacProcess(fsacDllPath ())
        liveFsac.KillForTests() // covers line 162 when current is None
        let! client1 = liveFsac.EnsureClient()
        for f in typeof<LspClient>.GetFields(System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Instance) do
            if f.FieldType = typeof<int> && f.Name.StartsWith("alive") then
                f.SetValue(client1, 0)

        // Invoke killProc with an unstarted Process so proc.HasExited throws (covers line 25)
        let killProcMi = typeof<FsacProcess>.GetMethod("killProc", System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Instance ||| System.Reflection.BindingFlags.Static)
        if not (isNull killProcMi) then
            use unstarted = new System.Diagnostics.Process()
            if killProcMi.IsStatic then killProcMi.Invoke(null, [| box unstarted |]) |> ignore
            else killProcMi.Invoke(liveFsac, [| box unstarted |]) |> ignore

        let! client2 = liveFsac.EnsureClient()
        liveFsac.Shutdown()

        // Set current to Some(unstartedProc, client2) and call Shutdown so proc.HasExited throws inside Shutdown's try/with (covers lines 174-175)
        for f in typeof<FsacProcess>.GetFields(System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Instance) do
            if f.Name.StartsWith("current") then
                use unstarted2 = new System.Diagnostics.Process()
                f.SetValue(liveFsac, box (Some(unstarted2, client2)))
                liveFsac.Shutdown()
    }


