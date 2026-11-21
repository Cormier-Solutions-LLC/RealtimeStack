FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/RealtimeStack.Accounts --locked-mode \
 && dotnet publish src/RealtimeStack.Accounts -c Release --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:7c625e44e5d588b4d7b2a913e94ca4bd5098ce47442da404829e4c7936a764e1
WORKDIR /app
COPY --from=build /publish/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
USER 1654:1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "RealtimeStack.Accounts.dll"]
