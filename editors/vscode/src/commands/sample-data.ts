import * as vscode from "vscode";
import { randomUUID } from "node:crypto";
import type { WorkerResponseFrame } from "../protocol";

export type WorkerLike = {
  send: (req: { id: string; method: string; params: unknown }) => Promise<WorkerResponseFrame>;
};

export interface SampleDataDeps {
  getWorker: () => Promise<WorkerLike>;
  log: (line: string) => void;
  startDataSourcesWatcher?: () => void;
  runSyncDataSources?: (targetDir?: string, silent?: boolean) => Promise<void>;
  outputChannel?: vscode.OutputChannel;
}

export interface DemoOption extends vscode.QuickPickItem {
  id: "standard" | "postgresql" | "sqlserver" | "mysql" | "mariadb" | "oracle";
}

export interface LanguageOption extends vscode.QuickPickItem {
  lang?: "en" | "pt" | "all";
}

export function getDemoOptions(isPt: boolean): DemoOption[] {
  return [
    {
      label: "$(database) Standard Multi-Source Demo (SQLite, CSV, Excel & REST)",
      description: isPt
        ? "Demonstração completa em inglês com todas as fontes de dados originais e sample_demo.fsqnb"
        : "Complete demo in English with all original data sources and sample_demo.fsqnb",
      id: "standard",
      picked: true,
    },
    {
      label: "$(server) PostgreSQL Advanced Demo (Docker & FSQL)",
      description: isPt
        ? "Container Docker postgres:16-alpine (JSONB, CTEs recursivas, LATERAL e sample_postgresql.fsqnb)"
        : "Docker container postgres:16-alpine (JSONB, recursive CTEs, LATERAL and sample_postgresql.fsqnb)",
      id: "postgresql",
      picked: false,
    },
    {
      label: "$(server-process) SQL Server Advanced Demo (Docker & FSQL)",
      description: isPt
        ? "Container Docker MSSQL 2022 (T-SQL, SHOWPLAN, CROSS APPLY, CTEs e sample_sqlserver.fsqnb)"
        : "Docker container MSSQL 2022 (T-SQL, SHOWPLAN, CROSS APPLY, CTEs and sample_sqlserver.fsqnb)",
      id: "sqlserver",
      picked: false,
    },
    {
      label: "$(database) MySQL Advanced Demo (Docker & FSQL)",
      description: isPt
        ? "Container Docker mysql:8.4 (JSON, CTEs, LATERAL e sample_mysql.fsqnb)"
        : "Docker container mysql:8.4 (JSON, CTEs, LATERAL and sample_mysql.fsqnb)",
      id: "mysql",
      picked: false,
    },
    {
      label: "$(database) MariaDB Advanced Demo (Docker & FSQL)",
      description: isPt
        ? "Container Docker mariadb:11.4 (JSON, CTEs, LATERAL e sample_mariadb.fsqnb)"
        : "Docker container mariadb:11.4 (JSON, CTEs, LATERAL and sample_mariadb.fsqnb)",
      id: "mariadb",
      picked: false,
    },
    {
      label: "$(database) Oracle Advanced Demo (Docker & FSQL)",
      description: isPt
        ? "Container Docker Oracle Free 23 (JSON_VALUE, CROSS APPLY, CTEs e sample_oracle.fsqnb)"
        : "Docker container Oracle Free 23 (JSON_VALUE, CROSS APPLY, CTEs and sample_oracle.fsqnb)",
      id: "oracle",
      picked: false,
    },
  ];
}

export function getLanguageOptions(isPt: boolean): LanguageOption[] {
  return [
    {
      label: "$(globe) English",
      description: isPt
        ? "Gerar cadernos de demonstração em inglês (*.fsqnb)"
        : "Generate demo notebooks in English (*.fsqnb)",
      lang: "en",
    },
    {
      label: "$(globe) Português (Brasil)",
      description: isPt
        ? "Gerar cadernos de demonstração em português (*_pt.fsqnb)"
        : "Generate demo notebooks in Portuguese (*_pt.fsqnb)",
      lang: "pt",
    },
    {
      label: "$(files) Both / Ambos",
      description: isPt
        ? "Gerar demonstrações em inglês e português"
        : "Generate demos in both English and Portuguese",
      lang: "all",
    },
  ];
}

