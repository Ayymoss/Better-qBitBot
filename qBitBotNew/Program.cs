using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Services.ComponentInteractions;
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

    // Application commands (slash commands, message commands)
    builder.Services.AddApplicationCommands();

    // Component interactions (button handlers)
    builder.Services.AddComponentInteractions<ButtonInteraction, ButtonInteractionContext>();

    // HTTP client for Gemini API
    builder.Services.AddHttpClient<GeminiService>();

    // Services
    builder.Services.AddSingleton<MessageAggregatorService>();
    builder.Services.AddSingleton<RateLimiterService>();
    builder.Services.AddSingleton<FeedbackService>();

    // Gateway event handlers
    builder.Services.AddGatewayHandler<MessageCreateHandler>();

    var host = builder.Build();

    // Register application command modules and component interaction modules
    host.AddModules(typeof(Program).Assembly);

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
