# .NET Web API → Azure DevOps template (Red Hat Developer Hub / Backstage)

A Software Template that:

1. Generates an ASP.NET Core minimal API (.NET 10 or .NET 8) with a multi-stage, non-root `Dockerfile`.
2. Creates a **new Azure DevOps repo** and pushes the code (`publish:azure`).
3. Creates a **new Azure Pipeline** from `azure-pipelines.yml` that builds the Docker image and pushes it to your registry (`azure:pipeline:create`).
4. Optionally pre-authorizes the pipeline to use the registry service connection (`azure:pipeline:permit`) and kicks off the first run (`azure:pipeline:run`).
5. Registers the new Component + API in the catalog, with Azure DevOps and TechDocs annotations.

```
dotnet-api-azure-devops/
├── template.yaml
└── skeleton/
    ├── azure-pipelines.yml        # Docker@2 build (+ push on default branch)
    ├── Dockerfile                 # MS sdk build → Red Hat UBI 9 runtime, port 8080, USER 1001
    ├── catalog-info.yaml          # Component + API, dev.azure.com annotations
    ├── mkdocs.yml, docs/, README.md
    └── src/WebApi/                # WebApi.csproj, Program.cs, appsettings
```

## 1. Enable the scaffolder actions in RHDH

| Action(s) | Package | In RHDH |
|---|---|---|
| `publish:azure` | `@backstage/plugin-scaffolder-backend-module-azure` | Preinstalled: `./dynamic-plugins/dist/backstage-plugin-scaffolder-backend-module-azure-dynamic` |
| `azure:pipeline:create`, `:permit`, `:run` | `@backstage-community/plugin-scaffolder-backend-module-azure-devops` | **Not** in the preinstalled set — add it (below) |

`dynamic-plugins.yaml` (or the `dynamic-plugins` ConfigMap for the Operator / Helm `global.dynamic.plugins`):

```yaml
plugins:
  - package: ./dynamic-plugins/dist/backstage-plugin-scaffolder-backend-module-azure-dynamic
    disabled: false

  # Azure DevOps pipeline actions. Options:
  #  a) An OCI build from redhat-developer/rhdh-plugin-export-overlays, if one is published
  #     for your RHDH version (check the "Plugin Catalog Status" wiki page for your release), e.g.
  #     oci://ghcr.io/redhat-developer/rhdh-plugin-export-overlays/backstage-community-plugin-scaffolder-backend-module-azure-devops:<tag>!backstage-community-plugin-scaffolder-backend-module-azure-devops
  #  b) Export it yourself as a derived dynamic plugin:
  #       npx @red-hat-developer-hub/cli plugin export   (run in a checkout of the plugin)
  #     push the result to your registry / npm, and reference it here with its integrity hash.
  - package: <see options above>
    disabled: false
```

For vanilla Backstage instead: `yarn --cwd packages/backend add @backstage-community/plugin-scaffolder-backend-module-azure-devops @backstage/plugin-scaffolder-backend-module-azure` and `backend.add(import(...))` both in `packages/backend/src/index.ts`.

## 2. Azure DevOps integration (app-config)

```yaml
integrations:
  azure:
    - host: dev.azure.com
      credentials:
        - personalAccessToken: ${AZURE_DEVOPS_PAT}
```

PAT scopes needed by the template: **Code (Read, write & manage)** to create the repo, **Build (Read & execute)** to create/run the pipeline, and **Service Connections (Read, query & manage)** only if you use the pre-authorize step. Prefer a service principal / managed identity credential for production.

## 3. Prerequisites in Azure DevOps

- The target **project** already exists (the template creates the repo, not the project).
- A **Docker Registry service connection** exists in that project (ACR, Quay, Docker Hub…). Users enter its name in the form; optionally its GUID so the pipeline is pre-authorized. Without the GUID, the first run will pause with a "needs permission" prompt that a project admin approves once.
- Pipeline uses the Microsoft-hosted `ubuntu-latest` pool. Change `pool:` in `skeleton/azure-pipelines.yml` if you use self-hosted agents.

## 4. Register the template

Push this folder to a repo RHDH can read and add it as a catalog location:

```yaml
catalog:
  locations:
    - type: url
      target: https://dev.azure.com/<org>/<project>/_git/<templates-repo>?path=/dotnet-api-azure-devops/template.yaml
      rules:
        - allow: [Template]
```

Then change `spec.owner` in `template.yaml` to your platform team.

## Notes

- **Templating gotcha:** the skeleton is rendered with Nunjucks using `${{ }}`, the same delimiters Azure Pipelines uses for template expressions. `azure-pipelines.yml` therefore uses only `$(var)` macros and `$[ ]` runtime expressions. If you add `${{ }}` pipeline expressions, wrap them in `{% raw %}…{% endraw %}` or list the file under `copyWithoutTemplating` in the `fetch:template` step.
- **Branch:** `publish:azure` defaults to `master`; the template sets `defaultBranch` explicitly (default `main`) and uses the same value for the pipeline trigger and the run.
- **PR builds:** for Azure Repos, PR validation is a branch policy, not a `pr:` block. The pipeline builds on PRs but only pushes on the default branch.
- **Image tags:** `<BuildId>` and `latest`.
