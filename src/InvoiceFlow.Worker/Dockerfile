# Build context: repository root.   docker build -f src/InvoiceFlow.Worker/Dockerfile -t invoiceflow-worker .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/InvoiceFlow.Worker/InvoiceFlow.Worker.csproj
RUN dotnet publish src/InvoiceFlow.Worker/InvoiceFlow.Worker.csproj -c Release -o /app --no-restore -p:UseAppHost=false \
 && rm -f /app/appsettings.Development.json

# runtime-deps would be smaller but PDFium/SkiaSharp natives need the full runtime image's libs.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
COPY evals/data/master-data.json /app/seed/master-data.json

ENV MockErp__SeedFile=/app/seed/master-data.json \
    DOTNET_gcServer=1
USER $APP_UID
ENTRYPOINT ["dotnet", "InvoiceFlow.Worker.dll"]
