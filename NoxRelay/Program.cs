using NoxRelay.Hubs;
using NoxRelay.Services;

var builder = WebApplication.CreateBuilder(args);

// Console logging with timestamps — enough for local dev; if this ever
// goes to Render, their log viewer captures stdout directly, so no extra
// sink is needed for now.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss.fff";
    options.SingleLine = true;
});

// SignalR's own internal logs (connection lifecycle, transport negotiation)
// are noisy at Debug — Information level shows connects/disconnects without
// drowning out our own application logs.
builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Information);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddSignalR();
builder.Services.AddSingleton<SessionManager>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
            .AllowAnyMethod()
            .SetIsOriginAllowed(_ => true)
            .AllowCredentials();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors();
app.MapHub<NoxRelayHub>("/hubs/noxrelay");
app.Logger.LogInformation("NoxRelay server starting up");

app.MapGet("/", () => "NoxRelay is running");

app.Run();
