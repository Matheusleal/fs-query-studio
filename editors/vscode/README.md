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
npm test               # unit tests (vitest); worker handshake runs if dist/worker exists
npm run test:vscode    # extension-host tests inside a real downloaded VS Code
npm run test:all       # both of the above
```

`test:vscode` downloads a VS Code build on first run, loads this folder as a
development extension, and drives the `fs-query-studio.checkWorker` command
end to end (including spawning the published worker). Needs `dist/worker`
present and `dotnet` on `PATH`; on Linux it needs a display, so CI runs it
under `xvfb-run`.

Press F5 in VS Code to launch an Extension Development Host, then run the
**F# Query Studio: Check Worker** command from the palette.

Set `FSQS_WORKER_DLL` to point at a `FSharpQueryStudio.Worker.dll` built
elsewhere instead of publishing into `dist/worker`.
