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
ENV DOTNET_GCHeapHardLimit=0x18000000
ENV DOTNET_GCConserveMemory=9
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WhiteListApi.dll"]
