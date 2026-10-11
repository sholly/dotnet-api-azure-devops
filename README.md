# .NET Web API → Azure DevOps template (Red Hat Developer Hub / Backstage)

A Software Template that:

1. Generates an ASP.NET Core minimal API (.NET 10 or .NET 8) with a multi-stage, non-root `Dockerfile`.
2. Creates **two new Azure DevOps repos** and pushes to both (`publish:azure`): the application, and a `-gitops` repo holding the Helm chart that describes its OpenShift deployment.
3. Creates a **new Azure Pipeline** from `azure-pipelines.yml` (`azure:pipeline:create`) with three stages: build the image and push it to **JFrog Artifactory**, pin the new tag for **dev** in the GitOps repo, and — manually triggered — pin the same tag for **prod**.
4. Optionally pre-authorizes the pipeline to use the registry service connection (`azure:pipeline:permit`) and kicks off the first run (`azure:pipeline:run`).
5. Creates **two Argo CD applications** — `<name>-dev` and `<name>-prod` — pointed at the GitOps repo (`argocd:create-resources`).
6. Registers the new Component + API in the catalog, with Azure DevOps, TechDocs and Argo CD annotations.

```
dotnet-api-azure-devops/
├── template.yaml
├── skeleton/                       # -> the application repo
│   ├── azure-pipelines.yml         # Build -> Deploy to dev -> Promote to prod
│   ├── .azure/templates/
│   │   └── gitops-bump.yml         # shared step: rewrite image.tag, push to GitOps repo
│   ├── Dockerfile                  # sdk build → Red Hat UBI 9 runtime, port 8080, USER 1001
│   ├── catalog-info.yaml           # Component + API, dev.azure.com + argocd annotations
│   ├── mkdocs.yml, docs/, README.md
│   └── src/WebApi/                 # WebApi.csproj, Program.cs, appsettings
└── gitops-skeleton/                # -> the <name>-gitops repo
    ├── README.md                   # cluster bootstrap commands for the new service
    └── chart/
        ├── Chart.yaml, values.yaml
        ├── values-dev.yaml         # image.tag written by the pipeline
        ├── values-prod.yaml        # image.tag written by the pipeline
        ├── .argocd-source-<name>-dev.yaml   # selects values-dev.yaml
        ├── .argocd-source-<name>-prod.yaml  # selects values-prod.yaml
        └── templates/              # Deployment, Service, Route, ServiceAccount
```

## How a change reaches OpenShift

```
commit to main
  └─ Build .............. image -> <registry>/<repo>/<name>:<BuildId>
      └─ Deploy to dev ... rewrites image.tag in chart/values-dev.yaml, pushes
          └─ Argo CD syncs <name>-dev
      └─ Promote to prod . MANUAL stage; writes the same tag into values-prod.yaml
          └─ Argo CD syncs <name>-prod
```

Both Argo CD applications sync automatically, so the production gate is the manually
triggered pipeline stage, not an Argo CD sync button. Because the promote stage reuses the
tag its own run built, prod can only receive an image that already went through dev.

## 1. Enable the scaffolder actions in RHDH

| Action(s) | Package | In RHDH |
|---|---|---|
| `publish:azure` | `@backstage/plugin-scaffolder-backend-module-azure` | Preinstalled: `./dynamic-plugins/dist/backstage-plugin-scaffolder-backend-module-azure-dynamic` |
| `azure:pipeline:create`, `:permit`, `:run` | `@backstage-community/plugin-scaffolder-backend-module-azure-devops` | **Not** in the preinstalled set — add it (below) |
| `argocd:create-resources` | `@roadiehq/scaffolder-backend-argocd` | Preinstalled: `./dynamic-plugins/dist/roadiehq-scaffolder-backend-argocd-dynamic` |

`dynamic-plugins.yaml` (or the `dynamic-plugins` ConfigMap for the Operator / Helm `global.dynamic.plugins`):

```yaml
plugins:
  - package: ./dynamic-plugins/dist/backstage-plugin-scaffolder-backend-module-azure-dynamic
    disabled: false

  - package: ./dynamic-plugins/dist/roadiehq-scaffolder-backend-argocd-dynamic
    disabled: false

  # Optional, for the Argo CD tab on the catalog entity (not needed by the template itself):
  # - package: ./dynamic-plugins/dist/backstage-community-plugin-redhat-argocd
  #   disabled: false

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

For vanilla Backstage instead: `yarn --cwd packages/backend add @backstage-community/plugin-scaffolder-backend-module-azure-devops @backstage/plugin-scaffolder-backend-module-azure @roadiehq/scaffolder-backend-argocd` and `backend.add(import(...))` for each in `packages/backend/src/index.ts`.

## 2. Azure DevOps integration (app-config)

```yaml
integrations:
  azure:
    - host: dev.azure.com
      credentials:
        - personalAccessToken: ${AZURE_DEVOPS_PAT}
