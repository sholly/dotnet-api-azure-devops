# ${{ values.name }}

${{ values.description }}

## Run locally

```bash
dotnet run --project src/${{ values.name }}
curl http://localhost:5080/healthz
```

## Run in Docker

```bash
docker build -t ${{ values.name }}:local .
docker run --rm -p 8080:8080 ${{ values.name }}:local
```

## CI/CD

`azure-pipelines.yml` runs three stages on every push to `${{ values.defaultBranch }}`.

**Build** builds the Docker image and pushes
`${{ values.imageReference }}:<BuildId>`{% if values.pushLatestTag %} and `:latest`{% endif %} to JFrog
Artifactory through the `${{ values.containerRegistryConnection }}` service connection. Pull requests
build the image but do not push it.

**Deploy to dev** writes that tag into `chart/values-dev.yaml` in
[${{ values.gitopsRepo }}](${{ values.gitopsRepoHttpUrl }}) and pushes the commit. Argo CD picks it
up and syncs the `${{ values.name }}-dev` application into the `${{ values.devNamespace }}` namespace.

**Promote to prod** does the same for `chart/values-prod.yaml` and the
`${{ values.name }}-prod` application in `${{ values.prodNamespace }}`. It is a **manually
triggered stage** — open the run in Azure Pipelines and start it when you want to release.
Because it reuses the tag its own run built, production can only ever get an image that
already went to dev.

Nothing is deployed with `oc apply` or `helm install`. To change how the app is deployed —
replicas, resources, route hostname, environment variables — edit the chart in the GitOps
repo; Argo CD applies it.
