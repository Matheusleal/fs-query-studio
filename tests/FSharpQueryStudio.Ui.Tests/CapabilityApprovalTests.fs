module CapabilityApprovalTests

open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui

let private canned (kind: ResultKind) (displayText: string) : ResultEnvelope =
    { Kind = kind
      DisplayText = Some displayText
      Schema = []
      Rows = [||]
      IsTruncated = false
      ContinuationToken = None
      ElapsedMs = 3L
      Output = [] }

let private trustRequesting (capabilities: string list) : TrustMetadata =
    { Origin = ScriptOrigin.Downloaded
      TrustState = TrustState.Unknown
      RequestedCapabilities = capabilities
      ReviewedAt = None
      ReviewedBy = None }

let private configureExecute (vm: ShellViewModel) =
    let mutable executed = false
    vm.ExecuteAsync <- Some(fun _source ->
        executed <- true
        Task.FromResult(canned ResultKind.Scalar "2"))
    (fun () -> executed)

[<Fact>]
let ``RunAsync blocks on an untrusted script requesting capabilities until approval is given`` () =
    task {
        let vm = ShellViewModel()
        let wasExecuted = configureExecute vm
        vm.Trust <- trustRequesting [ "network"; "filesystem-read" ]
        let mutable promptedCapabilities = None
        vm.RequestCapabilityApproval <- Some(fun trust ->
            promptedCapabilities <- Some trust.RequestedCapabilities
            true)

        do! vm.RunAsync()

        Assert.Equal(Some [ "network"; "filesystem-read" ], promptedCapabilities)
        Assert.True(wasExecuted ())
    }

[<Fact>]
let ``approving requested capabilities updates TrustState to Trusted`` () =
    task {
        let vm = ShellViewModel()
        configureExecute vm |> ignore
        vm.Trust <- trustRequesting [ "network" ]
        vm.RequestCapabilityApproval <- Some(fun _trust -> true)

        do! vm.RunAsync()

        Assert.Equal(TrustState.Trusted, vm.Trust.TrustState)
    }

[<Fact>]
let ``denying requested capabilities prevents ExecuteAsync from being sent`` () =
    task {
        let vm = ShellViewModel()
        let wasExecuted = configureExecute vm
        vm.Trust <- trustRequesting [ "network" ]
        vm.RequestCapabilityApproval <- Some(fun _trust -> false)

        do! vm.RunAsync()

        Assert.False(wasExecuted ())
        Assert.Equal(TrustState.Unknown, vm.Trust.TrustState)
    }

[<Fact>]
let ``no approval hook configured denies by default rather than running unapproved`` () =
    task {
        let vm = ShellViewModel()
        let wasExecuted = configureExecute vm
        vm.Trust <- trustRequesting [ "network" ]

        do! vm.RunAsync()

        Assert.False(wasExecuted ())
    }

[<Fact>]
let ``an already Trusted script runs without prompting`` () =
    task {
        let vm = ShellViewModel()
        let wasExecuted = configureExecute vm
        vm.Trust <- { trustRequesting [ "network" ] with TrustState = TrustState.Trusted }
        let mutable promptInvoked = false
        vm.RequestCapabilityApproval <- Some(fun _trust ->
            promptInvoked <- true
            true)

        do! vm.RunAsync()

        Assert.False(promptInvoked)
        Assert.True(wasExecuted ())
    }

[<Fact>]
let ``a Blocked script never runs and never prompts`` () =
    task {
        let vm = ShellViewModel()
        let wasExecuted = configureExecute vm
        vm.Trust <- { trustRequesting [ "network" ] with TrustState = TrustState.Blocked }
        let mutable promptInvoked = false
        vm.RequestCapabilityApproval <- Some(fun _trust ->
            promptInvoked <- true
            true)

        do! vm.RunAsync()

        Assert.False(promptInvoked)
        Assert.False(wasExecuted ())
    }

[<Fact>]
let ``a script requesting no capabilities runs without prompting even when TrustState is Unknown`` () =
    task {
        let vm = ShellViewModel()
        let wasExecuted = configureExecute vm
        vm.Trust <- trustRequesting []
        let mutable promptInvoked = false
        vm.RequestCapabilityApproval <- Some(fun _trust ->
            promptInvoked <- true
            true)

        do! vm.RunAsync()

        Assert.False(promptInvoked)
        Assert.True(wasExecuted ())
    }
