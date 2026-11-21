module Tests

open System.Threading
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Security

[<Fact>]
let ``Save then TryRead returns the saved secret`` () =
    let store = InMemorySecretStore() :> ISecretStore
    store.Save "vault://sales-db" "s3cr3t"

    Assert.Equal(Some "s3cr3t", store.TryRead "vault://sales-db")

[<Fact>]
let ``TryRead returns None for a reference that was never saved`` () =
    let store = InMemorySecretStore() :> ISecretStore

    Assert.Equal(None, store.TryRead "vault://never-registered")

[<Fact>]
let ``Save overwrites a previously saved secret for the same reference`` () =
    let store = InMemorySecretStore() :> ISecretStore
    store.Save "vault://sales-db" "old-secret"
    store.Save "vault://sales-db" "new-secret"

    Assert.Equal(Some "new-secret", store.TryRead "vault://sales-db")

[<Fact>]
let ``Delete removes a saved secret so TryRead returns None afterward`` () =
    let store = InMemorySecretStore() :> ISecretStore
    store.Save "vault://sales-db" "s3cr3t"

    store.Delete "vault://sales-db"

    Assert.Equal(None, store.TryRead "vault://sales-db")

[<Fact>]
let ``Delete is a no-op for a reference that was never saved`` () =
    let store = InMemorySecretStore() :> ISecretStore

    store.Delete "vault://never-registered" // Must not throw.

// ---------------------------------------------------------------------------
// TryReadAsync tests (Scenario 1: Successful read)
// ---------------------------------------------------------------------------

[<Fact>]
let ``Save then TryReadAsync returns the saved secret`` () =
    task {
        let store = InMemorySecretStore() :> ISecretStore
        store.Save "vault://sales-db" "s3cr3t"

        let! result = store.TryReadAsync("vault://sales-db", CancellationToken.None)
        Assert.Equal(Some "s3cr3t", result)
    }

[<Fact>]
let ``TryReadAsync handles distinct references with distinct secrets`` () =
    task {
        let store = InMemorySecretStore() :> ISecretStore
        store.Save "ref-1" "val-1"
        store.Save "ref-2" "val-2"

        let! res1 = store.TryReadAsync("ref-1", CancellationToken.None)
        let! res2 = store.TryReadAsync("ref-2", CancellationToken.None)
        Assert.Equal(Some "val-1", res1)
        Assert.Equal(Some "val-2", res2)
    }

// ---------------------------------------------------------------------------
// TryReadAsync tests (Scenario 2: Overwrite and update)
// ---------------------------------------------------------------------------

[<Fact>]
let ``Save overwrites secret and TryReadAsync sees updated secret`` () =
    task {
        let store = InMemorySecretStore() :> ISecretStore
        store.Save "vault://orders" "initial"
        store.Save "vault://orders" "updated"

        let! result = store.TryReadAsync("vault://orders", CancellationToken.None)
        Assert.Equal(Some "updated", result)
    }

[<Fact>]
let ``Multiple sequential updates are accurately reflected by TryReadAsync`` () =
    task {
        let store = InMemorySecretStore() :> ISecretStore
        for i in 1..5 do
            store.Save "counter" $"pass-{i}"

        let! result = store.TryReadAsync("counter", CancellationToken.None)
        Assert.Equal(Some "pass-5", result)
    }

// ---------------------------------------------------------------------------
// TryReadAsync tests (Scenario 3: Non-existent reference returns None)
// ---------------------------------------------------------------------------

[<Fact>]
let ``TryReadAsync returns None for unregistered reference`` () =
    task {
        let store = InMemorySecretStore() :> ISecretStore
        let! result = store.TryReadAsync("missing-ref", CancellationToken.None)
        Assert.Equal(None, result)
    }

[<Fact>]
let ``TryReadAsync returns None for empty reference`` () =
    task {
        let store = InMemorySecretStore() :> ISecretStore
        let! result = store.TryReadAsync("", CancellationToken.None)
        Assert.Equal(None, result)
    }

// ---------------------------------------------------------------------------
// TryReadAsync tests (Scenario 4: Delete removes secret)
// ---------------------------------------------------------------------------

[<Fact>]
let ``Delete removes secret so TryReadAsync returns None afterward`` () =
    task {
        let store = InMemorySecretStore() :> ISecretStore
        store.Save "temp-secret" "abc"
        store.Delete "temp-secret"

        let! result = store.TryReadAsync("temp-secret", CancellationToken.None)
        Assert.Equal(None, result)
    }

[<Fact>]
let ``Delete on non-existent reference does not throw and TryReadAsync returns None`` () =
    task {
        let store = InMemorySecretStore() :> ISecretStore
        store.Delete "non-existent"

        let! result = store.TryReadAsync("non-existent", CancellationToken.None)
        Assert.Equal(None, result)
    }

// ---------------------------------------------------------------------------
// TryReadAsync tests (Scenario 5: Cancellation token handling)
// ---------------------------------------------------------------------------

[<Fact>]
let ``TryReadAsync with pre-cancelled CancellationToken returns cancelled task`` () =
    let store = InMemorySecretStore() :> ISecretStore
    store.Save "token-ref" "tok"
    use cts = new CancellationTokenSource()
    cts.Cancel()

    let t = store.TryReadAsync("token-ref", cts.Token)
    Assert.True(t.IsCanceled)

[<Fact>]
let ``TryReadAsync with cancelled token throws TaskCanceledException when awaited`` () =
    task {
        let store = InMemorySecretStore() :> ISecretStore
        store.Save "token-ref" "tok"
        use cts = new CancellationTokenSource()
        cts.Cancel()

        let! ex = Assert.ThrowsAsync<TaskCanceledException>(fun () -> store.TryReadAsync("token-ref", cts.Token))
        Assert.NotNull(ex)
    }
