# Tasks: Increase Integration Tests Timeout & Resiliency for Windows CI

- [x] TASK-CI-001: Increase cancellation timeout in `tests/FSharpQueryStudio.Integration.Tests/Tests.fs` from 30s to 120s.
- [x] TASK-CI-002: Ensure `withTimeout` in `TestHelpers.fs` supports generous timeout limits.
- [x] TASK-CI-003: Update `timeout-minutes` in `.github/workflows/ci.yml` from 15 to 20 for `build-and-test`.
- [x] TASK-CI-004: Validate locally and in Linux container, commit, push branch, and create PR.
