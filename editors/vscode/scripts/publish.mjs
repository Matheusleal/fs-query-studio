// Publish the extension to the Visual Studio Marketplace.
//
//   node scripts/publish.mjs [--dry-run] [--allow-dirty]
//
// Preflight refuses unless every condition holds:
//   - `npm run doctor` has no hard failure (toolchain, EXTENSION_ID, icon)
//   - the git working tree is clean            (override: --allow-dirty)
//   - package.json "version" matches a `vscode-v<version>` tag on HEAD
//   - CHANGELOG.md has a `## <version>` heading
//   - VSCE_PAT is set in the environment
//
// This script never writes the version or the tag — that stays a manual,
// reviewed step (see docs/vscode-extension-release.md).

import { pathToFileURL } from "node:url";
import { runDoctor, printReport } from "./doctor.mjs";
import { packageVsix } from "./package.mjs";
import {
  parseArgs,
  run,
  runCapture,
  readExtManifest,
  readFileSync,
  existsSync,
  join,
  extDir,
  repoRoot,
} from "./lib.mjs";

function gitLines(args) {
  return runCapture("git", args, { cwd: repoRoot })
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter(Boolean);
}

/**
 * @param {{ allowDirty?: boolean }} [opts]
 * @returns {{ version: string, blockers: string[] }}
 */
export function preflight(opts = {}) {
  const blockers = [];
  const manifest = readExtManifest();
  const version = manifest.version;

  const doctor = runDoctor();
  printReport(doctor);
  for (const r of doctor.filter((r) => !r.ok && r.hard)) {
    blockers.push(`doctor: ${r.name} (${r.detail})`);
  }

  if (!opts.allowDirty) {
    const dirty = gitLines(["status", "--porcelain"]);
    if (dirty.length) {
      blockers.push(
        `working tree not clean (${dirty.length} change(s)); commit or pass --allow-dirty`,
      );
    }
  }

  const tags = gitLines(["tag", "--points-at", "HEAD"]);
  const wantTag = `vscode-v${version}`;
  if (!tags.includes(wantTag)) {
    blockers.push(
      `HEAD is not tagged ${wantTag} (tags here: ${tags.join(", ") || "none"})`,
    );
  }

  const changelogPath = join(extDir, "CHANGELOG.md");
  if (!existsSync(changelogPath)) {
    blockers.push("CHANGELOG.md missing");
  } else {
    const escaped = version.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    if (!new RegExp(`^##\\s*\\[?${escaped}\\b`, "m").test(readFileSync(changelogPath, "utf8"))) {
      blockers.push(`CHANGELOG.md has no "## ${version}" heading`);
    }
  }

  if (!process.env.VSCE_PAT) {
    blockers.push("VSCE_PAT not set in the environment");
  }

  return { version, blockers };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const { flags } = parseArgs();
  const dryRun = flags.has("dry-run");
  try {
    const { version, blockers } = preflight({ allowDirty: flags.has("allow-dirty") });

    if (blockers.length) {
      console.error(`\x1b[31m✗ ${blockers.length} publish blocker(s):\x1b[0m`);
      for (const b of blockers) console.error(`  - ${b}`);
      process.exit(1);
    }
    console.log(`\x1b[32m✓\x1b[0m preflight clean for v${version}`);

    const out = packageVsix();

    if (dryRun) {
      console.log(`\x1b[33m· --dry-run:\x1b[0m would now run \`vsce publish --packagePath ${out}\``);
      process.exit(0);
    }

    run("npx", ["--yes", "@vscode/vsce", "publish", "--packagePath", out], { cwd: extDir });
    console.log(`\x1b[32m✓\x1b[0m published v${version}`);
  } catch (err) {
    console.error(`\x1b[31m✗\x1b[0m ${err.message}`);
    process.exit(1);
  }
}
