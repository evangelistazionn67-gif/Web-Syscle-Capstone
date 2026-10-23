FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Syscle/Syscle.Web/Syscle.Web.csproj", "Syscle/Syscle.Web/"]
RUN dotnet restore "Syscle/Syscle.Web/Syscle.Web.csproj"
COPY . .
WORKDIR "/src/Syscle/Syscle.Web"
RUN dotnet publish "Syscle.Web.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Syscle.Web.dll"]
