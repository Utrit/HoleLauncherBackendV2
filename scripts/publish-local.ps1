param([string]$ArtifactDirectory)

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
    $staging = if ($ArtifactDirectory) {
        [IO.Path]::GetFullPath($ArtifactDirectory, $projectRoot)
    } else {
        Join-Path $output ([Guid]::NewGuid().ToString('N'))
    }
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

        dotnet publish src/HoleLauncherBackend.Repository/HoleLauncherBackend.Repository.csproj -c Release -r $runtime --self-contained false -o "$staging/repository"
        if ($LASTEXITCODE -ne 0) { throw 'Repository Service publish failed.' }

        dotnet publish src/HoleLauncher.Admin/HoleLauncher.Admin.csproj -c Release -o "$staging/admin"
        if ($LASTEXITCODE -ne 0) { throw 'Avalonia publish failed.' }

        $wwwroot = Join-Path $staging 'api/wwwroot'
        New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
        Copy-Item -Path "$staging/admin/wwwroot/*" -Destination $wwwroot -Recurse
        $models = Join-Path $wwwroot 'models'
        New-Item -ItemType Directory -Path $models -Force | Out-Null
        $samples = 'https://raw.githubusercontent.com/Mojang/bedrock-samples/v1.26.60.5'
        Invoke-WebRequest "$samples/resource_pack/models/entity/humanoid.custom.geo.json" -OutFile "$models/humanoid.custom.geo.json"
        Invoke-WebRequest "$samples/resource_pack/models/mobs.json" -OutFile "$models/mobs.json"
        Invoke-WebRequest "$samples/LICENSE.md" -OutFile "$models/LICENSE.md"

        $assembly = Get-ChildItem "$wwwroot/_framework" -File -Filter 'HoleLauncher.Admin*.wasm' | Select-Object -First 1
        if (!$assembly) { throw 'Admin WASM artifact missing.' }
        $version = (Get-FileHash $assembly.FullName -Algorithm SHA256).Hash.Substring(0,12).ToLowerInvariant()
        foreach ($entry in @{
            'index.html' = './main.js'
            'main.js' = './_framework/dotnet.js'
        }.GetEnumerator()) {
            $file = Join-Path $wwwroot $entry.Key
            $source = [IO.File]::ReadAllText($file)
            $updated = $source.Replace($entry.Value + [char]34, "$($entry.Value)?v=$version" + [char]34)
            [IO.File]::WriteAllText($file, $updated)
        }
        if ($ArtifactDirectory) { return }

        foreach ($service in @('api','access','orchestrator','storage','repository')) {
            docker build --file Dockerfile --target $service --tag "holelauncherbackend-${service}:local" $staging
            if ($LASTEXITCODE -ne 0) { throw "Packaging $service failed." }
        }
    } finally {
        if (!$ArtifactDirectory) {
            $resolved = (Resolve-Path -LiteralPath $staging).Path
            $resolvedOutput = (Resolve-Path -LiteralPath $output).Path
            if ((Split-Path -Parent $resolved) -ne $resolvedOutput -or (Split-Path -Leaf $resolved) -notmatch '^[a-f0-9]{32}$') {
                throw "Unexpected staging path: $resolved"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
} finally {
    Pop-Location
}
