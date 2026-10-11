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

## CI

`azure-pipelines.yml` builds the Docker image on every push to `${{ values.defaultBranch }}` and pushes
`${{ values.imageRepository }}:<BuildId>`{% if values.pushLatestTag %} and `:latest`{% endif %} to JFrog Artifactory
through the `${{ values.containerRegistryConnection }}` service connection. The registry host
comes from that service connection, so the full image is
`<registry>/${{ values.imageRepository }}:<BuildId>`.

Pull requests build the image but do not push it.

{%- if values.deployToOpenShift %}

## Deploy to OpenShift

The pipeline's second stage, *Deploy to OpenShift*, runs on every build of
`${{ values.defaultBranch }}` and applies the manifests in `openshift/` with `oc apply`:

| File | What it does |
|---|---|
| `openshift/deployment.yaml` | `${{ values.replicas }}` replicas, port 8080, `/healthz` readiness + liveness probes |
| `openshift/service.yaml` | ClusterIP Service on 8080 |
{%- if values.createRoute %}
| `openshift/route.yaml` | Edge-terminated Route, HTTP redirected to HTTPS |
{%- endif %}

The image is pinned per build: the stage replaces `IMAGE_PLACEHOLDER` in a copy of
`deployment.yaml` with `${{ values.registryHost }}/${{ values.imageRepository }}:<BuildId>`,
applies it, then waits on `oc rollout status`. Nothing in the repo is rewritten, so the
tag of what's running is the Build ID of the run that deployed it.

Target namespace: **`${{ values.openshiftNamespace }}`**. It has to exist, and the
`${{ values.openshiftServiceConnection }}` service connection's identity needs `edit` on
it — the stage checks that up front and fails with the `oc` command to fix it.

Deploy by hand, same manifests:

```bash
sed 's|IMAGE_PLACEHOLDER|${{ values.registryHost }}/${{ values.imageRepository }}:<tag>|' \
  openshift/deployment.yaml | oc apply -n ${{ values.openshiftNamespace }} -f -
oc apply -n ${{ values.openshiftNamespace }} -f openshift/service.yaml{% if values.createRoute %} -f openshift/route.yaml{% endif %}
```
{%- endif %}
