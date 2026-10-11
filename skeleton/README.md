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
