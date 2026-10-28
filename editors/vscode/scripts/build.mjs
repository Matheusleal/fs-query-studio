// Build a runnable extension: publish the F# worker, then type-check +
// bundle the TypeScript. This is what `vscode:prepublish` runs, so a bare
// `vsce package` can never ship a stale bundle or a missing worker.
//
//   node scripts/build.mjs [--skip-worker] [--worker-only] [--configuration Release|Debug]

import { existsSync, copyFileSync, readdirSync, statSync, unlinkSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { parseArgs, run, extDir, repoRoot, workerOutDir, workerProject } from "./lib.mjs";

/**
 * Recursively remove .pdb and unnecessary .xml doc files from worker publish directory
 * to prevent leaking symbols, local machine paths, or dev documentation in the VSIX.
 * @param {string} dir
 * @returns {number} number of files deleted
 */
export function sanitizeWorkerDirectory(dir) {
  if (!existsSync(dir)) return 0;
  let removed = 0;
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const stat = statSync(full);
    if (stat.isDirectory()) {
      removed += sanitizeWorkerDirectory(full);
    } else if (entry.endsWith(".pdb") || (entry.endsWith(".xml") && !entry.startsWith("Microsoft.Data.SqlClient"))) {
      unlinkSync(full);
      removed++;
    }
  }
  return removed;
}

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
    const cleaned = sanitizeWorkerDirectory(workerOutDir);
    if (cleaned > 0) {
      console.log(`\x1b[2m· sanitized ${cleaned} debug symbol / doc file(s) in reused worker directory\x1b[0m`);
    }
    console.log(`\x1b[2m· skipping worker publish (reusing ${workerOutDir})\x1b[0m`);
  } else {
    const dotnetArgs = [
      "publish",
      workerProject,
      "-c",
      configuration,
      "-o",
      workerOutDir,
    ];
    if (configuration === "Release") {
      dotnetArgs.push("-p:DebugType=None", "-p:DebugSymbols=false");
    }
    run("dotnet", dotnetArgs, { cwd: repoRoot });
    const cleaned = sanitizeWorkerDirectory(workerOutDir);
    if (cleaned > 0) {
      console.log(`\x1b[2m· stripped ${cleaned} debug symbol / doc file(s) from worker output\x1b[0m`);
    }
  }

  if (opts.workerOnly) {
    console.log("\x1b[32m✓\x1b[0m worker published");
    return;
  }

  const benchmarkSrc = join(repoRoot, "docs", "BENCHMARK_REPORT.md");
  const benchmarkDest = join(extDir, "BENCHMARK_REPORT.md");
  if (existsSync(benchmarkSrc)) {
    copyFileSync(benchmarkSrc, benchmarkDest);
  }

  run("npm", ["run", "compile"], { cwd: extDir });
  console.log("\x1b[32m✓\x1b[0m build complete");
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
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
