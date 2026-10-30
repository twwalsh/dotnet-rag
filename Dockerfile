# Multi-stage: SDK to build and publish, the ASP.NET runtime image to run. The corpus ships in the image so a
# fresh container can ingest without a mounted volume; a mounted or downloaded file can override Rag:ChunksPath.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/SupportRag.Core/SupportRag.Core.csproj src/SupportRag.Core/
COPY src/SupportRag.Api/SupportRag.Api.csproj src/SupportRag.Api/
RUN dotnet restore src/SupportRag.Api/SupportRag.Api.csproj
COPY src/ src/
RUN dotnet publish src/SupportRag.Api/SupportRag.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS run
WORKDIR /app
COPY --from=build /app .
COPY data/chunks.jsonl /app/data/chunks.jsonl
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Rag__ChunksPath=/app/data/chunks.jsonl \
    DOTNET_gcServer=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SupportRag.Api.dll"]
