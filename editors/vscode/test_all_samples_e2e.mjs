import * as path from "node:path";
import * as fs from "node:fs";
import * as os from "node:os";
import { spawn, execSync } from "node:child_process";
import * as readline from "node:readline";

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

function getContainerStatus(containerName) {
  try {
    const res = execSync(
      `docker inspect --format "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}" ${containerName}`,
      {
        stdio: ["pipe", "pipe", "ignore"],
        encoding: "utf-8",
      }
    ).trim();
    return res;
  } catch {
    return "not_found";
  }
}

function isSqlServerReady() {
  try {
    const logs = execSync("docker logs fsqs-sqlserver", {
      stdio: ["pipe", "pipe", "ignore"],
      encoding: "utf-8",
    });
    return logs.includes("SQL Server init script executed successfully");
  } catch {
    return false;
  }
}

function isMySqlReady() {
  try {
    const res = execSync('docker exec fsqs-mysql mysql -u root -pPassword123! -e "SELECT 1" fsqs_demo', {
      stdio: ["pipe", "pipe", "ignore"],
      encoding: "utf-8",
    });
    return res.includes("1");
  } catch {
    return false;
  }
}

async function ensureDatabasesReady(wsDir) {
  const composePath = path.join(wsDir, "docker-compose.fsqs.yml");
  if (!fs.existsSync(composePath)) {
    throw new Error(`docker-compose.fsqs.yml not found in ${wsDir}`);
  }

  const required = [
    "fsqs-postgres",
    "fsqs-mysql",
    "fsqs-mariadb",
    "fsqs-oracle",
    "fsqs-sqlserver",
  ];

  let needsUp = false;
  for (const c of required) {
    const status = getContainerStatus(c);
    if (status !== "healthy" && status !== "running") {
      needsUp = true;
      break;
    }
  }

  if (needsUp) {
    console.log("Starting database containers via docker compose...");
    execSync(`docker compose -f "${composePath}" up -d`, {
      stdio: "inherit",
      cwd: wsDir,
    });
  }

  console.log("Waiting for database containers to become healthy and ready...");
  const maxWaitMs = 180000; // up to 3 minutes for cold container boots
  const startTime = Date.now();

  while (Date.now() - startTime < maxWaitMs) {
    const pg = getContainerStatus("fsqs-postgres");
    const my = getContainerStatus("fsqs-mysql");
    const ma = getContainerStatus("fsqs-mariadb");
    const ora = getContainerStatus("fsqs-oracle");
    const sql = isSqlServerReady();

    const pgOk = pg === "healthy";
    const myOk = (my === "healthy" || my === "running") && isMySqlReady();
    const maOk = ma === "healthy";
    const oraOk = ora === "healthy";

    if (pgOk && myOk && maOk && oraOk && sql) {
      console.log("All 5 database containers are healthy and ready!");
      break;
    }

    console.log(
      `Waiting for DBs: postgres=${pg}, mysql=${my}, mariadb=${ma}, oracle=${ora}, sqlserver=${sql ? "ready" : "booting"}...`
    );
    await new Promise((r) => setTimeout(r, 4000));
  }

  // Ensure Oracle FREEPDB1 has all tables initialized
  const oraScript = path.join(wsDir, "init-scripts", "init_oracle.sql");
  if (fs.existsSync(oraScript)) {
    console.log("Ensuring Oracle FREEPDB1 schema is applied...");
    try {
      const sqlContent = fs.readFileSync(oraScript, "utf-8");
      const p = spawn(
        "docker",
        ["exec", "-i", "fsqs-oracle", "sqlplus", "-S", "system/Password123!@//localhost:1521/FREEPDB1"],
        { stdio: ["pipe", "pipe", "pipe"] }
      );
      p.stdin.write(sqlContent);
      p.stdin.end();
      await new Promise((resolve) => p.on("close", resolve));
      console.log("Oracle schema applied to FREEPDB1.");
    } catch (e) {
      console.warn("Oracle init script application warning:", e.message);
    }
  }
}

