# .NET Web API → Azure DevOps template (Red Hat Developer Hub / Backstage)

A Software Template that:

1. Generates an ASP.NET Core minimal API (.NET 10 or .NET 8) with a multi-stage, non-root `Dockerfile`.
2. Creates a **new Azure DevOps repo** and pushes the code (`publish:azure`).
3. Creates a **new Azure Pipeline** from `azure-pipelines.yml` (`azure:pipeline:create`) with two stages: build the Docker image and push it to **JFrog Artifactory**, then — optionally — `oc apply` the OpenShift manifests the template generated alongside the code.
4. Optionally pre-authorizes the pipeline to use the registry and OpenShift service connections (`azure:pipeline:permit`) and kicks off the first run (`azure:pipeline:run`).
5. Registers the new Component + API in the catalog, with Azure DevOps, TechDocs and (when deploying) Kubernetes annotations.

```
dotnet-api-azure-devops/
├── template.yaml
├── skeleton/                      # -> the new repo
│   ├── azure-pipelines.yml        # Build and push -> Deploy to OpenShift
│   ├── Dockerfile                 # sdk build → Red Hat UBI 9 runtime, port 8080, USER 1001
│   ├── catalog-info.yaml          # Component + API, dev.azure.com + kubernetes annotations
│   ├── mkdocs.yml, docs/, README.md
│   └── src/WebApi/                # WebApi.csproj, Program.cs, appsettings
└── openshift-skeleton/            # -> openshift/ in the same repo
    ├── base/                      # Deployment, Service
    └── route/                     # Route (only if "Expose with a Route" is on)
```

## How a change reaches OpenShift

```
commit to main
  └─ Build ............... image -> <registry>/<repo>/<name>:<BuildId>
      └─ Deploy to OpenShift
           sed IMAGE_PLACEHOLDER -> <image>:<BuildId>  (on a copy, in the agent workspace)
           oc apply -f openshift/
           oc rollout status
```

There is no GitOps controller and no second repository: the pipeline holds the cluster
credential and applies the manifests itself. The manifests in the repo stay generic —
the per-build tag only ever exists in the applied copy, so `git` never records it and
`oc rollout status` is the only thing that reports success.

## 1. Enable the scaffolder actions in RHDH

| Action(s) | Package | In RHDH |
|---|---|---|
| `publish:azure` | `@backstage/plugin-scaffolder-backend-module-azure` | Preinstalled: `./dynamic-plugins/dist/backstage-plugin-scaffolder-backend-module-azure-dynamic` |
| `azure:pipeline:create`, `:permit`, `:run` | `@backstage-community/plugin-scaffolder-backend-module-azure-devops` | **Not** in the preinstalled set — add it (below) |

