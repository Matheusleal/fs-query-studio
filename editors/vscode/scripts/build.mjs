// Build a runnable extension: publish the F# worker, then type-check +
// bundle the TypeScript. This is what `vscode:prepublish` runs, so a bare
// `vsce package` can never ship a stale bundle or a missing worker.
//
//   node scripts/build.mjs [--skip-worker] [--worker-only] [--configuration Release|Debug]

import { existsSync, copyFileSync, mkdirSync, readFileSync, readdirSync, statSync, unlinkSync, writeFileSync } from "node:fs";
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
 * Synchronize and ensure all required documentation files are available
 * for VS Code extension packaging and runtime.
 * @param {string} extDir
 * @param {string} repoRoot
 */
export function ensureDocsAvailable(extDir, repoRoot) {
  const docsDir = join(extDir, "docs");
  if (!existsSync(docsDir)) {
    mkdirSync(docsDir, { recursive: true });
  }

  // 1. Sync canonical docs from repository root into editors/vscode/docs/
  const syncFiles = [
    "BENCHMARK_REPORT.md",
    "BENCHMARK_REPORT_PT.md",
    "USER_GUIDE.md",
    "USER_GUIDE_PT.md",
    "CHANGELOG.md",
    "CHANGELOG_PT.md",
  ];
  for (const file of syncFiles) {
    const src = join(repoRoot, "docs", file);
    const dest = join(docsDir, file);
    if (existsSync(src)) {
      copyFileSync(src, dest);
    }
  }

  // 2. Ensure root documentation required by vsce package & marketplace is available in extDir
  const docsReadme = join(docsDir, "README.md");
  const rootReadme = join(extDir, "README.md");
  if (existsSync(docsReadme)) {
    let content = readFileSync(docsReadme, "utf8");
    // Rewrite sibling links in the root README copy so they point into docs/
    content = content
      .replaceAll("(USER_GUIDE.md", "(docs/USER_GUIDE.md")
      .replaceAll("(USER_GUIDE_PT.md", "(docs/USER_GUIDE_PT.md")
      .replaceAll("(BENCHMARK_REPORT.md", "(docs/BENCHMARK_REPORT.md")
      .replaceAll("(BENCHMARK_REPORT_PT.md", "(docs/BENCHMARK_REPORT_PT.md")
      .replaceAll("(README_PT.md", "(docs/README_PT.md");
    writeFileSync(rootReadme, content, "utf8");
  }

  const docsChangelog = join(docsDir, "CHANGELOG.md");
  const rootChangelog = join(extDir, "CHANGELOG.md");
  if (existsSync(docsChangelog)) {
    let content = readFileSync(docsChangelog, "utf8");
    content = content.replaceAll("(CHANGELOG_PT.md", "(docs/CHANGELOG_PT.md");
    writeFileSync(rootChangelog, content, "utf8");
  }
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

  ensureDocsAvailable(extDir, repoRoot);

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
