using Microsoft.Extensions.Configuration;
using AudioApp.Controllers;
using audioConverter.Services;
using System.Data.Entity;
using Serilog;
using Serilog.Sinks;
using Serilog.Sinks.SystemConsole.Themes;
using Serilog.Events;
using radioTranscodeManager.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    Console.WriteLine("Enable swagger");
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    Console.WriteLine("Disabled swagger");
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

var AppConfig = new ConfigurationBuilder().AddJsonFile("appsettings.Development.json").Build();
var NginxPath = AppConfig.GetValue<string>("NginxFolderConfig:path");
var url = AppConfig.GetValue<string>("NginxFolderConfig:prefixUrl");
var ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH");
var dbPath = Environment.GetEnvironmentVariable("DB_PATH");
AudioUrlConverter.SetNginxPath(NginxPath, url);
//app.Logger.LogInformation($"NginxPath = {NginxPath}");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
        .WriteTo.Console()
        .WriteTo.Logger(l => l.Filter.ByIncludingOnly(e => e.Level == LogEventLevel.Information).WriteTo.File($@"{NginxPath}/Log/Info-.txt",
                                                                                                                rollingInterval: RollingInterval.Day))
        .WriteTo.Logger(l => l.Filter.ByIncludingOnly(e => e.Level == LogEventLevel.Debug).WriteTo.File($@"{NginxPath}/Log/Debug-.txt",
                                                                                                        rollingInterval: RollingInterval.Day))
        .WriteTo.Logger(l => l.Filter.ByIncludingOnly(e => e.Level == LogEventLevel.Warning).WriteTo.File($@"{NginxPath}/Log/Warning-.txt",
                                                                                                        rollingInterval: RollingInterval.Day))
        .WriteTo.Logger(l => l.Filter.ByIncludingOnly(e => e.Level == LogEventLevel.Error).WriteTo.File($@"{NginxPath}/Log/Error-.txt",
                                                                                                        rollingInterval: RollingInterval.Day))
    .CreateLogger();

if (ffmpegPath != null && dbPath != null)
{
    AudioUrlConverter.SetFFmpegBinaryPath(ffmpegPath);
    RadioTranscodeManager.StartService(dbPath);

    //Create audio dir
    // If directory does not exist, create it
    if (!Directory.Exists(NginxPath))
    {
        Directory.CreateDirectory(NginxPath);
    }

    app.Run();
}
else
{
    Console.WriteLine("Unknown ffmpeg path, exit application");
}
