var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
{%- if values.dotnetVersion == '10.0' %}
builder.Services.AddOpenApi();
{%- endif %}

var app = builder.Build();
{%- if values.dotnetVersion == '10.0' %}

// OpenAPI document at /openapi/v1.json
app.MapOpenApi();
{%- endif %}

// Liveness/readiness probe for Kubernetes / OpenShift
app.MapHealthChecks("/healthz");

app.MapGet("/", () => Results.Ok(new
{
    service = "${{ values.name }}",
    description = "${{ values.description }}"
}));

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/api/weatherforecast", () =>
    Enumerable.Range(1, 5).Select(index => new WeatherForecast(
        DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
        Random.Shared.Next(-20, 55),
        summaries[Random.Shared.Next(summaries.Length)]))
    .ToArray())
    .WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
