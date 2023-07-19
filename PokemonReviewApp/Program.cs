using Microsoft.Extensions.Configuration;
using PokemonReviewApp.Controllers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
var AppConfig = new ConfigurationBuilder().AddJsonFile("appsettings.Development.json").Build();
var NginxPath = AppConfig.GetValue<string>("NginxFolderConfig:path");
var url = AppConfig.GetValue<string>("NginxFolderConfig:prefixUrl");
var FFmpegPath = AppConfig.GetValue<string>("FFMPEG:path");
AudioUrlConverter.SetNginxPath(NginxPath, url);
AudioUrlConverter.SetFFmpegPath(FFmpegPath);
app.Run();
