#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:5.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src
COPY ["Algorum.Strategy.IndexFuturesTrend/Algorum.Strategy.IndexFuturesTrend.csproj", "Algorum.Strategy.IndexFuturesTrend/"]
RUN dotnet restore "Algorum.Strategy.IndexFuturesTrend/Algorum.Strategy.IndexFuturesTrend.csproj"
COPY . .
WORKDIR "/src/Algorum.Strategy.IndexFuturesTrend"
RUN dotnet build "Algorum.Strategy.IndexFuturesTrend.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Algorum.Strategy.IndexFuturesTrend.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Algorum.Strategy.IndexFuturesTrend.dll"]