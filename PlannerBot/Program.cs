using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlannerBot.Data;
using PlannerBot.Services;
using Telegram.Bot;

var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ??
                  throw new Exception("DATABASE_URL environment variable not set");

var builder = Host.CreateApplicationBuilder();

builder.Services.AddHttpClient("telegram_bot_client")
    .AddTypedClient<ITelegramBotClient>(client =>
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ??
                    throw new Exception("TELEGRAM_TOKEN environment variable not set");
        TelegramBotClientOptions options = new(token);
        return new TelegramBotClient(options, client);
    });

builder.Services.AddScoped<UpdateHandler>();
builder.Services.AddScoped<ReceiverService>();
builder.Services.AddHostedService<PollingService>();
builder.Services.AddNpgsql<AppDbContext>(databaseUrl);

var app = builder.Build();

await using var db = app.Services.GetRequiredService<AppDbContext>();
await db.Database.MigrateAsync();

await app.RunAsync();