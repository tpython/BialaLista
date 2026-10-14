FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["WhiteListApi.csproj", "./"]
RUN dotnet restore "WhiteListApi.csproj"
COPY . .
RUN dotnet publish "WhiteListApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV TZ=Europe/Warsaw
ENV DOTNET_GCHeapHardLimit=0x30000000

# Instalacja tzdata dla strefy Europe/Warsaw
RUN apt-get update && apt-get install -y --no-install-recommends tzdata && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "WhiteListApi.dll"]
