using AoiDemo.Web.Aoi;
using AoiDemo.Web.Models;
using AoiDemo.Web.Runtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<WorldOptions>()
    .Bind(builder.Configuration.GetSection(WorldOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IAoiStrategy, BruteForceAoiStrategy>();
builder.Services.AddSingleton<IAoiStrategy, UniformGridAoiStrategy>();
builder.Services.AddSingleton<IAoiStrategy, QuadtreeAoiStrategy>();
builder.Services.AddSingleton<DemoWorldService>();
builder.Services.AddHostedService(static services => services.GetRequiredService<DemoWorldService>());

var app = builder.Build();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

app.Map("/ws", async (HttpContext context, DemoWorldService worldService) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Expected a WebSocket upgrade request.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await worldService.HandleConnectionAsync(socket, context.RequestAborted);
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;