async function removeDirWithRetry(dir, maxRetries = 10, delayMs = 1000) {
  for (let i = 0; i < maxRetries; i++) {
    try {
      if (fs.existsSync(dir)) {
        fs.rmSync(dir, { recursive: true, force: true });
      }
      return;
    } catch (err) {
      if (i === maxRetries - 1) {
        console.warn(`Could not completely remove ${dir}: ${err.message}`);
        return;
      }
      await new Promise((r) => setTimeout(r, delayMs));
    }
  }
}

async function run() {
  const wsDir = process.env.FSQS_TEST_DIR || fs.mkdtempSync(path.join(os.tmpdir(), "fsqs-samples-e2e-"));
  const workerDll = findWorkerDll();

  console.log("========================================");
  console.log("F# Query Studio - E2E Samples Test Suite");
  console.log(`Base Directory: ${wsDir}`);
  console.log(`Worker DLL:     ${workerDll}`);
  console.log("========================================");

  const proc = spawn("dotnet", [workerDll], {
    cwd: wsDir,
    stdio: ["pipe", "pipe", "pipe"],
  });

  proc.stderr.on("data", () => {});

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

  let totalPassed = 0;
  let totalFailed = 0;
  const failures = [];

  try {
    console.log("\n1. Initializing standard sample data (SQLite, CSV, Excel, XML, JSON, REST)...");
    await send("workspace/createSampleData", {
      targetDir: wsDir,
      targetDirectory: wsDir,
      language: "all",
    });
    console.log("Standard sample data initialized.");

    console.log("\n2. Initializing advanced database samples and docker compose scripts...");
    await send("workspace/createAdvancedSamples", {
      targetDir: wsDir,
      targetDirectory: wsDir,
      engines: ["all"],
      language: "all",
    });
    console.log("Advanced sample templates and configs generated.");

    console.log("\n3. Verifying and launching database containers...");
    await ensureDatabasesReady(wsDir);

    console.log("\n4. Re-synchronizing schema discovery with running databases...");
    const genPath = path.join(wsDir, ".fsqs", "generated", "GeneratedEntities.fs");
    if (fs.existsSync(genPath)) {
      try { fs.unlinkSync(genPath); } catch {}
    }
    await send("workspace/createAdvancedSamples", {
      targetDir: wsDir,
      targetDirectory: wsDir,
      engines: ["all"],
      language: "all",
    });
    console.log("Schema discovery complete. GeneratedEntities.fs generated.");

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
  } catch (err) {
    console.error("Fatal test execution error:", err);
    totalFailed++;
  } finally {
    // 1. Terminate worker process
    try {
      if (proc && !proc.killed) {
        proc.kill();
      }
    } catch {}

    // Wait a brief moment for worker process exit
    await new Promise((r) => setTimeout(r, 1000));

    // 2. Remove database containers and volumes
    console.log("\n========================================");
    console.log("Cleaning up database containers...");
    console.log("========================================");
    const composePath = path.join(wsDir, "docker-compose.fsqs.yml");
    if (fs.existsSync(composePath)) {
      try {
        execSync(`docker compose -f "${composePath}" down -v --remove-orphans`, {
          stdio: "inherit",
          cwd: wsDir,
        });
      } catch (err) {
        console.warn("docker compose down warning:", err.message);
      }
    }
    try {
      execSync("docker rm -f fsqs-postgres fsqs-sqlserver fsqs-mysql fsqs-mariadb fsqs-oracle", {
        stdio: "ignore",
      });
    } catch {}
    console.log("Database containers removed.");

    // 3. Remove temporary test directory
    if (!process.env.FSQS_KEEP_TEMP) {
      console.log(`\nRemoving temporary test directory: ${wsDir}`);
      await removeDirWithRetry(wsDir);
      console.log("Temporary test directory removed.");
    }

    process.exit(totalFailed > 0 ? 1 : 0);
  }
}

run();
