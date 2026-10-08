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
`${{ values.imageRepository }}:<BuildId>` and `:latest` through the
`${{ values.containerRegistryConnection }}` service connection.
