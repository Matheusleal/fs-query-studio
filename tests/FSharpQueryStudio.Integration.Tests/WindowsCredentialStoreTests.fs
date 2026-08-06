module WindowsCredentialStoreTests

open System
open Xunit
open FSharpQueryStudio.Security

/// A `Fact` that only actually runs on Windows — `WindowsCredentialStore`
/// P/Invokes `advapi32.dll`, which has nothing to call on other
/// platforms. xUnit v2 has no dynamic skip; setting `Skip` from the
/// constructor is the standard workaround.
type WindowsOnlyFactAttribute() =
    inherit FactAttribute()

    do
        if not (OperatingSystem.IsWindows()) then
            base.Skip <- "Windows-only: requires the Windows Credential Manager."

/// Exercises the real Windows Credential Manager (Section 14's chosen
/// v1 secret store), not the `InMemorySecretStore` fake every other
/// suite drives. Uses a unique reference per run so repeated local runs
/// never collide with a credential a previous run left behind, and
/// cleans up after itself either way.
[<WindowsOnlyFact>]
let ``Save, TryRead, and Delete round-trip a secret through the real Windows Credential Manager`` () =
    let store = WindowsCredentialStore() :> ISecretStore
    let reference = "fs-query-studio-test-" + Guid.NewGuid().ToString("N")

    try
        Assert.Equal(None, store.TryRead reference)

        store.Save reference "s3cr3t-integration-value"
        Assert.Equal(Some "s3cr3t-integration-value", store.TryRead reference)

        store.Save reference "replaced-value"
        Assert.Equal(Some "replaced-value", store.TryRead reference)

        store.Delete reference
        Assert.Equal(None, store.TryRead reference)
    finally
        store.Delete reference
