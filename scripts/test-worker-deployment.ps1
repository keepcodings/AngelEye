$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    dotnet test `
        "AngelEyeBmsBridge.UiTests\AngelEyeBmsBridge.UiTests.csproj" `
        -c Debug `
        --filter "FullyQualifiedName~WorkerDeployment|FullyQualifiedName~DeploymentMode|FullyQualifiedName~DeploymentForm" `
        -p:NoWarn=NETSDK1138
    if ($LASTEXITCODE -ne 0) {
        throw "Worker deployment .NET tests failed."
    }

    docker run --rm `
        -v "${repositoryRoot}:/src" `
        mcr.microsoft.com/dotnet/sdk:8.0 `
        sh -lc "apt-get update >/dev/null && apt-get install -y python3 shellcheck sudo >/dev/null && chmod +x /src/AngelEyeBridgeWorker/systemd/angel-eye-worker-deploy /src/AngelEyeBridgeWorker/systemd/tests/deployment-script-tests.sh && shellcheck /src/AngelEyeBridgeWorker/systemd/angel-eye-worker-deploy /src/AngelEyeBridgeWorker/systemd/tests/deployment-script-tests.sh && visudo -cf /src/AngelEyeBridgeWorker/systemd/angel-eye-worker-deploy.sudoers && /src/AngelEyeBridgeWorker/systemd/tests/deployment-script-tests.sh /src"
    if ($LASTEXITCODE -ne 0) {
        throw "Worker deployment Linux integration tests failed."
    }
}
finally {
    Pop-Location
}
