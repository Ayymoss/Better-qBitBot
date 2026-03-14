FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY qBitBotNew/qBitBotNew.csproj qBitBotNew/
RUN dotnet restore qBitBotNew/qBitBotNew.csproj
COPY . .
RUN dotnet publish qBitBotNew/qBitBotNew.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "qBitBotNew.dll"]
