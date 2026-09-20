FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/TicketNow.ServiceDefaults ./src/BuildingBlocks/TicketNow.ServiceDefaults
COPY src/Contracts/TicketNow.Contracts ./src/Contracts/TicketNow.Contracts
COPY src/OrdersService ./src/OrdersService
RUN dotnet publish ./src/OrdersService/TicketNow.OrdersService.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "TicketNow.OrdersService.dll"]
