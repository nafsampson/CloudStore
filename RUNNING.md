# Running CloudStore

CloudStore has two modes in the same web application. **LocalDemo** runs without Azure using SQLite and local files. **Azure** uses your own Azure SQL and Storage resources, with a separate Functions worker for order processing.

Run all commands from the extracted folder containing `CloudStore.slnx`. Use a stable .NET 10 SDK compatible with `global.json`; the first build needs internet access to restore packages. Visual Studio users can open `CloudStore.slnx` and select the appropriate web launch profile.

## 1. Local demo - easiest way to try the application

No Azure account, SQL Server installation, Docker or Functions host is required.

```powershell
dotnet build src/CloudStore.Web --configuration Release --maxcpucount:1
dotnet run --project src/CloudStore.Web --configuration Release --no-build --launch-profile LocalDemo -- demo-setup
dotnet run --project src/CloudStore.Web --configuration Release --no-build --launch-profile LocalDemo
```

Setup prompts for separate passwords for **demo-admin** and **demo-customer**. Each requires 12–256 characters, uppercase and lowercase letters, a digit and punctuation. Input is hidden; nothing appearing while typing is normal. There are no supplied default passwords.

Open **http://127.0.0.1:5267/Login/Index**. Keep the terminal open; Ctrl+C stops the application.

- As the customer, browse products, add a cart item and check out.
- As the administrator in a separate browser profile, manage products and contracts, view orders and mark Ready orders Processed.
- The local background worker advances Pending orders to Ready. Restarting preserves accounts and records.

LocalDemo is Development-only and binds to loopback. Fictional sample records, uploads, SQLite databases and cookie keys are stored in `src/CloudStore.Web/.local-demo/`. Keep that directory private and out of repositories/archives. Repeating completed setup preserves existing data. For a deliberate reset, stop the app, back up or move the directory, then run setup again.

To add another local customer login, stop the app and run:

```powershell
dotnet run --project src/CloudStore.Web --configuration Release --no-build --launch-profile LocalDemo -- create-customer
```

Creating a business customer record through the UI does not create a login account.

## 2. Run the website locally against Azure

This mode accesses real cloud resources and may incur charges. It requires resources you control, valid credentials and network access. It does not fall back to local storage when Azure is unavailable.

### Prepare Azure resources

Create or configure these business resources, using the default names below or matching overrides in `AzureStorage` settings:

| Resource | Default name |
| --- | --- |
| Private Blob container | images |
| Queues | orders-queue, operations |
| Tables | Products, Orders, Cart |
| SMB file share | contracts |
| Azure SQL database | Your chosen accounts database |

The SQL database stores login accounts; retail records live in Tables. Review and apply `database/InitialIdentity.sql` to a fresh accounts database using an authorized database administrator. Normal startup and account commands do not apply migrations automatically. Do not replace a populated database or change existing migration identifiers.

Allow your current client IP through the SQL firewall. Use encrypted SQL connections with certificate validation. The application database user needs SELECT/INSERT/UPDATE/DELETE on `LoginUsers`, `AspNetUserClaims`, `AspNetUserLogins` and `AspNetUserTokens`, plus SELECT on `__EFMigrationsHistory`; schema creation requires a separate authorized operator.

Keep Blob anonymous access disabled. The business storage setting must be a **complete connection string**, not a raw account key.

### Supply credentials privately

In PowerShell, use this helper to set credentials only for the current terminal session. Do not paste real values into source files, command arguments, screenshots or public issues.

```powershell
function Read-SessionSetting {
    param([string]$Name, [string]$Prompt)
    $secret = Read-Host $Prompt -AsSecureString
    $credential = [PSCredential]::new('setting', $secret)
    [Environment]::SetEnvironmentVariable(
        $Name, $credential.GetNetworkCredential().Password.Trim(), 'Process')
    $credential = $null
    $secret = $null
}

Read-SessionSetting 'ConnectionStrings__DefaultConnection' 'Paste the complete SQL connection string'
Read-SessionSetting 'AzureStorage__ConnectionString' 'Paste the complete business storage connection string'
$env:Storage__Mode = 'Azure'
```

A SQL-authentication connection has this structure; replace the placeholders privately before entering it at the hidden prompt:

```text
Server=tcp:<server>.database.windows.net,1433;Database=<database>;User ID=<application-user>;Password=<private-password>;Encrypt=True;TrustServerCertificate=False;
```

Use the **Connection string** field from the storage account's Access keys page. It contains `DefaultEndpointsProtocol`, `AccountName`, `AccountKey` and `EndpointSuffix`. A raw key alone will fail startup validation. Never print the resulting environment-variable values.

### Create website accounts and start

In that same terminal:

```powershell
dotnet build src/CloudStore.Web --configuration Release --maxcpucount:1
dotnet run --project src/CloudStore.Web --configuration Release --no-build --no-launch-profile -- bootstrap-admin
dotnet run --project src/CloudStore.Web --configuration Release --no-build --no-launch-profile -- create-customer
dotnet run --project src/CloudStore.Web --configuration Release --no-build --launch-profile https
```

