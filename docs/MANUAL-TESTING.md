# DataHub: manual testing on the local cluster

How to start the whole DataHub stack on this PC (Rancher Desktop, local Kubernetes) and test it by hand,
the way TBC's LOS and a customer would use it. Background and details: `docs/PROJECT.md` 6.5–6.7.

---

## 1. Before you start (once)

| What | How to check / do it |
|---|---|
| Rancher Desktop is running with Kubernetes | Rancher Desktop window shows "Kubernetes is running"; `kubectl get nodes` lists one `Ready` node |
| A test ZIP of an ORIS company folder | e.g. `.local\test-data\HIRO.zip`. Zip the company folder from `Desktop\logyx\` **outside the tracked repo** (`.local\` is gitignored). Real customer data: never commit it |
| Optional: no browser certificate warnings | `Import-Certificate -FilePath .local\k8s\tls.crt -CertStoreLocation Cert:\CurrentUser\Root` (Windows asks to confirm). Without it, click through the warning |

All commands below run in PowerShell from the repository root.

## 2. Start everything

```powershell
powershell -ExecutionPolicy Bypass -File scripts\local\start-all.ps1 -Rebuild -SmokeZip .local\test-data\HIRO.zip
```

- `-Rebuild` builds the four images from the current code. Leave it out if nothing changed since the last build (much faster).
- `-SmokeZip` runs the automated end-to-end check at the end. Leave it out to skip it.
- Takes about 3–5 minutes with `-Rebuild`, under a minute without.

**Expected result:** every smoke-test line is `PASS`, then `Smoke test: all checks passed.` and the list of addresses below.
If something fails, see section 7.

## 3. Where everything is

| What | Address | Login |
|---|---|---|
| Customer portal | https://datahub.localtest.me | via the invitation link (section 4) |
| API + Swagger (as TBC LOS) | https://api.datahub.localtest.me/swagger | dev token (section 4, step 1) |
| Mailpit (all emails: links and codes) | http://mailpit.localtest.me | none |
| SMS messages | folder `.local\sms-outbox-k8s` (one `.txt` per SMS) | – |
| Seq (logs, traces, metrics) | http://seq.localtest.me | none |
| RabbitMQ management | http://rabbitmq.localtest.me | user `datahub`; password = `rabbitmqPassword` in `.local\k8s\secrets.json` |

`*.localtest.me` resolves to your own PC; nothing leaves the machine.

## 4. The main flow, step by step

### Step 1: Get an API token (acting as LOS)
1. Open https://api.datahub.localtest.me/swagger.
2. Top right, **Select a definition** → **"dev (local token)"**.
3. `POST /dev/token` → **Try it out** → **Execute** (the body is pre-filled: client `tbc-los`, scopes `datahub.invitations datahub.reports`).
4. Copy `access_token` from the response.
5. Switch the definition back to the main API, click **Authorize**, paste the token, **Authorize**, **Close**.

The token is valid for 8 hours.

### Step 2: Create an invitation
1. `POST /invitations` → **Try it out**. The body is pre-filled; change `companyCode` to a new 9-digit number each time
   (e.g. `400000001`), so every test is a separate company:
   ```json
   {
     "companyCode": "400000001",
     "companyName": "შპს მაგალითი",
     "email": "owner@example.ge",
     "phone": "+995555123456",
     "channels": ["email", "sms"]
   }
   ```
   Optional header `Idempotency-Key`: sending the same key again returns the same invitation.
2. **Execute** → `201`. Note down from the response: **`invitationId`**, **`tenantId`** and **`link`**.

### Step 3: Check the messages
- Mailpit (http://mailpit.localtest.me): an email with the link.
- `.local\sms-outbox-k8s`: a `.txt` file with the SMS text and the link.

### Step 4: Upload as the customer
1. Open the `link` in the browser (the link from the email works the same way).
2. Enter the **company code** you used in step 2 → a verification code is sent.
3. Take the 6-digit code from the newest email in Mailpit and enter it.
4. On the upload page choose the ZIP (e.g. `.local\test-data\HIRO.zip`) and upload. The progress bar shows the parts.
5. The status page updates by itself: **queued → processing → processed** (HIRO takes a few seconds).

While it runs, in Swagger: `GET /invitations/{id}` with the `invitationId` shows the invitation status and
`processingStatus` (`Queued`, `Processing`, `Succeeded`/`Failed`).

### Step 5: Read the reports (acting as LOS / Risk)
In Swagger, for each report fill in **`X-Tenant-Id`** = the `tenantId` from step 2:

| Endpoint | Parameters to try |
|---|---|
| `GET /reports/journal-entries` | `fromDate=2024-01-01`, `toDate=2024-12-31`; optional `accountNumber` (e.g. `1210`), `currency` (e.g. `USD`), `page`, `pageSize` |
| `GET /reports/turnover-register` | `fromDate=2024-01-01`, `toDate=2024-12-31`; optional `accountNumber` |
| `GET /reports/balance-sheet` | `toDate=2024-12-31` |

Expected: `200` with figures. Paged reports carry `X-Total-Count`, `X-Page`, `X-Page-Size`, `X-Total-Pages` headers.
For HIRO, compare with the ORIS reports / reference XLSX files (journal for 2024: 1,533 entries).

## 5. Things that should be refused

| Try | Expected |
|---|---|
| On the portal, enter a wrong company code | an error message; no code is sent (nothing new in Mailpit) |
| Enter a wrong verification code | an error with the attempts left; after 5 wrong tries (or 5 minutes) the code stops working and a new one must be requested |
| Upload a file that isn't a ZIP, or a ZIP without `.tps` files | a bilingual error (Georgian/English) with a code such as `UPLOAD_NOT_ZIP` / `ORIS_NO_JOURNAL`; nothing is processed |
| Start a second upload while one is processing | refused until the first finishes |
| A report with a random `X-Tenant-Id` (a new GUID) | `404` |
| A report without `X-Tenant-Id` | `400` |
| A report without the Authorize token | `401` |
| A token from `/dev/token` with `scope` set to only `datahub.invitations`, then a report | `403` |
| Open an invitation link with a changed character | the "invalid link" page |

## 6. Look behind the scenes

- **Seq** (http://seq.localtest.me): events from `datahub-web`, `datahub-api` and `datahub-worker`.
  - Search for the `invitationId` or `tenantId` to follow one company.
  - The worker's `process job` span appears in the same trace as the portal's upload "complete" request.
  - Paste the token part of the invitation link (after `/i/`) into the search: **no results**. Links are logged as `/i/***`.
- **RabbitMQ** (http://rabbitmq.localtest.me): after processing, the queue is empty and has no unacked messages.
- **Pods**: `kubectl get pods -n datahub-local` (all `Running`, the migrator `Completed`).

Optional:
- Upload newer data for the same company again: the reports then use the newest upload.
- Stop and start again (section 8 then section 2 without `-Rebuild`): invitations and reports are still there.

## 7. If something goes wrong

| Problem | What to do |
|---|---|
| `The cluster doesn't answer` | Start Rancher Desktop, wait for "Kubernetes is running", run again |
| `kubectl context is '...'` | `kubectl config use-context rancher-desktop` |
| A page doesn't open / 404 from Traefik | `kubectl get pods -n datahub-local`; wait until all are `Running` |
| Upload stuck in "processing" | `kubectl logs -n datahub-local deploy/datahub-worker` |
| Portal or API errors | `kubectl logs -n datahub-local deploy/datahub-web` (or `deploy/datahub-api`), or look in Seq |
| Database / migration problem | `kubectl logs -n datahub-local job/datahub-migrator` |
| No email in Mailpit | check the address in the invitation; `kubectl logs -n datahub-local deploy/datahub-api` |
| Image build can't download packages | DNS inside the Rancher VM; see `docs/PROJECT.md` 6.4 |

## 8. Stop

```powershell
powershell -ExecutionPolicy Bypass -File scripts\local\k8s-down.ps1          # stop, keep all data
powershell -ExecutionPolicy Bypass -File scripts\local\k8s-down.ps1 -Purge   # stop and delete all data (clean start)
```
