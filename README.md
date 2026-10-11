# .NET Web API → Azure DevOps template (Red Hat Developer Hub / Backstage)

A Software Template that:

1. Generates an ASP.NET Core minimal API (.NET 10 or .NET 8) with a multi-stage, non-root `Dockerfile`.
2. Creates a **new Azure DevOps repo** and pushes the code (`publish:azure`).
3. Creates a **new Azure Pipeline** from `azure-pipelines.yml` that builds the Docker image and pushes it to **JFrog Artifactory** (`azure:pipeline:create`).
4. Optionally pre-authorizes the pipeline to use the registry service connection (`azure:pipeline:permit`) and kicks off the first run (`azure:pipeline:run`).
5. Registers the new Component + API in the catalog, with Azure DevOps and TechDocs annotations.

```
dotnet-api-azure-devops/
├── template.yaml
└── skeleton/
    ├── azure-pipelines.yml        # Docker@2 build (+ push to Artifactory on default branch)
    ├── Dockerfile                 # sdk build → Red Hat UBI 9 runtime, port 8080, USER 1001
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
- A **Docker Registry service connection** of type **Others** exists in that project, pointing at Artifactory:
  - **Docker Registry** — your Artifactory Docker registry URL (see the access methods below).
  - **Docker ID** — an Artifactory user, or a token subject.
  - **Password** — an **identity token** or reference token, not the account password.

  Users enter the connection's name in the form; optionally its GUID so the pipeline is pre-authorized. Without the GUID, the first run will pause with a "needs permission" prompt that a project admin approves once.
- The **Docker repository already exists in Artifactory**. Unlike ACR, Artifactory does not create a repository on first push. A *local* Docker repo works as-is; a *virtual* Docker repo needs a **default deployment repository** set, or the push is rejected.
- The deploying identity needs **Deploy/Cache** permission on that repository, and **Read** on any remote repo used for base images.
- Pipeline uses the Microsoft-hosted `ubuntu-latest` pool, so Artifactory must be reachable from Microsoft-hosted agents. For an internal-only Artifactory, switch `pool:` in `skeleton/azure-pipelines.yml` to a self-hosted pool.

### Artifactory access methods

`Docker@2` builds the image name as `<registry from the service connection>/<repository>:<tag>`, so where the Artifactory repo key goes depends on how the registry is addressed:

| Access method | Service connection URL | Leave **Artifactory Docker repository key** |
|---|---|---|
| Repository path (default, typical for self-hosted) | `https://example.jfrog.io` | **Set** it, e.g. `docker-local` — the template folds it into the image path |
| Subdomain / virtual host (needs SaaS config or a reverse proxy) | `https://docker-local.example.jfrog.io` | **Blank** — the repo key is already in the host |

Getting this wrong is the most common failure: the push succeeds against a path that Artifactory treats as an unknown repository, or 404s.

### Base images

The form's **Base images** step controls where the Dockerfile pulls from, defaulting to the upstream registries (`mcr.microsoft.com`, `registry.access.redhat.com`). To keep pulls inside your network, point each at an Artifactory **remote** repository — one per upstream, since a remote proxies a single upstream:

| Field | Upstream | Artifactory example |
|---|---|---|
| .NET SDK image registry | `mcr.microsoft.com` | `example.jfrog.io/mcr-remote` |
| Red Hat runtime image registry | `registry.access.redhat.com` | `example.jfrog.io/redhat-remote` |

Note that the Red Hat UBI images need a remote repo configured against `registry.access.redhat.com` (or `registry.redhat.io` with credentials, for subscription content).

Because base images may now come from an authenticated registry, the pipeline's `docker login` runs on **pull request builds too** — it no longer skips them. PR builds still only build, never push.

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
- **Image tags:** `<BuildId>` and, unless the form's *Also push the ":latest" tag* is turned off, `latest`. Turn it off when the target repo has **immutable tags** / "block pushing existing tags" enabled — otherwise the second build fails trying to overwrite `latest`.
- **No Artifactory build-info.** `Docker@2` only does a plain registry push, so there is no build-info, dependency graph or Xray scan gate. For those, install the **JFrog Artifactory** extension on the Azure DevOps organization, swap the form field for a *JFrog Platform* service connection, and replace the Docker tasks with `JFrogToolsInstaller@1` + `JFrogDocker@1` + `JFrogPublishBuildInfo@1` (optionally `JFrogBuildScan@1`).
