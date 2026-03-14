using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using qBitBotNew.Config;
using qBitBotNew.Handlers;
using qBitBotNew.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("NetCord", Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.File("logs/qbitbot-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog();

    // Bind configuration sections
    builder.Services.Configure<BotConfig>(builder.Configuration.GetSection("Bot"));
    builder.Services.Configure<GeminiConfig>(builder.Configuration.GetSection("Gemini"));

    // Discord gateway with required intents
    // Token is bound automatically from "Discord:Token" in config (appsettings, user-secrets, env vars)
    builder.Services.AddDiscordGateway(options =>
    {
        options.Intents = GatewayIntents.GuildMessages
                          | GatewayIntents.MessageContent
                          | GatewayIntents.GuildUsers;
    });

    // HTTP client for Gemini API
    builder.Services.AddHttpClient<GeminiService>();

    // Services
    builder.Services.AddSingleton<MessageAggregatorService>();
    builder.Services.AddSingleton<RateLimiterService>();

    // Gateway event handlers
    builder.Services.AddGatewayHandler<MessageCreateHandler>();

    var host = builder.Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
