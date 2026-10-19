FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY publish/ ./
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "AIBot.Server.dll"]