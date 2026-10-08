# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:${{ values.dotnetVersion }} AS build
WORKDIR /src

# Restore first so this layer is cached when only source changes
COPY ["src/${{values.name}}/${{values.name}}.csproj", "src/${{values.name}}/"]
RUN dotnet restore "src/${{values.name}}/${{values.name}}.csproj"

COPY . .
RUN dotnet publish "src/${{values.name}}/${{values.name}}.csproj" \
    -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime (Red Hat UBI 9 / RHEL-based) ----
{%- if values.dotnetVersion == '10.0' %}
# .NET 10: ASP.NET Core ships in its own image
FROM registry.access.redhat.com/ubi9/dotnet-100-aspnet AS final
{%- else %}
# .NET 8: the runtime image includes ASP.NET Core
FROM registry.access.redhat.com/ubi9/dotnet-80-runtime AS final
{%- endif %}

# Standard app directory for Red Hat .NET images
WORKDIR /opt/app-root/app
ENV ASPNETCORE_URLS=http://*:8080
EXPOSE 8080

# Group 0 ownership so OpenShift's arbitrary UIDs can read the app
COPY --from=build --chown=1001:0 /app/publish .

# Non-root default user of the Red Hat .NET images
USER 1001
ENTRYPOINT ["dotnet", "${{values.name}}.dll"]
