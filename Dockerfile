# Один Dockerfile собирает сервер или клиент по аргументу APP_PROJECT.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
ARG APP_PROJECT=Server
COPY . .
RUN dotnet restore "${APP_PROJECT}/${APP_PROJECT}.csproj"
RUN dotnet publish "${APP_PROJECT}/${APP_PROJECT}.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
ARG APP_PROJECT=Server
ENV APP_PROJECT=${APP_PROJECT}
COPY --from=build /app/publish .
ENTRYPOINT ["sh", "-c", "dotnet \"$APP_PROJECT.dll\""]
