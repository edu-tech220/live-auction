using System.Text.Json.Serialization;
using LiveAuction.Api.Background;
using LiveAuction.Api.Common;
using LiveAuction.Api.Realtime;
using LiveAuction.Application;
using LiveAuction.Application.Abstractions;
using LiveAuction.Application.Realtime;
using LiveAuction.Infrastructure;
using LiveAuction.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// ---------------- Servicios ----------------

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddApiSecurity(builder.Configuration);
builder.Services.AddApiRateLimiting(builder.Configuration);
builder.Services.AddApiCors(builder.Configuration);
builder.Services.AddApiDocumentation();
builder.Services.AddApiHealthChecks();

// SignalR: usa WebSockets cuando están disponibles y cae a Server-Sent Events / Long Polling.
builder.Services.AddSignalR(o =>
    {
        o.EnableDetailedErrors = builder.Environment.IsDevelopment();
        o.MaximumReceiveMessageSize = 4 * 1024; // los mensajes del cliente son diminutos
    })
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Singletons de tiempo real: estado compartido por todas las conexiones del proceso.
builder.Services.AddSingleton<ConnectionTracker>();
builder.Services.AddSingleton<BidThrottle>();
builder.Services.AddSingleton<IAuctionNotifier, SignalRAuctionNotifier>();

builder.Services.AddOptions<LifecycleOptions>()
    .Bind(builder.Configuration.GetSection(LifecycleOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHostedService<AuctionLifecycleWorker>();

var app = builder.Build();

// ---------------- Pipeline ----------------

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "Live Auction v1");
        o.DocumentTitle = "Live Auction API";
    });
}
else
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseHttpsRedirection();
app.UseCors(CorsSettings.PolicyName);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<AuctionHub>(ApiServiceExtensions.HubPath).AllowAnonymous(); // métodos protegidos con [Authorize] dentro del Hub
app.MapHealthChecks("/health", new HealthCheckOptions()).AllowAnonymous().DisableRateLimiting();

// Migraciones (si MigrateOnStartup) y datos iniciales (si Seed habilitado y BD vacía).
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<DbInitializer>().InitializeAsync();

await app.RunAsync();

// Visible para WebApplicationFactory en las pruebas de integración.
public partial class Program;