```

PAT scopes needed by the template: **Code (Read, write & manage)** to create both repos, **Build (Read & execute)** to create/run the pipeline, and **Service Connections (Read, query & manage)** only if you use the pre-authorize step. Prefer a service principal / managed identity credential for production.

## 3. Argo CD integration (app-config)

`argocd:create-resources` talks to the Argo CD API, so RHDH needs an instance configured.
The form's **Argo CD instance** field must match a `name` here, not a URL:

```yaml
argocd:
  appLocatorMethods:
    - type: config
      instances:
        - name: main
          url: https://openshift-gitops-server-openshift-gitops.apps.example.com
          token: ${ARGOCD_AUTH_TOKEN}
          # or username/password instead of token
```

Mint the token from an Argo CD local account (`argocd account generate-token --account backstage`)
whose RBAC allows `applications, create` and `projects, create`. On OpenShift GitOps, add the
account under `spec.extraConfig` / `accounts.<name>` of the `ArgoCD` CR and grant it a role in
`spec.rbac.policy`.

## 4. Prerequisites in Azure DevOps

- The target **project** already exists (the template creates the repo, not the project).
- A **Docker Registry service connection** of type **Others** exists in that project, pointing at Artifactory:
  - **Docker Registry** — your Artifactory Docker registry URL (see the access methods below).
  - **Docker ID** — an Artifactory user, or a token subject.
  - **Password** — an **identity token** or reference token, not the account password.

  Users enter the connection's name in the form; optionally its GUID so the pipeline is pre-authorized. Without the GUID, the first run will pause with a "needs permission" prompt that a project admin approves once.
- The **Docker repository already exists in Artifactory**. Unlike ACR, Artifactory does not create a repository on first push. A *local* Docker repo works as-is; a *virtual* Docker repo needs a **default deployment repository** set, or the push is rejected.
- The deploying identity needs **Deploy/Cache** permission on that repository, and **Read** on any remote repo used for base images.
- Pipeline uses the Microsoft-hosted `ubuntu-latest` pool, so Artifactory must be reachable from Microsoft-hosted agents. For an internal-only Artifactory, switch `pool:` in `skeleton/azure-pipelines.yml` to a self-hosted pool.
- The **build identity can write to the GitOps repo**. The deploy stages push a commit to it using `System.AccessToken`, so *Project Settings → Repositories → `<name>-gitops` → Security* must grant **Contribute** to `<Project> Build Service (<org>)`. Project-level Contribute is inherited by newly created repos, so this usually already holds — check it once, because the failure shows up only in the first deploy stage, as `TF401027` or a `403` on push.
- **Manually triggered stages** (`trigger: manual` on the prod stage) are an Azure DevOps **Services** feature. On Azure DevOps Server 2022 the keyword is ignored and the stage runs automatically — gate it with an [environment approval](https://learn.microsoft.com/azure/devops/pipelines/process/approvals) instead.
- Both repos should live in the **same project**. The pipeline reaches the GitOps repo through `resources.repositories`, and the default job authorization scope is the current project. For a cross-project GitOps repo, turn off *Limit job authorization scope to current project* and grant that project's build identity access.

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

## 5. Prerequisites in OpenShift

The scaffolder creates the Argo CD applications but cannot create cluster objects, so for
each new service someone has to do this once. The generated GitOps repo's `README.md` has
the exact commands with the names already filled in — point the service team at it.

- **Both namespaces must already exist.** `argocd:create-resources` hardcodes
  `CreateNamespace=false`, so Argo CD will not create them and the application stays
  `Missing` until they do.
- **Argo CD must be allowed to manage them**, via
  `oc label namespace <ns> argocd.argoproj.io/managed-by=openshift-gitops`.
- **Argo CD needs read access to the GitOps repo** — an Azure DevOps PAT with *Code (Read)*,
  added under *Settings → Repositories* or as a `argocd.argoproj.io/secret-type=repository`
  secret in the Argo CD namespace. One credential scoped to the whole Azure DevOps
  organization covers every service, so this is normally a one-time platform setup rather
  than per-service.
- **A pull secret for Artifactory** in both namespaces, if the Docker repo isn't anonymous.
  Name it in the form's *Image pull secret* field and the chart wires it up.

Until the first build finishes, both environments point at the `latest` tag and will report
as degraded. That clears itself on the first run of the pipeline.

### How one chart serves both environments

Both Argo CD applications use the same `source.path` of `chart`. The per-environment values
file is selected by a `chart/.argocd-source-<application name>.yaml` file, which Argo CD
merge-patches over `spec.source` when it renders that application:

```yaml
# chart/.argocd-source-orders-api-dev.yaml
helm:
  valueFiles:
    - values-dev.yaml