The account commands prompt for your chosen usernames and hidden website passwords. These are different from the database user's credentials. `bootstrap-admin` creates the first administrator only and refuses an existing administrator; omit it if accounts already exist. Neither command resets passwords or promotes existing accounts.

### Reset an existing Azure website password

From a trusted terminal configured with an authorized SQL connection, run:

```powershell
dotnet run --project src/CloudStore.Web --configuration Release --no-launch-profile -- reset-password
```

Enter the existing username and a new password twice at the hidden prompts. This command uses Identity to update the password hash and security stamp, clears failed-login lockout, and preserves the account's ID, role and enabled status. Existing website sessions are rejected on their next authenticated request. It never creates missing accounts or modifies retail data. LocalDemo deliberately does not accept this Azure-only command.

For Azure Cloud Shell, `scripts/Reset-AzurePasswords.ps1` can run the published website package using the signed-in operator's Microsoft Entra SQL access token. Supply the current published `CloudStore-Password-Reset.zip`, or use `-ArchivePath` to select a current published website ZIP. Do not use the source ZIP. The helper prepares .NET 10 if needed, temporarily permits the current Cloud Shell IP through the SQL firewall, and removes its own rule afterward. The operator must already have database access; no application password is needed. It prompts for `store-admin` and `store-customer` separately. Passwords are never passed as arguments or written to files.

Keep administrator credentials private. Any customer demo credentials published in a README should be dedicated to the demo and never reused elsewhere.

Open **https://localhost:7234/Login/Index** and use the accounts you just created. Use HTTPS because Azure-mode authentication and antiforgery cookies are Secure. If a trusted development certificate is not configured, review and use `dotnet dev-certs https --trust` on a supported development system; this changes the local trust store. Do not use HTTP to bypass the requirement.

### Run the Azure order worker

Azure-mode orders need the separate Functions worker; the website alone will not advance Pending orders to Ready. Use either an already deployed worker or a local Functions host, not both against the same development queue unless that is intentional.

For a local host, use an Azure Functions Core Tools 4 release supporting .NET 10 isolated workers and a **separate host storage account**. In a second PowerShell terminal, change to the solution root, paste the `Read-SessionSetting` helper above, then run:

```powershell
Read-SessionSetting 'StorageConnection' 'Paste the complete business storage connection string'
Read-SessionSetting 'AzureWebJobsStorage' 'Paste the separate host storage connection string'
$env:FUNCTIONS_WORKER_RUNTIME = 'dotnet-isolated'
$env:OrdersQueueName = 'orders-queue'
$env:OrderTable = 'Orders'
$env:BlobContainer = 'images'
$env:FileShareName = 'contracts'
$env:EnableStorageHttpWrites = 'false'

dotnet build src/CloudStore.Functions --configuration Release --maxcpucount:1
Push-Location src/CloudStore.Functions
func start --port 7076
Pop-Location
```

Stop with Ctrl+C. The Functions host may perform its own storage operations. Keep operator HTTP writes disabled; they are not needed for website checkout. Host failures are retried and exhausted order messages go to `orders-queue-poison`; investigate before replaying them.

## 3. Host the application in Azure

The `infra` templates and `.github/workflows` support hosted deployment. This is an operator setup, separate from the local demo. Review subscription eligibility, region/runtime availability and costs first. Budgets notify; they do not stop spending.

1. Review `infra/existing.bicepparam`. Its defaults refer to existing resources and are not portable to another user's subscription. Replace names, region, budget dates and recipients with your own values. For a new environment, also set `provisionCore=true`, globally unique storage/server names and your Entra administrator object ID/login. Select the intended subscription/resource group.

   **Updating the existing live environment:** this checked-in file is a starting configuration, not an export of live settings. Before an infrastructure apply, populate `webOutboundIps` with the current nonempty IPv4 addresses, set `alertEmail` to the intended recipient and set `enableAlerts=true` to retain monitoring notifications. Its default empty recipient and disabled alerts can change the existing action group and monitoring configuration. Review the what-if for these changes. The app deployment workflow does not apply this parameter file.

2. Preview, then apply only after reviewing the actual changes:

```powershell
az deployment group what-if --resource-group YOUR_RESOURCE_GROUP --parameters infra/existing.bicepparam --result-format ResourceIdOnly
az deployment group create --resource-group YOUR_RESOURCE_GROUP --parameters infra/existing.bicepparam --mode Incremental --query properties.outputs
```

