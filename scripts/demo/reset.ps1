[CmdletBinding()]
param(
    [ValidateSet('Provision', 'Reset')]
    [string]$Mode = 'Reset',
    [switch]$KeepRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectName = 'coglatas-issue483-demo'
$datasetNamespace = 'issue-483-demo'
$observerEmail = 'demo-observer@example.test'
$executionTaskTitle = 'Issue 483 Demo: execute synthetic report'
$executionIdempotencyKey = 'issue-483-demo-execution-v1'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$baseCompose = Join-Path $repoRoot 'infra/compose/test/real-backend-smoke.yml'
$demoCompose = Join-Path $repoRoot 'infra/compose/demo/dataset.yml'
$port = if ($env:COGLATAS_DEMO_PORT) { $env:COGLATAS_DEMO_PORT } else { '8088' }
$baseUrl = "http://127.0.0.1:$port"

if ($env:COGLATAS_DEMO_MODE -cne '1') {
    throw 'Refusing to run. Set COGLATAS_DEMO_MODE=1 explicitly; this command is Test/demo-only.'
}
if ([string]::IsNullOrWhiteSpace($env:COGLATAS_DEMO_PASSWORD)) {
    throw 'Refusing to run without COGLATAS_DEMO_PASSWORD. Supply it locally; do not commit it.'
}
if ($env:COGLATAS_DEMO_EMAIL -and $env:COGLATAS_DEMO_EMAIL -notmatch '^[^@\s]+@example\.test$') {
    throw 'COGLATAS_DEMO_EMAIL must use the synthetic @example.test domain.'
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required for the isolated demo stack.'
}

$compose = @(
    'compose', '--project-name', $projectName,
    '--file', $baseCompose,
    '--file', $demoCompose
)

function Invoke-DemoCompose {
    param([string[]]$Arguments)

    & docker @compose @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Demo Compose command failed (exit code $LASTEXITCODE)."
    }
}

function Get-CsrfToken {
    param([Microsoft.PowerShell.Commands.WebRequestSession]$Session)

    $response = Invoke-WebRequest -Uri "$baseUrl/api/security/csrf-token" -WebSession $Session
    if ($response.StatusCode -ne 200) {
        throw "CSRF token request returned HTTP $($response.StatusCode)."
    }
    $token = $response.Content | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($token.token) -or [string]::IsNullOrWhiteSpace($token.headerName)) {
        throw 'CSRF token response is incomplete.'
    }
    return $token
}

function Login-DemoUser {
    param([string]$Email)

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $csrf = Get-CsrfToken -Session $session
    $payload = @{ email = $Email; password = $env:COGLATAS_DEMO_PASSWORD } | ConvertTo-Json -Compress
    $response = Invoke-WebRequest -Uri "$baseUrl/api/auth/login" -Method Post -WebSession $session `
        -Headers @{ $csrf.headerName = $csrf.token } -ContentType 'application/json' `
        -Body $payload
    if ($response.StatusCode -ne 200) {
        throw "Demo login returned HTTP $($response.StatusCode)."
    }
    return $session
}

function Get-ExecutionTaskId {
    $escapedTitle = $executionTaskTitle.Replace("'", "''")
    $query = "SELECT `"Id`" FROM task_items WHERE `"Title`" = '$escapedTitle' AND `"DeletedAt`" IS NULL;"
    $taskId = (& docker @compose exec -T postgres psql --tuples-only --no-align -U coglatas_smoke -d coglatas_smoke -c $query |
        Select-Object -First 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $taskId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'The execution-ready Issue #483 Demo Task was not found.'
    }
    return $taskId
}

