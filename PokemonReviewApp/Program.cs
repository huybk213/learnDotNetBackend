using Microsoft.Extensions.Configuration;
using AudioApp.Controllers;
using audioConverter.Services;

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
AudioUrlConverter.SetNginxPath(NginxPath, url);

if (ffmpegPath != null)
{
    AudioUrlConverter.SetFFmpegBinaryPath(ffmpegPath);

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
