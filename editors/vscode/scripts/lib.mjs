// Shared helpers for the F# Query Studio build / package / publish scripts.
//
// These scripts are plain Node ESM driven by npm scripts so the same code
// path runs on a Windows dev box, on macOS / Linux, and in CI — no new
// runtime, no new dependency. `@vscode/vsce` is invoked via `npx --yes`,
// matching the CI workflow (it is intentionally not a devDependency).

import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptsDir = dirname(fileURLToPath(import.meta.url));

/** `editors/vscode/` */
export const extDir = resolve(scriptsDir, "..");
/** repository root */
export const repoRoot = resolve(extDir, "..", "..");
/** `dotnet publish` output that gets packed into the VSIX */
export const workerOutDir = join(extDir, "dist", "worker");
export const workerProject = join(
  repoRoot,
  "src",
  "FSharpQueryStudio.Worker",
  "FSharpQueryStudio.Worker.fsproj",
);
export const defaultVsixName = "fs-query-studio.vsix";

// On Windows `npm` / `npx` are `.cmd` shims. Since Node 20.12 a `.cmd`
// cannot be spawned without a shell (EINVAL), so those go through
// `shell: true`; real .exe (`dotnet`, `git`) do not, avoiding DEP0190 and
// the unescaped-args hazard for the paths we pass. With a shell, quote any
// arg that carries whitespace.
const isWin = process.platform === "win32";
const winShims = new Set(["npm", "npx", "node"]);
const identity = (a) => a;
const quoteForShell = (a) => (/\s/.test(a) ? `"${a}"` : a);

/** Decide shell usage + per-arg transform for a given command. */
function spawnOpts(cmd) {
  const shell = isWin && winShims.has(cmd);
  return { shell, args: shell ? quoteForShell : identity };
}

/**
 * Run a command with inherited stdio. Throws if it exits non-zero.
 * @param {string} cmd
 * @param {string[]} args
 * @param {{cwd?: string, env?: NodeJS.ProcessEnv}} [opts]
 */
export function run(cmd, args, opts = {}) {
  const printable = `${cmd} ${args.join(" ")}`.trim();
  console.log(`\x1b[2m$ ${printable}\x1b[0m`);
  const { shell, args: xf } = spawnOpts(cmd);
  const res = spawnSync(cmd, args.map(xf), {
    stdio: "inherit",
    shell,
    cwd: opts.cwd ?? extDir,
    env: opts.env ?? process.env,
  });
  if (res.error) throw res.error;
  if (res.status !== 0) {
    throw new Error(`\`${printable}\` exited with code ${res.status}`);
  }
}

/**
 * Run a command, echo its output live-ish, and also return it (stdout+stderr
 * combined) for inspection. Throws if it exits non-zero.
 * @param {string} cmd
 * @param {string[]} args
 * @param {{cwd?: string, env?: NodeJS.ProcessEnv}} [opts]
 * @returns {string}
 */
export function runCapture(cmd, args, opts = {}) {
  const printable = `${cmd} ${args.join(" ")}`.trim();
  console.log(`\x1b[2m$ ${printable}\x1b[0m`);
  const { shell, args: xf } = spawnOpts(cmd);
  const res = spawnSync(cmd, args.map(xf), {
    encoding: "utf8",
    shell,
    cwd: opts.cwd ?? extDir,
    env: opts.env ?? process.env,
    maxBuffer: 32 * 1024 * 1024,
  });
  if (res.error) throw res.error;
  const output = `${res.stdout ?? ""}${res.stderr ?? ""}`;
  if (output.trim()) process.stdout.write(output.endsWith("\n") ? output : output + "\n");
  if (res.status !== 0) {
    throw new Error(`\`${printable}\` exited with code ${res.status}`);
  }
  return output;
}

