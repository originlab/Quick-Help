wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x ./dotnet-install.sh
./dotnet-install.sh --channel 8.0
dotnet run -c Release --project ./src/generator -- ./src/books ./out/static
cp -r ./src/templates/wwwroot/static/ ./out/static