3. For a fresh database, apply the reviewed Identity schema. Run `database/GrantManagedIdentity.sql` as the Entra administrator after replacing its placeholder with the deployment's **webIdentityClientId**. This is also the identity client ID used in the hosted connection string; do not use the service principal object ID for this SID-based script. Create initial website accounts from a trusted terminal using an authorized SQL/Entra connection as described above; a hosted managed-identity connection string is not a laptop credential.
4. Add the deployment's individual `webOutboundIpAddresses` to `webOutboundIps`, preview and redeploy. Do not allow every IP address or enable unrestricted Azure-service access. Recheck addresses after hosting changes.
5. Verify the web and worker Key Vault references resolve. The template configures managed identity for hosted SQL and Functions host storage; business storage still uses a key through Key Vault. Role changes can take time to propagate.
6. After creating a GitHub repository, bootstrap `infra/github-identity.bicep` with the exact repository name and deployed app names. Configure protected environments `azure-production`, `azure-preview` and `azure-infrastructure`. Their variables are `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`; production also needs `AZURE_WEBAPP_NAME` and `AZURE_FUNCTIONAPP_NAME`, while infrastructure environments need `AZURE_RESOURCE_GROUP`. Use the publisher identity's client ID for production and the infrastructure identity's client ID for preview/apply.
7. Run the manual app deployment workflow from `main`. It builds/tests that revision and deploys the worker and website. Protect workflow changes and environment approvals; the infrastructure identity has powerful resource-group permissions. No client secret or publish profile is required for OIDC.

A template compiling successfully does not establish that deployment or live Azure acceptance has succeeded. Infrastructure recreation does not restore deleted accounts, orders or files; back up required data separately.

## Editing the MayBell design

The local demo catalog contains six fictional electronics products with sample ZAR prices. The hero and catalog pictures are synthetic, unbranded demo imagery generated with the built-in image-generation tool. They do not represent real manufacturers, product specifications or live offers. Design imagery remains under `src/CloudStore.Web/wwwroot/img/`. Catalogue source photos are offline sample data under `sample-data/catalog/`, outside the website project and published website. Local setup imports them into private local uploads; Azure catalogue photos belong in the private `images` Blob container and are served through authenticated product-image actions. Product images may be PNG or JPEG up to 4 MiB, with the existing dimension checks.

For an existing local setup, stop the website and run this from the solution directory:

```powershell
dotnet run --project src/CloudStore.Web --configuration Release --launch-profile LocalDemo -- demo-refresh-catalog
```

This adds missing electronics demo products and fills missing private image references without resetting accounts, replacing existing uploaded images or overwriting edited product details. Untouched old lifestyle samples are removed only when no cart or saved order references them. It never changes Azure data.

To populate the six existing Azure demo products with their catalogue photos, run `scripts/Move-CatalogImagesToBlob.ps1 -Apply` from an authorized Azure CLI PowerShell session. It checks source-file hashes, backs up the existing product records privately, uploads and verifies all photos before linking them, and uses captured ETags to avoid overwriting concurrent product edits. Identical existing photos are reused. It does not clear products, orders, carts, accounts or contracts. Deploy the current website afterward so its 4 MiB image limit and storage-only catalogue display are active. The script without `-Apply` validates only the local files.

CloudStore uses a Tailwind adaptation of [MayBell](https://github.com/bbulakh/tailwind-ecommerce), with its MIT licence retained in `src/CloudStore.Web/wwwroot/lib/maybell/`. The compiled `maybell.min.css` is included, so Node.js is not required to run or publish the application.

To change the styles, install Node.js with npm, open a terminal in `src/CloudStore.Web`, then run:

```powershell
npm ci --ignore-scripts
npm run css:build
```

Edit `Styles/maybell.css` and the Razor views, then rebuild the CSS. `npm run css:watch` rebuilds while editing. Commit the source, lock file and compiled stylesheet together. Refresh the browser after changes. The application uses its own catalog data and local illustrations; the template's example payment, review and promotional claims are not implemented features.

## Verify and troubleshoot

Run `./scripts/Verify.ps1` to restore, build and run the automated suite. It does not install tools or deploy Azure resources. Optional `-DotNet` and `-Bicep` arguments accept tool paths.

- **Liveness:** `/health/live`, or `scripts/Test-AzureLiveness.ps1 -WebAppName YOUR_APP`. This checks the web process only, not SQL, storage or worker readiness.
- **Login:** verify the database schema, application user's permissions, SQL firewall, HTTPS and the correct mode's website account.
- **Storage parse error:** supply the complete storage connection string rather than the key alone.
- **Pending orders:** verify the Functions host is running, queue/table names match and its business/host storage settings are distinct and valid.
- **Local port in use:** stop the previous instance. For a separate preview, set `$env:LocalDemo__Port = "5268"` in PowerShell before starting, then open `http://127.0.0.1:5268/Login`. The app still binds only to loopback. Clear the override with `Remove-Item Env:LocalDemo__Port` when finished.
- **Monitoring:** confirm real requests, dependencies and worker outcomes in Application Insights. The workbook is in `monitoring/workbook.json`; workspace queries are in `monitoring/queries.kql`. LocalDemo exports no cloud telemetry. Configure/test alert recipients explicitly after cost review.

Before exposing a hosted deployment, test administrator/customer separation, private image/contract access, checkout and idempotent worker processing with synthetic data. Upload format checks are not malware scanning. Keep original library licenses with redistributed assets.