/** Minimal flag parsing over an argv slice. */
export function parseArgs(argv = process.argv.slice(2)) {
  const flags = new Set();
  const values = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith("--")) continue;
    const eq = a.indexOf("=");
    if (eq !== -1) {
      values[a.slice(2, eq)] = a.slice(eq + 1);
    } else if (i + 1 < argv.length && !argv[i + 1].startsWith("--")) {
      values[a.slice(2)] = argv[++i];
    } else {
      flags.add(a.slice(2));
    }
  }
  return { flags, values };
}

/** Files every VSIX must contain — kept in lockstep with the release doc. */
export const requiredVsixFiles = [
  "package.json",
  "README.md",
  "LICENSE",
  "dist/extension.js",
  "dist/worker/FSharpQueryStudio.Worker.dll",
  "media/renderer/index.js",
];

/** Security rules declaring files and directories that must NEVER appear in the VSIX package. */
export const forbiddenVsixRules = [
  {
    category: "debug symbols & source maps",
    test: (f) => /\.(pdb|map)$/i.test(f),
  },
  {
    category: "uncompiled source code files",
    test: (f) =>
      /\.(fs|fsi|cs|ts|tsx)$/i.test(f) ||
      (/\.js$/i.test(f) && !["dist/extension.js", "media/renderer/index.js"].includes(f)),
  },
  {
    category: "secrets, environment configs & private keys",
    test: (f) =>
      /(^|\/)\.env/i.test(f) ||
      /\.(key|pem|cert|crt|pfx|p12|publishsettings)$/i.test(f) ||
      /(^|\/)(secret|credential|token|password)/i.test(f),
  },
  {
    category: "local databases, notebooks & data dumps",
    test: (f) => /\.(fsqnb|db|sqlite|sqlite3|dump|bak|log|tmp|csv)$/i.test(f),
  },
  {
    category: "source, test & build directories",
    test: (f) => /(^|\/)(src|test|out|node_modules)(\/|$)/i.test(f),
  },
  {
    category: "vcs, ide & agent metadata",
    test: (f) =>
      /(^|\/)(\.git|\.github|\.githooks|\.agents|\.claude|\.superpowers|\.vscode|\.vs|\.idea)(\/|$)/i.test(
        f,
      ),
  },
  {
    category: "project, solution & test config files",
    test: (f) =>
      /\.(fsproj|csproj|sln|slnx|props|targets)$/i.test(f) ||
      /(^|\/)tsconfig.*\.json$/i.test(f) ||
      /(^|\/)(esbuild\.mjs|vitest\.config\.[jt]s)$/i.test(f) ||
      /\.(test|spec)\.[a-z0-9]+$/i.test(f),
  },
  {
    category: "nested package archives",
    test: (f) => /\.vsix$/i.test(f),
  },
];

/**
 * Assert a `vsce ls` listing contains every required file and no source,
 * test, debug, secret, database, or build junk. Port and expansion of the
 * CI assertions to guarantee complete packaging security.
 * @param {string} listing newline-separated output of `vsce ls`
 */
export function assertVsixContents(listing) {
  const lines = listing
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter(Boolean);
  const has = (f) => lines.includes(f);
  const problems = [];

  for (const required of requiredVsixFiles) {
    if (!has(required)) problems.push(`missing required file: ${required}`);
  }

  for (const rule of forbiddenVsixRules) {
    const offenders = lines.filter(rule.test);
    if (offenders.length > 0) {
      const sample = offenders.slice(0, 3).join(", ");
      const more = offenders.length > 3 ? ` (+${offenders.length - 3} more)` : "";
      problems.push(`contains ${rule.category}: ${sample}${more}`);
    }
  }

  if (problems.length) {
    throw new Error(`VSIX contents security check failed:\n  - ${problems.join("\n  - ")}`);
  }
  console.log(`\x1b[32m✓\x1b[0m VSIX contents OK (${lines.length} files, zero security leaks)`);
}

/** Read + parse `editors/vscode/package.json`. */
export function readExtManifest() {
  return JSON.parse(readFileSync(join(extDir, "package.json"), "utf8"));
}

export const paths = { extDir, repoRoot, workerOutDir, workerProject };
export { existsSync, readFileSync, join };
