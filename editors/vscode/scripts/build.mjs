// Build a runnable extension: publish the F# worker, then type-check +
// bundle the TypeScript. This is what `vscode:prepublish` runs, so a bare
// `vsce package` can never ship a stale bundle or a missing worker.
//
//   node scripts/build.mjs [--skip-worker] [--worker-only] [--configuration Release|Debug]

import { existsSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { parseArgs, run, extDir, repoRoot, workerOutDir, workerProject } from "./lib.mjs";

/**
 * @param {{ skipWorker?: boolean, workerOnly?: boolean, configuration?: string }} [opts]
 */
export function build(opts = {}) {
  const configuration = opts.configuration ?? "Release";

  if (opts.skipWorker) {
    if (!existsSync(workerOutDir)) {
      throw new Error(
        `--skip-worker given but ${workerOutDir} does not exist; run without it once.`,
      );
    }
    console.log(`\x1b[2m· skipping worker publish (reusing ${workerOutDir})\x1b[0m`);
  } else {
    run(
      "dotnet",
      ["publish", workerProject, "-c", configuration, "-o", workerOutDir],
      { cwd: repoRoot },
    );
  }

  if (opts.workerOnly) {
    console.log("\x1b[32m✓\x1b[0m worker published");
    return;
  }

  run("npm", ["run", "compile"], { cwd: extDir });
  console.log("\x1b[32m✓\x1b[0m build complete");
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const { flags, values } = parseArgs();
  try {
    build({
      skipWorker: flags.has("skip-worker"),
      workerOnly: flags.has("worker-only"),
      configuration: values.configuration,
    });
  } catch (err) {
    console.error(`\x1b[31m✗\x1b[0m ${err.message}`);
    process.exit(1);
  }
}
