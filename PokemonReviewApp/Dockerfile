#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.
FROM ubuntu
FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# Install ffmpeg
RUN apt-get -y update
RUN apt-get -y upgrade
RUN apt-get install -y ffmpeg

ENV ASPNETCORE_ENVIRONMENT=Development
ENV ASPNETCORE_URLS http://*:80;https://*:443
#ENV ASPNETCORE_URLS http://*:80

ENV FFMPEG_PATH="/usr/bin/ffmpeg"
ENV DB_PATH="/home/ubuntu/"

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["PokemonReviewApp/audioConverter.csproj", "PokemonReviewApp/"]
RUN dotnet restore "PokemonReviewApp/audioConverter.csproj"
COPY . .
WORKDIR "/src/PokemonReviewApp"
RUN dotnet build "audioConverter.csproj" -c Debug -o /app/build

FROM build AS publish
RUN dotnet publish "audioConverter.csproj" -c Debug -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "audioConverter.dll"]