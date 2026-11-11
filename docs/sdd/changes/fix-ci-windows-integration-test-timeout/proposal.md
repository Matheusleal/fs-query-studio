# Proposal: Increase Integration Tests Timeout & Resiliency for Windows CI

## Intent Block
**Goal:** Eliminate intermittent `System.OperationCanceledException` timeouts during Windows CI test execution.  
**Expectation:** `Tests.a throwing script returns a structured Error result and the worker survives to handle the next request` completes reliably even when GitHub-hosted Windows runners experience high CPU load from concurrent solution test runs.  
**Action:** Increase `roundTrip`'s `CancellationTokenSource` timeout from 30s to 120s in `FSharpQueryStudio.Integration.Tests/Tests.fs`, adjust `withTimeout` ceiling in `TestHelpers.fs`, and increase Windows CI job timeout from 15m to 20m.  
**Result:** 100% test pass rate across both Windows and Linux CI environments without transient cancellation timeouts.

