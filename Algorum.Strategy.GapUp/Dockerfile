#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:5.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src
COPY ["Algorum.Strategy.GapUp/Algorum.Strategy.GapUp.csproj", "Algorum.Strategy.GapUp/"]
RUN dotnet restore "Algorum.Strategy.GapUp/Algorum.Strategy.GapUp.csproj"
COPY . .
WORKDIR "/src/Algorum.Strategy.GapUp"
RUN dotnet build "Algorum.Strategy.GapUp.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Algorum.Strategy.GapUp.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Algorum.Strategy.GapUp.dll"]