```

This indirection exists because `argocd:create-resources` only sets `repoURL` and `path` on
the Application — it has no input for `helm.valueFiles`. Doing it in the repo keeps the
templates in one place instead of copying the chart per environment, and it stays in Git
where the rest of the desired state lives.

Two consequences worth knowing:

- If your Argo CD runs applications outside its own namespace (*apps in any namespace*), the
  filename it looks for is `.argocd-source-<app namespace>_<application name>.yaml`. Rename
  the two files in `gitops-skeleton/chart/` to match.
- Renaming an Argo CD application means renaming its override file too, or it silently falls
  back to the chart's plain `values.yaml`.

### What the action fixes for you, and how to change it

`argocd:create-resources` builds the Application with a fixed spec. The parts you're most
likely to want different:

| Behaviour | Set by the action | To change |
|---|---|---|
| `syncPolicy.automated` with `prune` + `selfHeal` | always on, both environments | edit the Application after creation — Argo CD doesn't manage its own Application object, so the change sticks |
| `targetRevision` | unset, so Argo CD tracks `HEAD` | set it on the Application to pin a branch or tag |
| `destination.server` | `https://kubernetes.default.svc` | for a remote cluster, edit the Application and its AppProject |
| AppProject | one per application, named after it | the form's *Argo CD project* field — but see below |

Leave *Argo CD project* blank unless you know the project already matches. The action `POST`s
the project and the Argo CD API rejects a create whose spec differs from an existing project
of that name, which surfaces as `Duplicate project detected. Cannot overwrite existing.` and
fails the scaffolder run after the repos have already been created. A per-application project
also keeps `sourceRepos` and `destinations` tight, which is the behaviour you usually want.

## 6. Register the template

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

- **Templating gotcha:** the skeleton is rendered with Nunjucks using `${{ }}`, the same delimiters Azure Pipelines uses for template expressions. `azure-pipelines.yml` therefore uses only `$(var)` macros and `$[ ]` runtime expressions. The one file that does need `${{ parameters.* }}` — `skeleton/.azure/templates/gitops-bump.yml` — is listed under `copyWithoutTemplating` and so is copied verbatim; don't put `${{ values.* }}` in it, pass values through as template parameters from `azure-pipelines.yml` instead. The Helm templates in `gitops-skeleton/chart/templates/` are copied verbatim for the same reason (Helm's `{{ }}` would survive Nunjucks, but they need no scaffolder values). Anywhere else, wrap pipeline expressions in `{% raw %}…{% endraw %}`.
- **Branch:** `publish:azure` defaults to `master`; the template sets `defaultBranch` explicitly (default `main`) and uses the same value for the pipeline trigger and the run.
- **PR builds:** for Azure Repos, PR validation is a branch policy, not a `pr:` block. The pipeline builds on PRs but only pushes on the default branch.
- **Image tags:** `<BuildId>` and, unless the form's *Also push the ":latest" tag* is turned off, `latest`. Turn it off when the target repo has **immutable tags** / "block pushing existing tags" enabled — otherwise the second build fails trying to overwrite `latest`. Deployments always pin `<BuildId>`; `latest` is only the placeholder the chart ships with before the first build.
- **Registry host is asked for twice over,** in effect: the pipeline gets it from the service connection, but OpenShift pulls the image itself and can't read that connection, so the form's *Artifactory Docker registry host* field supplies it to the Helm chart. If the two disagree, the build succeeds and the pods land in `ImagePullBackOff`.
- **Tag bumping is a `sed` on one line.** `gitops-bump.yml` asserts each `values-<env>.yaml` contains exactly one `tag:` key and fails the stage otherwise, rather than guessing. Keep the tag a single quoted scalar on its own line, and keep other `tag:` keys out of those two files. It also re-reads the remote tip and retries on a rejected push, so concurrent builds of the same branch don't lose a bump.
- **Nothing triggers a loop.** The deploy stages push to the GitOps repo, which has no pipeline of its own, and the application pipeline only triggers on its own repo.
- **No Artifactory build-info.** `Docker@2` only does a plain registry push, so there is no build-info, dependency graph or Xray scan gate. For those, install the **JFrog Artifactory** extension on the Azure DevOps organization, swap the form field for a *JFrog Platform* service connection, and replace the Docker tasks with `JFrogToolsInstaller@1` + `JFrogDocker@1` + `JFrogPublishBuildInfo@1` (optionally `JFrogBuildScan@1`).