function Assert-DemoDatabaseInvariants {
    $query = @"
SELECT CASE WHEN
    (SELECT count(*) FROM workspaces WHERE "Slug" = 'issue-483-demo-workspace' AND "DeletedAt" IS NULL) = 1 AND
    (SELECT count(*) FROM projects WHERE "Slug" = 'issue-483-demo-project' AND "DeletedAt" IS NULL) = 1 AND
    (SELECT count(*) FROM task_items WHERE "Title" LIKE 'Issue 483 Demo:%' AND "DeletedAt" IS NULL) >= 3 AND
    (SELECT count(*) FROM research_plans) >= 1 AND
    (SELECT count(*) FROM attachments WHERE "StorageProvider" = 'issue-483-demo' AND "ScanStatus" = 'Clean' AND "DeletedAt" IS NULL) = 1 AND
    (SELECT count(*) FROM conversations WHERE "Title" = 'Issue #483 Demo Conversation') = 1 AND
    (SELECT count(*) FROM messages WHERE "Body" = '[issue-483-demo] Synthetic conversation message for the demo.' AND "DeletedAt" IS NULL) = 1 AND
    (SELECT count(*) FROM announcements WHERE "Title" = 'Issue #483 Demo: published announcement' AND "DeletedAt" IS NULL) = 1 AND
    (SELECT count(*) FROM announcement_drafts WHERE "Title" = 'Issue #483 Demo: draft announcement' AND "Status" = 'Draft') = 1 AND
    (SELECT count(*) FROM announcement_drafts WHERE "Title" = 'Issue #483 Demo: scheduled announcement' AND "Status" = 'Scheduled') = 1 AND
    (SELECT count(*) FROM audit_logs WHERE "Action" = 'DemoDatasetProvisioned' AND "CorrelationId" = 'issue-483-demo') = 1
THEN 'OK' ELSE 'FAILED' END;
"@
    $result = (& docker @compose exec -T postgres psql --tuples-only --no-align -U coglatas_smoke -d coglatas_smoke -c $query |
        Select-Object -First 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $result -ne 'OK') {
        throw 'The Issue #483 demo dataset database invariants failed.'
    }
}

function Assert-UnauthorizedObserverCannotReadTask {
    param(
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [string]$TaskId
    )

    try {
        $null = Invoke-WebRequest -Uri "$baseUrl/api/tasks/$TaskId/execution-scope" -WebSession $Session
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -notin 403, 404) {
            throw
        }
        return
    }
    throw 'The observer unexpectedly received protected Task data.'
}

Push-Location $repoRoot
try {
    Invoke-DemoCompose -Arguments @('config', '--quiet')
    if ($Mode -eq 'Reset') {
        # This fixed Compose project name owns the only volumes removed here.
        Invoke-DemoCompose -Arguments @('down', '--volumes', '--remove-orphans')
    }
    Invoke-DemoCompose -Arguments @('up', '--build', '--wait', 'app')

    Assert-DemoDatabaseInvariants
    $ownerEmail = if ($env:COGLATAS_DEMO_EMAIL) { $env:COGLATAS_DEMO_EMAIL } else { 'demo-operator@example.test' }
    $ownerSession = Login-DemoUser -Email $ownerEmail
    $taskId = Get-ExecutionTaskId
    $scope = Invoke-WebRequest -Uri "$baseUrl/api/tasks/$taskId/execution-scope" -WebSession $ownerSession
    if ($scope.StatusCode -ne 200) {
        throw "The execution scope check returned HTTP $($scope.StatusCode)."
    }
    $scopeBody = $scope.Content | ConvertFrom-Json
    if (-not $scopeBody.effectivePolicy.projectFilesEnabled -or $scopeBody.effectivePolicy.webEnabled) {
        throw 'The synthetic execution Task does not have the expected no-Web, project-file-only policy.'
    }

    $runCsrf = Get-CsrfToken -Session $ownerSession
    $run = Invoke-WebRequest -Uri "$baseUrl/api/tasks/$taskId/execution-runs" -Method Post -WebSession $ownerSession `
        -Headers @{ $runCsrf.headerName = $runCsrf.token; 'Idempotency-Key' = $executionIdempotencyKey } -ContentType 'application/json' `
        -Body '{}'
    if ($run.StatusCode -ne 201) {
        throw "The Task execution Golden Path returned HTTP $($run.StatusCode)."
    }
    $runBody = $run.Content | ConvertFrom-Json
    if ($runBody.status -ne 'Succeeded' -or [string]::IsNullOrWhiteSpace($runBody.id)) {
        throw 'The Task execution Golden Path did not produce a durable successful result.'
    }
    $result = Invoke-WebRequest -Uri "$baseUrl/api/tasks/$taskId/execution-result" -WebSession $ownerSession
    if ($result.StatusCode -ne 200) {
        throw "The durable Task execution result returned HTTP $($result.StatusCode)."
    }

    $observerSession = Login-DemoUser -Email $observerEmail
    Assert-UnauthorizedObserverCannotReadTask -Session $observerSession -TaskId $taskId

    [pscustomobject]@{
        dataset = $datasetNamespace
        mode = $Mode
        url = "$baseUrl/app/login"
        ownerEmail = $ownerEmail
        observerEmail = $observerEmail
        executionTaskId = $taskId
        taskExecutionStatus = $runBody.status
    } | ConvertTo-Json
}
finally {
    if (-not $KeepRunning) {
        Invoke-DemoCompose -Arguments @('down', '--remove-orphans')
    }
    Pop-Location
}
