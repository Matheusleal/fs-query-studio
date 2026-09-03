# F# Query Studio — VS Code extension

Cross-platform host for the F# Query Studio worker. See
`docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md` for the
migration design and `docs/superpowers/plans/2026-09-03-vscode-extension-f1-scaffold.md`
for the F1 phase.

## Development

Requirements: Node 20+, .NET 10 SDK.

```bash
cd editors/vscode
npm install

# Build the F# worker into the extension (from the repo root):
dotnet publish ../../src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj \
  -c Release -o dist/worker

npm run compile        # type-check + bundle to dist/extension.js
npm test               # unit tests (vitest); integration test runs if dist/worker exists
```

Press F5 in VS Code to launch an Extension Development Host, then run the
**F# Query Studio: Check Worker** command from the palette.

Set `FSQS_WORKER_DLL` to point at a `FSharpQueryStudio.Worker.dll` built
elsewhere instead of publishing into `dist/worker`.
