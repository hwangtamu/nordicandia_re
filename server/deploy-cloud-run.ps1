param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z][a-z0-9-]{4,28}[a-z0-9]$')][string]$ProjectId,
    [string]$Region = 'us-central1',
    [string]$Service = 'nordicandia-server',
    [string]$DataBucket = "$ProjectId-nordicandia-data"
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gcloud = Get-Command gcloud -ErrorAction Stop

if (-not (gcloud auth list --filter=status:ACTIVE --format='value(account)')) {
    throw 'Google Cloud CLI is not signed in. Run: gcloud auth login'
}

gcloud config set project $ProjectId
gcloud services enable run.googleapis.com cloudbuild.googleapis.com artifactregistry.googleapis.com

$bucketUri = "gs://$DataBucket"
gcloud storage buckets describe $bucketUri 2>$null
if ($LASTEXITCODE -ne 0) {
    gcloud storage buckets create $bucketUri --location=$Region --uniform-bucket-level-access
}

$projectNumber = gcloud projects describe $ProjectId --format='value(projectNumber)'
$runtimeAccount = "$projectNumber-compute@developer.gserviceaccount.com"
gcloud storage buckets add-iam-policy-binding $bucketUri `
    --member="serviceAccount:$runtimeAccount" --role='roles/storage.objectUser'

gcloud run deploy $Service `
    --project=$ProjectId --region=$Region --source=$repo `
    --allow-unauthenticated --port=8080 --use-http2 `
    --timeout=3600 --concurrency=80 --max-instances=1 `
    --set-env-vars='NORD_DATA_DIR=/data,NORD_DUMP_BODY=0' `
    --add-volume="name=nord-data,type=cloud-storage,bucket=$DataBucket" `
    --add-volume-mount='volume=nord-data,mount-path=/data'

$url = gcloud run services describe $Service --project=$ProjectId --region=$Region --format='value(status.url)'
Write-Output "Server URL: $url"
Write-Output "Rebuild the client with: node server/prepare-desktop.mjs `"<Steam folder>`" dist/desktop $($url -replace '^https://','')"
