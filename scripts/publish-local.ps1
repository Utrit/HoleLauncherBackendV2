$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $platform = docker info --format '{{.OSType}}/{{.Architecture}}'
    if ($LASTEXITCODE -ne 0) { throw 'Docker is unavailable.' }
    $runtime = switch ($platform) {
        'linux/x86_64' { 'linux-x64' }
        'linux/aarch64' { 'linux-arm64' }
        default { throw "Unsupported Docker platform: $platform" }
    }

    $output = Join-Path $projectRoot 'artifacts/local'
    $staging = Join-Path $output ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    try {
        dotnet publish src/HoleLauncherBackend.Api/HoleLauncherBackend.Api.csproj -c Release -r $runtime --self-contained false -o "$staging/api"
        if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }

        dotnet publish src/HoleLauncherBackend.AccessService/HoleLauncherBackend.AccessService.csproj -c Release -r $runtime --self-contained false -o "$staging/access"
        if ($LASTEXITCODE -ne 0) { throw 'Access Service publish failed.' }

        dotnet publish src/HoleLauncherBackend.Orchestrator/HoleLauncherBackend.Orchestrator.csproj -c Release -r $runtime --self-contained false -o "$staging/orchestrator"
        if ($LASTEXITCODE -ne 0) { throw 'Orchestrator publish failed.' }

        dotnet publish src/HoleLauncherBackend.Storage/HoleLauncherBackend.Storage.csproj -c Release -r $runtime --self-contained false -o "$staging/storage"
        if ($LASTEXITCODE -ne 0) { throw 'Storage Service publish failed.' }

        dotnet publish src/HoleLauncher.Admin/HoleLauncher.Admin.csproj -c Release -o "$staging/admin"
        if ($LASTEXITCODE -ne 0) { throw 'Avalonia publish failed.' }

        $wwwroot = Join-Path $staging 'api/wwwroot'
        New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
        Copy-Item -Path "$staging/admin/wwwroot/*" -Destination $wwwroot -Recurse
        foreach ($service in @('api','access','orchestrator','storage')) {
            docker build --file Dockerfile --target $service --tag "holelauncherbackend-${service}:local" $staging
            if ($LASTEXITCODE -ne 0) { throw "Packaging $service failed." }
        }
    } finally {
        $resolved = (Resolve-Path -LiteralPath $staging).Path
        $resolvedOutput = (Resolve-Path -LiteralPath $output).Path
        if ((Split-Path -Parent $resolved) -ne $resolvedOutput -or (Split-Path -Leaf $resolved) -notmatch '^[a-f0-9]{32}$') {
            throw "Unexpected staging path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
} finally {
    Pop-Location
}
