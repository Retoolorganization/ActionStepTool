# Actionstep REST Client

Small .NET 8 WPF REST client using the supplied Actionstep client's login and token-refresh flow, with the supplied Material Design 2 theme.

## Run

1. Extract the project and open `ActionstepGet.csproj` in Visual Studio with the .NET desktop development workload and .NET 8 SDK installed.
2. Copy your existing `appsettings.actionstep.json` beside the project file. It is ignored by Git and copied to build/publish output when present.
3. Check `Actionstep:Environment` in `appsettings.json`. This project retains **Staging** from the supplied configuration. Use **Production** when appropriate. The selected environment appears in the window.
4. Restore NuGet packages, build, and run. Sign in through the browser.

The credential keys are unchanged: `Actionstep:Staging:ClientId`, `Actionstep:Staging:ClientSecret`, and the corresponding `Production` keys. Your existing credential file can also override the other Actionstep configuration values.

## Requests

Choose **GET** or **POST**, then enter an Actionstep REST path. Either of these endpoint forms is accepted:

```text
GET /api/rest/actionparticipants?participant=12345&include=action,participantType&pageSize=200&page=1
actionparticipants?participant=12345&include=action,participantType&pageSize=200&page=1
```

Full HTTPS REST URLs are also accepted when they belong to the signed-in API endpoint. Encode spaces in query values as `%20` or `+`.

For POST, enter valid JSON in the request-body field. The request uses the `application/vnd.api+json` content type; shape the body and endpoint according to the Actionstep API resource you are creating.

JSON shows the response body as received for a single request. YAML converts that JSON for display; string values stay strings, including dates, numeric-looking IDs, and empty strings. Copy uses the currently displayed representation. Non-JSON responses remain visible as received, with a notice if YAML conversion fails.

For GET, Fetch all pages starts with the requested page and follows `meta.paging[resource].nextPage` until it is null. It leaves filters and includes in place. If paging metadata is missing, it stops and reports that instead of guessing. Multiple responses are grouped as:

```json
{
  "pages": [
    { "actions": [], "meta": {} },
    { "actions": [], "meta": {} }
  ]
}
```

This is an illustrative shape, not a sample of actual returned records. Each page's original response envelope is retained, including linked records and metadata. A failed HTTP response is also retained and stops pagination. Stop cancels the current operation and retains responses already received. Repeated request URLs stop pagination with an error. Pagination does not provide a transactional snapshot if the source data changes between requests.

API reference: https://docs.actionstep.com/api-responses/

## Sessions

Tokens are encrypted with Windows user protection and saved under `%LOCALAPPDATA%\ActionstepGet`. Cache files are separated by environment and client ID. This tool does not share the interest tool's token file. Log Out clears this tool's cached session and response display.

## Publish

```powershell
dotnet publish ActionstepGet.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Keep the copied JSON configuration files alongside the published executable.

## Validation status

Build the project with the .NET 8 SDK on Windows. Live Actionstep login and GET/POST requests require valid environment configuration and have not been tested against an Actionstep account.

Before sending a POST, confirm the selected environment in the header and verify the endpoint and JSON body. POST is a write operation and may create or modify records.
