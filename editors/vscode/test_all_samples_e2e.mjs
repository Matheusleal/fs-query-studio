import * as path from "node:path";
import * as fs from "node:fs";
import * as os from "node:os";
import { spawn } from "node:child_process";
import * as readline from "node:readline";

const defaultDir = fs.existsSync("C:\\Users\\theus\\Downloads\\fs-qs")
  ? "C:\\Users\\theus\\Downloads\\fs-qs"
  : fs.mkdtempSync(path.join(os.tmpdir(), "fsqs-e2e-"));
const wsDir = process.env.FSQS_TEST_DIR || defaultDir;

function findWorkerDll() {
  const candidates = [
    path.resolve("editors/vscode/dist/worker/FSharpQueryStudio.Worker.dll"),
    path.resolve("dist/worker/FSharpQueryStudio.Worker.dll"),
    path.resolve("../../editors/vscode/dist/worker/FSharpQueryStudio.Worker.dll"),
    path.resolve("src/FSharpQueryStudio.Worker/bin/Release/net10.0/FSharpQueryStudio.Worker.dll"),
    path.resolve("../../src/FSharpQueryStudio.Worker/bin/Release/net10.0/FSharpQueryStudio.Worker.dll"),
  ];
  for (const c of candidates) {
    if (fs.existsSync(c)) return c;
  }
  throw new Error("FSharpQueryStudio.Worker.dll not found in candidate paths.");
}

const workerDll = findWorkerDll();

console.log("Starting worker:", workerDll);
const proc = spawn("dotnet", [workerDll], {
  cwd: wsDir,
  stdio: ["pipe", "pipe", "pipe"],
});

proc.stderr.on("data", (data) => {
  // console.error("[worker stderr]", data.toString());
});

let reqId = 1;
const pending = new Map();

const rl = readline.createInterface({ input: proc.stdout });
rl.on("line", (line) => {
  if (!line.trim()) return;
  try {
    const msg = JSON.parse(line);
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(msg.error);
      else resolve(msg.result);
    }
  } catch (err) {
    console.error("JSON parse error:", err, line);
  }
});

function send(method, params) {
  const id = `req-${reqId++}`;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    const payload = JSON.stringify({ id, method, params }) + "\n";
    proc.stdin.write(payload);
  });
}

async function run() {
  try {
    const genPath = path.join(wsDir, ".fsqs", "generated", "GeneratedEntities.fs");
    if (fs.existsSync(genPath)) {
      try { fs.unlinkSync(genPath); } catch {}
    }

    console.log("Generating sample projects & notebooks in target directory:", wsDir);
    // 1. Ensure basic sample data (sample_demo & sample_demo_pt)
    const sampleRes = await send("workspace/createSampleData", {
      targetDir: wsDir,
      targetDirectory: wsDir,
      language: "all",
    });
    console.log("Standard sample data initialized.");

    // 2. Ensure advanced database samples (EN and PT for all engines)
    const advRes = await send("workspace/createAdvancedSamples", {
      targetDir: wsDir,
      targetDirectory: wsDir,
      engines: ["all"],
      language: "all",
    });
    console.log("Advanced samples initialized.");

    const notebooks = [
      // English notebooks
      "sample_demo.fsqnb",
      "sample_mysql.fsqnb",
      "sample_mariadb.fsqnb",
      "sample_oracle.fsqnb",
      "sample_postgresql.fsqnb",
      "sample_sqlserver.fsqnb",
      // Portuguese notebooks
      "sample_demo_pt.fsqnb",
      "sample_mysql_pt.fsqnb",
      "sample_mariadb_pt.fsqnb",
      "sample_oracle_pt.fsqnb",
      "sample_postgresql_pt.fsqnb",
      "sample_sqlserver_pt.fsqnb",
    ];

    let totalPassed = 0;
    let totalFailed = 0;
    const failures = [];

    for (const nb of notebooks) {
      console.log(`\n========================================`);
      console.log(`Testing notebook: ${nb}`);
      console.log(`========================================`);
      const nbPath = path.join(wsDir, nb);
      if (!fs.existsSync(nbPath)) {
        console.error("File does not exist:", nbPath);
        failures.push(`${nb}: File not found`);
        totalFailed++;
        continue;
      }
      const nbData = JSON.parse(fs.readFileSync(nbPath, "utf-8"));
      const codeCells = nbData.cells.filter((c) => c.kind === "code");

      const normalizedWs = wsDir.replace(/\\/g, "/").replace(":", "%3A");
      const docUri = `file:///${normalizedWs}/${nb}`;

      for (let i = 0; i < codeCells.length; i++) {
        const cell = codeCells[i];
        try {
          const evalRes = await send("execution/evaluate", {
            source: cell.value,
            sessionId: docUri,
            mode: { Case: "Persistent" },
            documentUri: docUri,
            isNotebookCell: true,
          });

          const kind = evalRes?.kind?.Case || evalRes?.kind || "Unknown";
          const isError = kind === "Error" || evalRes?.error != null;
          if (isError) {
            console.error(`  [FAIL] Cell ${i + 1}:`, evalRes?.error || evalRes?.displayText || evalRes);
            failures.push(`${nb} - Cell ${i + 1}: ${evalRes?.displayText || JSON.stringify(evalRes?.error)}`);
            totalFailed++;
          } else {
            console.log(`  [PASS] Cell ${i + 1} (${kind})`);
            totalPassed++;
          }
        } catch (cellErr) {
          console.error(`  [FAIL] Cell ${i + 1} exception:`, cellErr);
          failures.push(`${nb} - Cell ${i + 1} exception: ${cellErr?.message || cellErr}`);
          totalFailed++;
        }
      }
    }

    console.log(`\n========================================`);
    console.log(`FINAL RESULTS: Passed=${totalPassed}, Failed=${totalFailed}`);
    if (failures.length > 0) {
      console.log(`FAILURES:`);
      failures.forEach((f) => console.log(`  * ${f}`));
    }
    console.log(`========================================`);

    proc.kill();
    process.exit(totalFailed > 0 ? 1 : 0);
  } catch (err) {
    console.error("Fatal error:", err);
    proc.kill();
    process.exit(1);
  }
}

run();
