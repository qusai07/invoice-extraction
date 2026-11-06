# Build context: repository root.   docker build -f src/InvoiceFlow.Api/Dockerfile -t invoiceflow-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/InvoiceFlow.Api/InvoiceFlow.Api.csproj
RUN dotnet publish src/InvoiceFlow.Api/InvoiceFlow.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false \
 && rm -f /app/appsettings.Development.json   # developer machine settings never ship in an image

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
# Mock ERP master data until a real ERP adapter exists (Phase 4). Replace via MockErp__SeedFile.
COPY evals/data/master-data.json /app/seed/master-data.json

ENV ASPNETCORE_HTTP_PORTS=8080 \
    MockErp__SeedFile=/app/seed/master-data.json \
    ForwardedHeaders__Enabled=true \
    DOTNET_gcServer=1
EXPOSE 8080
# Non-root user provided by the .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "InvoiceFlow.Api.dll"]