The OpenShift deploy stage needs no scaffolder plugin — it runs inside the Azure Pipeline. It does need the **[OpenShift extension for Azure DevOps](https://marketplace.visualstudio.com/items?itemName=redhat.openshift-vsts)** installed on the Azure DevOps organization, which is what provides the `oc-setup@3` task and the OpenShift service connection type.

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

## 4. Prerequisites in OpenShift

Only needed when the form's **Deploy to OpenShift** is left on.

- The **[OpenShift extension for Azure DevOps](https://marketplace.visualstudio.com/items?itemName=redhat.openshift-vsts)** is installed on the organization. The deploy stage uses its `oc-setup@3` task, which installs `oc`, puts it on `PATH` and writes a kubeconfig — the rest of the stage is plain `oc` in `script:` steps.
- An **OpenShift service connection** exists in the Azure DevOps project (*Project Settings → Service connections → New → OpenShift*). Token authentication with a service account token is the one to use:

  ```bash
  oc create serviceaccount azure-pipelines -n orders-dev
  oc policy add-role-to-user edit -z azure-pipelines -n orders-dev
  oc create token azure-pipelines -n orders-dev --duration=8760h   # paste as the API token
  ```

  `oc create token` issues a bound, expiring token — put its renewal on a calendar, or use a long-lived `kubernetes.io/service-account-token` Secret instead. Users enter the connection's name in the form; optionally its GUID, so the new pipeline is pre-authorized and the first run doesn't pause for approval.
- The **namespace already exists** and that identity has `edit` on it. The template only creates the repo, the pipeline and the manifests — nothing cluster-side:

  ```bash
  oc new-project orders-dev
  ```

  The deploy stage checks this with `oc auth can-i create deployments.apps` before applying, and fails with the `oc policy` command to fix it.
- **A pull secret for Artifactory** in that namespace, unless the Docker repository allows anonymous pulls. Name it in the form's *Image pull secret* field and it is wired into the Deployment:

  ```bash
  oc create secret docker-registry artifactory-pull \
    --docker-server=example.jfrog.io --docker-username=<user> --docker-password=<identity-token> \
    -n orders-dev
  ```

- The **cluster API is reachable from the agent**. The pipeline runs on Microsoft-hosted `ubuntu-latest`, so a cluster behind a private network needs `pool:` in `skeleton/azure-pipelines.yml` switched to a self-hosted pool. The same applies to `oc` itself, which `oc-setup@3` downloads from mirror.openshift.com — for an air-gapped agent, put the oc bundle somewhere internal and pass its URL in the form's *oc version* field.

### Adding an approval gate

The stage deploys on every build of the default branch. To gate it, turn the job into a deployment job against an Azure DevOps [environment](https://learn.microsoft.com/azure/devops/pipelines/process/environments) and add an approval check to that environment:

```yaml
      - deployment: OcApply
        environment: orders-dev
        strategy:
          runOnce:
            deploy:
              steps:
                - checkout: self
                # ...unchanged steps
```

### Without the OpenShift extension

If the organization can't install marketplace extensions, drop the `oc-setup@3` task and log in from a secret variable (`OPENSHIFT_SERVER`, `OPENSHIFT_TOKEN` in a variable group linked to the pipeline):

```yaml
          - script: |
              set -euo pipefail
              curl -sSL https://mirror.openshift.com/pub/openshift-v4/clients/ocp/stable/openshift-client-linux.tar.gz \
                | tar -xz -C "$(Agent.TempDirectory)" oc
              echo "##vso[task.prependpath]$(Agent.TempDirectory)"
              "$(Agent.TempDirectory)/oc" login "$(OPENSHIFT_SERVER)" --token="$(OPENSHIFT_TOKEN)"
            displayName: Install and configure oc
```

`prependpath` only affects later steps, hence the explicit path for the login itself; the
kubeconfig `oc login` writes persists for the rest of the job. Everything after it is
already plain `oc`, so no other step changes.

## 5. Register the template

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

- **Templating gotcha:** the skeleton is rendered with Nunjucks using `${{ }}`, the same delimiters Azure Pipelines uses for template expressions. `azure-pipelines.yml` therefore uses only `$(var)` macros and `$[ ]` runtime expressions. If you add `${{ }}` pipeline expressions, wrap them in `{% raw %}…{% endraw %}` or list the file under `copyWithoutTemplating` in the `fetch:template` step. The OpenShift manifests are rendered the same way, so `${{ values.* }}` works in them, but anything Helm-like would not survive.
- **`oc-setup` major version:** the task is pinned at `@3`, read from [its `task.json`](https://github.com/redhat-developer/openshift-vsts/blob/master/tasks/oc-setup/task.json). The extension's getting-started doc still documents `@2` for all four of its tasks, and a stale major version fails at queue time with *"A task is missing. The pipeline references a task called 'oc-setup' with version '2'"* — which reads like the extension isn't installed even when it is. If a future extension release bumps the major again, check `task.json` rather than the docs.
- **Branch:** `publish:azure` defaults to `master`; the template sets `defaultBranch` explicitly (default `main`) and uses the same value for the pipeline trigger and the run.
- **PR builds:** for Azure Repos, PR validation is a branch policy, not a `pr:` block. The pipeline builds on PRs but only pushes on the default branch.
- **Image tags:** `<BuildId>` and, unless the form's *Also push the ":latest" tag* is turned off, `latest`. Turn it off when the target repo has **immutable tags** / "block pushing existing tags" enabled — otherwise the second build fails trying to overwrite `latest`.
- **Registry host is asked for twice over,** in effect: the pipeline gets it from the Docker service connection, but OpenShift pulls the image itself and can't read that connection, so the form's *Artifactory Docker registry host* field supplies it to the manifests. If the two disagree the build succeeds and the pods land in `ImagePullBackOff`.
- **The tag is pinned in the applied copy, not in the repo.** The deploy stage copies `openshift/` into the agent's staging directory and `sed`s `IMAGE_PLACEHOLDER` there, so the repo never carries a build-specific tag and two runs can't conflict over the file. The trade-off is that the running tag lives only in the cluster and the build log — `oc get deployment <name> -o jsonpath='{.spec.template.spec.containers[0].image}'` is the source of truth. The stage fails loudly if the placeholder is ever edited out.
- **Route conditionality is a directory, not a flag.** `fetch:template` has no per-file condition, so the Route lives in `openshift-skeleton/route/` and is fetched by its own step. Turning *Expose with a Route* off leaves no dead file in the new repo.
- **Catalog Kubernetes annotations are added but not configured here.** When deploying, `catalog-info.yaml` gets `backstage.io/kubernetes-id` + `-namespace`, and the manifests carry the matching label. The Kubernetes / Topology tab only appears once RHDH itself has a `kubernetes:` cluster locator in `app-config` — without it the annotations are simply inert.
- **Rollback is `oc rollout undo`,** or re-running an older pipeline run's deploy stage — the image tag it pins is that run's Build ID, which still exists in Artifactory.
- **No Artifactory build-info.** `Docker@2` only does a plain registry push, so there is no build-info, dependency graph or Xray scan gate. For those, install the **JFrog Artifactory** extension on the Azure DevOps organization, swap the form field for a *JFrog Platform* service connection, and replace the Docker tasks with `JFrogToolsInstaller@1` + `JFrogDocker@1` + `JFrogPublishBuildInfo@1` (optionally `JFrogBuildScan@1`).
