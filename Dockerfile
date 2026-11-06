# Web-Syscle container image (multi-stage: build with the SDK, run with the small ASP.NET runtime)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Syscle/Syscle.Web/Syscle.Web.csproj Syscle/Syscle.Web/
RUN dotnet restore Syscle/Syscle.Web/Syscle.Web.csproj
COPY Syscle/ Syscle/
RUN dotnet publish Syscle/Syscle.Web/Syscle.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# secrets are NOT baked into the image: pass them as environment variables (see .env.example)
ENTRYPOINT ["dotnet", "Syscle.Web.dll"]