export async function executeCreateSampleData(deps: SampleDataDeps): Promise<void> {
  let targetDir: string | undefined;
  const wsFolders = vscode.workspace.workspaceFolders;
  if (wsFolders && wsFolders.length === 1) {
    targetDir = wsFolders[0].uri.fsPath;
  } else if (wsFolders && wsFolders.length > 1) {
    const pick = await vscode.window.showQuickPick(
      wsFolders.map((f) => ({ label: f.name, description: f.uri.fsPath, uri: f.uri })),
      { placeHolder: "Select a workspace folder for sample demo" },
    );
    if (!pick) return;
    targetDir = pick.uri.fsPath;
  } else {
    const picked = typeof vscode.window.showOpenDialog === "function"
      ? await vscode.window.showOpenDialog({
          canSelectFiles: false,
          canSelectFolders: true,
          canSelectMany: false,
          openLabel: "Select Folder for Sample Demo",
        })
      : undefined;
    if (!picked || picked.length === 0) return;
    targetDir = picked[0].fsPath;
  }

  const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
  const demoOptions = getDemoOptions(isPt);

  const selected = await vscode.window.showQuickPick(demoOptions, {
    canPickMany: true,
    title: isPt
      ? "Selecione as Demonstrações do F# Query Studio"
      : "Select F# Query Studio Sample Demos",
    placeHolder: isPt
      ? "Escolha uma ou mais demonstrações para gerar arquivos, conexões e notebooks"
      : "Choose one or more sample demos to generate files, connections and notebooks",
  });

  if (!selected || selected.length === 0) {
    return;
  }

  const defaultLang = isPt ? "pt" : "en";
  const languageOptions = getLanguageOptions(isPt);

  const selectedLangItem = await vscode.window.showQuickPick(languageOptions, {
    title: isPt
      ? "Passo 2 de 2: Selecione o Idioma das Demonstrações (Opcional)"
      : "Step 2 of 2: Select Demo Language (Optional)",
    placeHolder: isPt
      ? "Escolha o idioma dos cadernos (pressione Esc para usar o padrão)"
      : "Choose notebook language (press Esc for default)",
  });

  const chosenLang = selectedLangItem ? selectedLangItem.lang : defaultLang;
  const langParam = chosenLang === "all" ? undefined : chosenLang;

  const hasStandard = selected.some((s) => s.id === "standard");
  const advancedEngines = selected
    .filter(
      (s) =>
        s.id === "postgresql" ||
        s.id === "sqlserver" ||
        s.id === "mysql" ||
        s.id === "mariadb" ||
        s.id === "oracle",
    )
    .map((s) => s.id);

  deps.log(
    `[Workspace] Creating demos (standard=${hasStandard}, advanced=[${advancedEngines.join(", ")}], lang=${chosenLang}) in directory: ${targetDir}`,
  );

  try {
    const openedNotebooks: string[] = [];

    if (hasStandard) {
      deps.log(`[Workspace] Creating standard multi-source sample data in directory: ${targetDir}`);
      const worker = await deps.getWorker();
      const res = await worker.send({
        id: randomUUID(),
        method: "workspace/createSampleData",
        params: {
          targetDir,
          targetDirectory: targetDir,
          ...(langParam ? { language: langParam } : {}),
        },
      });

      if (res.error) {
        deps.log(`[Workspace] Failed to create standard sample data: ${res.error.message}`);
        throw new Error(res.error.message);
      }

      const data = res.result as {
        sqlitePath: string;
        sqlite2Path?: string;
        csvPath: string;
        csv2Path?: string;
        excelPath?: string;
        excel2Path?: string;
        notebookPath: string;
        notebookPtPath?: string;
      };

      const primaryNotebook =
        chosenLang === "pt" && data.notebookPtPath
          ? data.notebookPtPath
          : data.notebookPath;
      deps.log(`[Workspace] Standard sample data created: Notebook=${primaryNotebook}`);
      openedNotebooks.push(primaryNotebook);
      deps.startDataSourcesWatcher?.();
      void deps.runSyncDataSources?.(targetDir, true);
    }

    if (advancedEngines.length > 0) {
      deps.log(
        `[Workspace] Creating advanced database samples for [${advancedEngines.join(", ")}] in directory: ${targetDir}`,
      );
      const worker = await deps.getWorker();
      const res = await worker.send({
        id: randomUUID(),
        method: "workspace/createAdvancedSamples",
        params: {
          targetDir,
          targetDirectory: targetDir,
          engines: advancedEngines,
          selectedEngines: advancedEngines,
          ...(langParam ? { language: langParam } : {}),
        },
      });

      if (res.error) {
        deps.log(`[Workspace] Failed to create advanced database samples: ${res.error.message}`);
        throw new Error(res.error.message);
      }

      const data = res.result as {
        targetDir: string;
        selectedEngines: string[];
        dockerComposePath?: string;
        initScripts: string[];
        notebooksGenerated: string[];
        registeredConnections: string[];
      };

      deps.log(
        `[Workspace] Advanced database samples generated: Notebooks=${data.notebooksGenerated.join(", ")}`,
      );
      if (data.notebooksGenerated) {
        openedNotebooks.push(...data.notebooksGenerated);
      }

      if (data.dockerComposePath) {
        const composeName = data.dockerComposePath.split(/[\\/]/).pop();
        const dockerNotice = isPt
          ? `Para iniciar os bancos no Docker: 'docker compose -f ${composeName} up -d'.`
          : `To start containers with Docker: 'docker compose -f ${composeName} up -d'.`;
        void vscode.window.showInformationMessage(dockerNotice);
      }
    }

    await vscode.commands.executeCommand("fs-query-studio.connections.refresh");

    if (openedNotebooks.length > 0) {
      try {
        const doc = await vscode.workspace.openNotebookDocument(
          vscode.Uri.file(openedNotebooks[0]),
        );
        await vscode.window.showNotebookDocument(doc);
      } catch {
        // ignore error if notebook viewer fails to open immediately
      }
    }

    const successMsg = isPt
      ? "Demonstrações e conexões criadas com sucesso!"
      : "Sample demos and connections created successfully!";
    void vscode.window.showInformationMessage(successMsg);
  } catch (err) {
    const msg = (err as Error).message;
    deps.log(`[Workspace] Error creating sample demos: ${msg}`);
    deps.outputChannel?.show(true);
    const choice = await vscode.window.showErrorMessage(
      `Error creating sample demos: ${msg}`,
      "Show Logs",
    );
    if (choice === "Show Logs") {
      if (deps.outputChannel) {
        deps.outputChannel.show(true);
      } else {
        void vscode.commands.executeCommand("fs-query-studio.showOutput");
      }
    }
  }
}

export function registerSampleDataCommands(
  _context: vscode.ExtensionContext,
  deps: SampleDataDeps,
): vscode.Disposable[] {
  const createSampleDataCommand = vscode.commands.registerCommand(
    "fs-query-studio.createSampleData",
    async () => {
      await executeCreateSampleData(deps);
    },
  );

  const createAdvancedSamplesCommand = vscode.commands.registerCommand(
    "fs-query-studio.createAdvancedSamples",
    async () => {
      await vscode.commands.executeCommand("fs-query-studio.createSampleData");
    },
  );

  return [createSampleDataCommand, createAdvancedSamplesCommand];
}
