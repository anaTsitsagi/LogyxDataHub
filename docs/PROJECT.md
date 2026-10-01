# Logyx DataHub for TBC Bank: project reference

_Last updated: 2026-10-01. Steps 0–5a are merged into `main` (PR #1). Step 6 is in progress on branch `feature/step6-observability`._

**This file is the single reference for the project:** context, requirements, every decision, architecture, status per step, how to run it, open items and lessons learned.
- Read it at the start of any work session.
- Update it whenever a step finishes, a decision is made or something surprising is found, and commit it with that work.
- **It is committed to the repository, so it never contains secret values**, only where secrets live.

Contents:
1. Context
2. TBC's mandatory requirements
3. Decision log
4. Architecture
5. Step-by-step plan and status
6. Running locally
7. Verification plan and test inventory
8. Open items
9. Security and handling rules
10. Problems found and fixed

---

## 1. Context

Logyx is the vendor building **DataHub** for TBC Bank. The system description is `TBC DATAHUB.pdf` on the user's OneDrive Desktop (7 architecture pages). The flow described there:

1. A business customer applying for a loan gets a **unique link**.
2. They verify themselves with **email/phone + company ID + a one-time code (OTP)**.
3. They upload their **ORIS accounting database** (a ZIP of `.tps` files).
4. A background **worker** parses it into **MSSQL**.
5. **REST APIs** serve the data to TBC's systems: **LOS** (Loan Origination System) and Risk.

Logyx must also provide TBC with a **testing environment**.

### Starting point (before this work)
- The repo was a single .NET 8 Web API project that read a pre-loaded `dbo.HIRO_WIRING` table and used a dev-only JWT.
- It had no portal, upload, parser, worker, queue, Docker files or tests.
- `appsettings.json` contained a committed Azure SQL admin password and the JWT key (see 8.3: still to be rotated).
- The old untracked copy `source\repos\LogyxDataHub` still exists. Delete it only after the user confirms.

### Sample data (never committed; real customer data)
- `Desktop\logyx\` (the OneDrive Desktop) holds ORIS sample databases for three companies (AILABI, HIRO, ALTERA_SOLUSHEN) under `ORIS DB\ORIS 5\`.
- Also there: a reference `WIRING.TPS` (a different company from HIRO, **without** its `Rate.tps`), `WIRING-UNNAMED.CSV`, and reference XLSX files (`HIRO.xlsx`, `ALTERA_SOLUTION.xlsx`, and those in `balance\` and `TBC APIs Data Hub\`).
- `logyx DataHub API.docx` is the API contract.
- Biggest sample ZIP: 16.5 MB. HIRO `WIRING.TPS` is 1.9 MB, `Acc_name.tps` 72 KB; a ZIP of the HIRO folder is about 12.3 MB.
- 6 of HIRO's 66 TPS files are **encrypted**: Access, ACCOUNT, CRT, Dtd, Prj_name, Projects.

---

## 2. TBC's mandatory requirements (translated from the original Georgian)

| # | Requirement | How we meet it |
|---|---|---|
| 1 | Central logging through Serilog, sent as GELF or OTLP/HTTP | **OTLP/HTTP only** (user's decision), via `Serilog.Sinks.OpenTelemetry` (step 6; code done, live check pending) |
| 2 | Containerized | `deploy/docker/Dockerfile`: multi-stage build, one target per app, chiseled image, non-root user (step 6; build pending Rancher Desktop) |
| 3 | Updates delivered to TBC's SFTP, as a zipped Docker image and/or a Helm chart plus image | `package-release.ps1` + `upload-sftp.ps1` (step 9) |
| 4 | MSSQL | EF Core 10 on SQL Server, `datahub` schema (done) |
| 5 | AWS S3 | AWSSDK.S3 (done; verified live against SeaweedFS locally) |
| 6 | RabbitMQ | RabbitMQ.Client 7.2.2 (done; verified live against RabbitMQ 4.3.6 locally) |
| 7 | Resource-use estimate for processing plus Kubernetes requests/limits | Load test + `docs/resource-estimate.md` (step 8) |

---

## 3. Decision log

### 3.1 Decisions made by the user
| Topic | Decision |
|---|---|
| Test environment location | **The user's local machine only** (Win10 Pro 19045, 16 GB RAM, 4 threads) |
| Scope | **Full end-to-end flow** |
| API contract | **`logyx DataHub API.docx` only.** Three endpoints: `/reports/journal-entries`, `/reports/turnover-register`, `/reports/balance-sheet` |
| Repository | **Restructure in the same repo** (LogyxDataHubRepo) |
| Who creates the unique links | **TBC LOS, through our Invitations API** |
| SMS | **Needed.** LOS chooses **SMS and/or email** for each invitation |
| Logging format | **One format only: OTLP/HTTP** ("no need for two"). GELF is not implemented |
| Upload size | ZIPs can be **up to 2 GB** |
| File transport | The file goes to **S3**. **The RabbitMQ message carries only the S3 location**, never the file |
| Upload types in the portal | The user chooses: **ORIS database ZIP** (now) or **ORIS entries sheet CSV** (later phase; no logic exists yet) |
| WIRING field mapping | Confirmed: `entryNumber = RANGE`, `postingDate = REAL_DATE`, `operationDate = DATE` |
| Account-number rule | Confirmed: "3120 - yes first 4 digits are the account, then 1 is also sub and 193 is the sub of 1". Example: `3 1 20 1 193` gives account `3120` with sub-account path `1 193` |
| turnover-register naming | **Paused.** Keep the docx names for now |
| mTLS / Kubernetes version | **Unknown.** The design supports both options |
| Order of work | Commit, then the parser spike, then step 2, step 3, step 4 (each confirmed by the user) |
| Test locally before pushing | 2026-10-01: the user tested the portal and API locally, then asked to commit and push |
| Project reference | 2026-10-01: this file lives in the repo (`docs/PROJECT.md`) so it can be read at any time |
| Local OTLP receiver (step 6) | 2026-10-01: **Seq for Windows** (`winget install Datalust.Seq`), the same tool step 7 uses |
| Rancher Desktop | 2026-10-01: **install now**, so the images are built and checked within step 6 |
| Branch for step 6 | 2026-10-01: PR #1 was merged; step 6 runs on a **new branch `feature/step6-observability` from `main`** |

### 3.2 Technical choices made during the build (the user can revisit any of them)

**Platform and structure**
- **.NET 10 (LTS)**, SDK 10.0.401 installed via winget; all projects target `net10.0`.
- **Clean layering:**
  - Domain: entities and enums.
  - Application: use cases and interfaces.
  - Infrastructure: EF Core, S3, RabbitMQ, SMTP, SMS.
  - Oris: the TPS reader.
  - Host apps: Web, Api, Worker (Migrator comes later).
- The legacy API moved to `src/LogyxDataHub/`, because at the repo root it globbed the new source files. The Azure workflow publish path was updated to match.

**Parser**
- **TpsParser 6.0.1** (open-source C# TopSpeed reader) instead of porting the Java `tps-to-csv`.
- Workaround: TpsParser throws a NullReferenceException on GROUP fields. We request only non-group fields and flatten any `ClaGroup` values ourselves.
- **Georgian decoding table written by us.** ORIS's `oris_byte_to_unicode.json` turned out to be only an identity map from the font. The actual mapping:
  - bytes `0xC0` and up → `აბგდევზჱთიკლმნჲოპჟრსტჳუფქღყშჩცძწჭხჴჯჰჵ`
  - ASCII stays as is
- **Clarion dates** are days since 1800-12-28 (for example, 80692 = 2021-12-01). This also explains the wrong 2119/2120 dates in the old examples.
- Encrypted or corrupt TPS files raise `OrisFileException`, which the worker reports as `ORIS_UNREADABLE`.

**GEL conversion** (`DataHub.Oris/GelConverter.cs`)
- AmountGel = round(MONEY × rate, 2, half away from zero).
- The rate is the line's CURS if > 0; otherwise the latest `Rate.tps` rate on or before the operation date.
- A blank MON_TYPE means GEL.
- A missing rate fails the job with `ORIS_RATES_MISSING`. `Rate.tps` is optional; without it, foreign lines need a rate on the line.
- `SqlDatasetStore` refuses foreign-currency lines that were not converted (they must go through `GelConverter` first).
- Checked to the tetri against ORIS's 2024 HIRO turnover register.

**Database**
- EF Core 10.0.12 SqlServer, schema `datahub`, migrations history table in the same schema.
- `EnableRetryOnFailure` is on.
- `Database:CompatibilityLevel` is configurable: default **150** (SQL 2019); **130** for LocalDB (SQL 2016) in tests and local runs.
- Row tables (`Accounts`, `JournalEntries`) use a **composite clustered key (DatasetId, Id)**. A dataset's rows are contiguous, which makes tenant-scoped reads and batch deletes cheap.
- Money is `decimal(19,4)`. Float sums in the old code were a bug.
- **SqlBulkCopy in batches of 10,000 rows**, so memory is bounded whatever the file size.
- **The apps never apply migrations.** `DataHub.Migrator` does (`DatabaseMigrator`; later a Helm pre-install/pre-upgrade Job). It creates the database if missing, logs what it applies, and exits 1 on failure. EF Core takes a database lock while migrating, so concurrent runs are safe. Its connection needs DDL rights; the apps' connections don't.
- **Dataset lifecycle:**
  - Staging → Active, or Superseded/Failed.
  - Activation is one transaction with an `UPDLOCK, HOLDLOCK` on the company row.
  - **Older data never replaces newer data.** A late-finishing older upload is marked Superseded.
  - Purge deletes rows with `DELETE TOP (50000)` in a loop, which keeps locks and log growth small.

**Invitations and verification**
- **The link token is HMAC-derived; only its SHA-256 hash is stored.** An idempotent replay therefore returns the *same* link without storing the token.
- **Idempotency:** optional `Idempotency-Key` header, unique per `(CreatedByClient, IdempotencyKey)`. A repeat with the same key creates nothing, sends nothing and returns 200 with the original invitation; a new key returns 201. Purpose: LOS can safely retry after a network failure. Must be in the API documentation for TBC.
- Validation:
  - company code must be 9 or 11 digits
  - Georgian phone numbers are normalised to `+9955XXXXXXXX`
  - every chosen channel needs its contact field
- One company per company code: invitations with the same code share the company and its `tenantId`, and a new upload replaces that company's data.
- Delivery is **best effort per channel**; the result reports which channels were delivered.
- **OTP:** 6 digits, stored hashed.
  - Valid for 5 minutes; at most 5 attempts.
  - 60 s cooldown between sends; at most 5 codes per hour.
  - Sent by **email when the invitation has one, otherwise SMS**.
  - The destination is shown masked (e.g. `O***@Hiro.ge`).
- The same error (`DETAILS_MISMATCH` / `LINK_INVALID`) is returned for a wrong link and for wrong details, so the form doesn't reveal which invitations exist.
- `SecurityOptions.SigningKey` is base64 of at least 32 bytes.
- Invitation link lifetime: 14 days. `Invitations:PortalBaseUrl` sets the link's base address.

**Upload**
- **Chunked, resumable upload of 16 MB chunks** (`Uploads:ChunkSizeBytes`). Each chunk becomes one **S3 multipart part** (the S3 minimum is 5 MB).
  - The browser sends 3 chunks in parallel, retries, and remembers progress in localStorage, so re-selecting the file resumes.
  - The Web app buffers only one chunk in memory (the request limit is 64 MB).
- Every part must be exactly the expected size.
- Completion checks that all parts are present and the total size matches.
- **The ZIP is inspected by ranged reads from S3** (1 MB blocks through `S3RangeReadStream`), so only the central directory is fetched. Checks:
  - path traversal (`/`, `../`, `:`)
  - entry count (max 5,000)
  - uncompressed total (max 20 GB)
  - compression ratio (max 200:1, the zip-bomb guard)
  - at least one `.tps` file
- **The browser does not compute SHA-256**; hashing 2 GB in a browser isn't practical. The worker computes and records it.
- A new upload is refused while a job is Queued or Processing (`UPLOAD_PROCESSING`).
- The S3 key is `uploads/{tenantId:N}/{uploadId:N}/{ascii-safe-name}`. The original (possibly Georgian) file name is kept in the database. Server-side encryption uses **AES256** (the S3 server must support SSE-S3).
- The job row is committed **before** publishing. If the publish fails, the job is still recovered by the sweep (see the worker section).
- The CSV option is **shown but disabled** (`Uploads:EntriesCsvEnabled=false`).

**Messaging**
- RabbitMQ topology:
  - exchange `datahub.jobs` (direct), routing key `process`
  - queue `datahub.processing` (**quorum**; `classic` locally), with dead-letter exchange `datahub.jobs.dlx` (fanout) feeding queue `datahub.processing.dead`
  - **`x-delivery-limit` = 10** on quorum queues
- **Publisher confirms**, persistent messages, `mandatory: true`, message type `datahub.processing-job.v1`.
- One long-lived connection per process with automatic recovery. The Web app connects lazily (on the first publish), so it starts without RabbitMQ.
- Message body: `{jobId, uploadId, companyId, tenantId, uploadType, s3Bucket, s3Key, sizeBytes, sha256, correlationId}`.

**API (DataHub.Api)**
- JWT bearer against a configurable **OIDC authority/audience** (TBC's IdP, still unknown).
- **Dev tokens are issued only in the `Local` environment** (`POST /dev/token`). Defaults: client `tbc-los`, scopes `datahub.invitations datahub.reports`.
- Policies: `invitations` (scope from `Auth:InvitationsScope`) and `reports` (scope `datahub.reports`, from `Auth:ReportsScope`). A token with the wrong scope gets 403.
- The caller's client id comes from the `client_id`, `azp`, `appid` or `sub` claim. `GET /invitations/{id}` returns only invitations created by the same client.
- Errors are **ProblemDetails with a stable `code`**.
- **Swagger:**
  - document `v1` is the TBC contract, with bearer auth
  - document `dev` exists **only in the Local environment**; it adds `/dev/token` (pre-filled with working defaults via `[DefaultValue]`)
  - `OpenApiExamples.cs` (a schema filter) gives `POST /invitations` a valid **example** body. It is an example, not a default, because `v1` is the contract TBC reads.
- Health: `/health/live` (no checks) and `/health/ready` (database). The portal has the same two endpoints.

**Portal (DataHub.Web)**
- ASP.NET Core MVC, Georgian first with English alongside.
- Session: cookie `__Host-datahub` (HttpOnly, SameSite Strict, Secure), 60-minute sliding expiry. `/portal-api` answers 401 instead of redirecting.
- **`__Host-` cookies require HTTPS**, so the portal must run on HTTPS even locally (the `http` profile fails with a 500 on the antiforgery cookie).
- CSRF: antiforgery on every form and every script request (header `X-CSRF-TOKEN`, cookie `__Host-datahub-csrf`). Forms carry an explicit `asp-antiforgery="true"`.
- Headers: CSP `default-src 'self'`, `frame-ancestors 'none'`, `Referrer-Policy: no-referrer` (the link token is in the URL), `X-Frame-Options: DENY`, `nosniff`.
- Rate limiting: 20 requests per minute per IP on the anonymous verification pages.
- Georgian is output as-is (`UnicodeRanges.All`), not as HTML entities.
- Friendly error pages are shown for browser navigation only; the JSON upload API keeps its raw status codes.
- **Data Protection keys** (which encrypt the session and anti-forgery cookies) are stored in the database table `datahub.DataProtectionKeys` (migration `AddDataProtectionKeys`, application name `datahub-web`). Without this, each pod would have its own keys: a customer routed to another replica, or a pod restart, would end the session mid-upload. The keys are not encrypted at rest beyond the database's own protection (see 8.2).

**Notifications**
- Email: SMTP via MailKit; Mailpit locally.
- SMS: `Sms:Provider=Outbox`, which writes each message to a text file (`.local/sms-outbox`) for local testing. Any other provider fails at startup until TBC's gateway adapter is written.
- **The customer is notified only when processing fails**, on the invitation's channels. Success is visible on the portal status page and to LOS through `GET /invitations/{id}`.
- Failure messages never contain internal detail; that goes only into `ProcessingJob.ErrorDetail` and the logs.

**Worker (DataHub.Worker)**
- One job at a time per replica (prefetch 1, manual ack). Scaling is by adding replicas.
- **The database is the source of truth**; the message only routes. The worker claims a job with a conditional `UPDATE`: status must be Queued, or Processing with a stale heartbeat.
- The ZIP is downloaded to a temp file on disk (deleted on close). The worker checks the size and SHA-256.
- **One company per ZIP.** Two `WIRING.TPS` files cause `ORIS_MULTIPLE_DATABASES`.
- **`Acc_name.tps` is optional.** If it's missing, the import runs with empty account names and a logged warning.
- `WIRING.TPS` is required. Missing → `ORIS_NO_JOURNAL`; zero records → `ORIS_EMPTY_JOURNAL`.
- Tables are extracted one at a time to temp files, with an uncompressed-size limit enforced again during extraction.
- **Heartbeat** every 30 s, on a separate DB scope. A job is stale after 3 minutes.
- **Error classification:**
  - Permanent: validation or content errors, `OrisFileException`, `InvalidDataException`. The job fails immediately and the customer is notified.
  - Transient: anything else. The job goes back to Queued and is retried; after **3 attempts** it fails with `PROCESSING_FAILED`.
- **Recovery sweep** (every 1 minute), which is safe with several replicas:
  - republishes jobs Queued for more than 5 minutes, at most once per 5 minutes per job
  - requeues Processing jobs with a stale heartbeat (oldest heartbeat first), or fails them if they're on their last attempt
- **Shutdown:** an in-flight job is handed back (status Queued, attempt count restored, message nacked with requeue). The shutdown timeout is 25 s, so the Kubernetes grace period must be longer.
- Unexpected handler errors (e.g. the database is unreachable): pause 15 s, then nack with requeue. The delivery limit dead-letters a message that keeps failing.
- Unreadable messages are rejected straight to the DLQ.
- Purge of inactive datasets runs every 15 minutes.
- **Probes (step 6):** the worker is a small web host (port 8080 in containers, `http://localhost:5290` locally) serving only health endpoints. An exec probe isn't possible because chiseled images have no shell.
  - `/health/live`: the maintenance loop ran within `Processing:LivenessTimeout` (15 min; it keeps looping even when the database or broker is down, so a stopped loop means the process is stuck).
  - `/health/ready`: the database is reachable and the RabbitMQ consumer channel is open.
- **Uploaded ZIPs are kept in S3** after processing, pending TBC's retention rule. The plan is an S3 lifecycle rule; incomplete multipart uploads are also aborted by a lifecycle rule.

**Reports** (`DataHub.Application/Reports`, `ReportsController`)
- Classes: `ReportService`, `AccountFilter`, `StandardChart`, `BalanceSheetMapping`, `BalanceSheetBuilder`.
- `X-Tenant-Id` is required and must be a GUID. A `tenantId` in the query must match it. Unknown tenant → 404; no active dataset yet → clear ProblemDetails.
- Queries read only the company's **active** dataset. Dates are operation dates (ORIS DATE) and **inclusive**.
- **Journal:** the response is a JSON array of lines in their own currency plus `amountGel`. Paging in the `X-Total-Count`, `X-Page`, `X-Page-Size` and `X-Total-Pages` headers; page size max 500; stable order (date, entry number, id).
- **Turnover:** rows for every class, group, account and sub-account level, with name and level fields; opening, turnover and closing; all amounts in GEL.
- **Balance sheet:** accounts map to lines by longest prefix; the mapping is configurable at `Reports:BalanceSheet:Lines`. **The default mapping needs review by an accountant** (see 8.2).

**Logging and telemetry (step 6, `DataHub.Hosting`)**
- One registration for every host: `builder.AddDataHubObservability("datahub-<app>")`.
- **Serilog** writes the console and, when `Otlp:Endpoint` is set, **OTLP/HTTP protobuf** to `{Endpoint}/v1/logs`. In containers (`DOTNET_RUNNING_IN_CONTAINER`) the console is compact JSON; locally it is readable text.
- **OpenTelemetry traces** (ASP.NET Core, HttpClient, SqlClient, AWS S3, RabbitMQ.Client's own sources, and DataHub's `process job` span) and **metrics** (ASP.NET Core, HttpClient, runtime, DataHub meter) go to `{Endpoint}/v1/traces` and `/v1/metrics`. Nothing is registered when no endpoint is set.
- RabbitMQ.Client 7 carries the trace context in message headers, so a worker job joins the trace of the upload that queued it. `CorrelationId` (the upload's trace id) is on every job log line.
- DataHub metrics (`DataHubTelemetry`): `datahub.job.duration` (s) and `datahub.jobs.finished` (tags `outcome`, `error.code`), `datahub.import.journal_lines`, `datahub.uploads.completed`, `datahub.uploads.bytes`.
- Settings: `Otlp:Endpoint`, `Otlp:Headers` (`key=value,key2=value2`, from a Secret) and `Otlp:ExportMetrics` (default on; off in the local config until it is confirmed whether Seq accepts OTLP metrics). Resource attributes: `service.name`, `service.version`, `service.instance.id` (the pod name) and `deployment.environment.name`.
- Log levels: the `Serilog:MinimumLevel` section, which replaces the old `Logging:LogLevel` (e.g. `Serilog__MinimumLevel__Default=Debug`). ASP.NET Core, EF Core and HttpClient are at Warning.
- **One request line per HTTP request** (`UseDataHubRequestLogging`), with `TenantId` (API: the `X-Tenant-Id` header; portal: the session) and `CorrelationId` on every line of the request.
- **Personal data review:** log calls use ids only. Fixed: the SMS outbox file name contained the phone number. **The link token in `/i/{token}` is redacted to `/i/***`** in request logs and trace `url.path` (`SensitiveData.RedactPath`); ASP.NET Core's own request logs are off (Warning).

**Containers (step 6)**
- `deploy/docker/Dockerfile`, **one file with a target per app** (`web`, `api`, `worker`, `migrator`) and one shared build stage that publishes all four. This replaces the planned four separate files, so they can't drift apart.
- Runtime image: `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra`. It is chiseled (no shell or package manager); "extra" adds ICU and tzdata, needed by SqlClient and culture-aware formatting. It runs as user 1654 on port 8080 with `DOTNET_GCHeapHardLimitPercent=4B` (75%).
- Read-only root filesystem works: temp files go to `/tmp` (an emptyDir in Kubernetes), and the portal's key ring is in the database.
- The root `.dockerignore` lets only the app projects under `src/` into the build context. It also excludes `appsettings.Local.json`, launchSettings, bin/obj and every data-file type.
- `scripts/build-images.ps1 -Version x.y.z` builds `datahub-<app>:x.y.z`; the version also goes into the assemblies (and so `service.version`).

**Configuration and secrets**
- `appsettings.json` holds **non-secret defaults only**. Secrets come from environment variables or Kubernetes Secrets, e.g. `ConnectionStrings__DataHub`, `Security__SigningKey`, `S3__SecretKey`, `RabbitMq__Uri`.
- `appsettings.Local.json` holds **published, local-only** values: LocalDB, S3 `localhost:9000` (minioadmin/minioadmin, served by SeaweedFS), RabbitMQ `localhost:5672` (guest), SMTP `localhost:1025`, outbox `../../.local/sms-outbox`, worker temp `../../.local/worker-tmp`, portal links `https://localhost:7049`.
- Every app's launchSettings sets the `Local` environment (`ASPNETCORE_ENVIRONMENT` or `DOTNET_ENVIRONMENT`).
- `.local/` is gitignored (local tools, data, logs, SMS outbox). The legacy project's secrets were moved to user-secrets.

**Tests**
- xUnit.
- A LocalDB fixture creates a unique database per run, applies migrations and drops it afterwards. `DATAHUB_TEST_SQL` can point to another server.
- `WebApplicationFactory` for the API and portal tests.
- Fakes for SMS, email, the publisher and S3 (with multipart semantics and simulated download failure).
- Sample-file tests are skipped when the sample is missing. `DATAHUB_ORIS_SAMPLES` defaults to `Desktop\logyx`.
- The API and portal test factories set `Otlp:Endpoint` to empty, so tests never send telemetry to a local Seq.

---

## 4. Architecture

### 4.1 Solution layout
```
src/
  DataHub.Domain          entities: Company, Invitation, OtpChallenge, Upload, ProcessingJob,
                          Dataset, Account, JournalEntry; enums                            [done]
  DataHub.Application     use cases: InvitationService, VerificationService, UploadService,
                          JobProcessor, OrisDatabaseProcessor, JobRecovery, ProcessingNotifier,
                          Reports/ (ReportService, AccountFilter, StandardChart,
                          BalanceSheetMapping, BalanceSheetBuilder); interfaces IDataHubDb,
                          IFileStore, IJobPublisher, ISmsSender, IEmailSender, IDatasetStore,
                          IUploadProcessor                                                  [done]
  DataHub.Infrastructure  EF Core DbContext + migrations, SqlDatasetStore, S3FileStore,
                          RabbitMQ connection/topology/publisher, SMTP, Outbox SMS            [done]
  DataHub.Oris            TPS reader + ORIS mapping (Georgian, Clarion dates, accounts),
                          GelConverter                                                       [done]
  DataHub.Web             customer portal (MVC)                                              [done]
  DataHub.Api             Invitations API, /reports/*, /dev/token (Local), Swagger,
                          OpenApiExamples                                                    [done]
  DataHub.Worker          RabbitMQ consumer + maintenance (recovery sweep, purge),
                          health endpoints (WorkerHealth)                                    [done]
  DataHub.Hosting         shared Serilog/OTLP + OpenTelemetry registration, request logging,
                          SensitiveData (link-token redaction)                               [step 6]
  DataHub.Migrator        console app applying EF migrations (Helm pre-install Job)          [step 6]
  LogyxDataHub            legacy API (kept for reference; its logic is ported in step 5)
tests/
  DataHub.Oris.Tests, DataHub.Infrastructure.Tests, DataHub.Api.Tests, DataHub.Web.Tests,
  DataHub.Worker.Tests                                                                     [done]
  (integration with real MSSQL/RabbitMQ/S3 containers – step 7)
scripts/
  local/start-infra.ps1, local/stop-infra.ps1   local Mailpit, SeaweedFS, RabbitMQ        [done]
  build-images.ps1                                                                        [step 6]
  package-release.ps1, upload-sftp.ps1, load-test.ps1                                     [steps 8–9]
deploy/
  docker/Dockerfile   one multi-stage file, targets web/api/worker/migrator (chiseled,
                      non-root); .dockerignore at the repo root                           [step 6]
  helm/datahub/  web, api, worker Deployments, migrator Job, Services, Ingress,
                 ConfigMap/Secret refs, optional HPA for the worker, requests/limits      [step 7]
  helm/values-local.yaml, values-tbc.yaml
  local/    mssql, rabbitmq, seaweedfs (S3), mailpit, seq (OTLP receiver)                 [step 7]
docs/
  PROJECT.md (this file)                                                                  [done]
  resource-estimate.md, deployment-guide.md, configuration.md, API (from Swagger)         [steps 8–9]
CLAUDE.md   points Claude Code sessions to this file
```

### 4.2 End-to-end flow
1. **TBC LOS** calls `POST /invitations` with a bearer token. It sends the company code, name, email, phone, `channels` (sms/email) and optionally an `Idempotency-Key`.
   - DataHub creates the Company (if new) and the Invitation.
   - It sends the link on the chosen channels.
   - It returns `{invitationId, tenantId, link, expiresAt, status, deliveredChannels}`.
2. **The customer** opens `/i/{token}` and enters the company ID (plus email if the invitation has one).
   - An OTP is sent by email, or by SMS if the invitation has no email.
   - The customer enters it at `/verify/{challengeId}`.
   - A session cookie is issued.
3. On `/upload`, the customer chooses the upload type (ORIS database ZIP, or CSV, disabled for now) and selects the file.
   - The browser calls `POST /portal-api/uploads`, then sends the chunks with `PUT .../parts/{n}`, then calls `POST .../complete`.
   - The Web app inspects the ZIP in S3 and writes the Upload and Job rows (job **Queued**, invitation **Uploaded**).
   - It publishes a location-only message to RabbitMQ.
4. **The worker** consumes the message and claims the job (**Processing**).
   - It downloads the file, verifies it, converts amounts to GEL and imports into a staging dataset.
   - It activates the dataset: job **Succeeded**, invitation **Processed**.
   - On failure: job **Failed**, invitation **Failed**, and the customer is notified.
5. **The status page** `/status` polls `/portal-api/status`. LOS polls `GET /invitations/{id}`.
6. **TBC LOS/Risk** call `/reports/*` with a bearer token (scope `datahub.reports`) and `X-Tenant-Id`. The response is served from the company's **active** dataset only.

### 4.3 Data model (MSSQL, schema `datahub`)
- **Companies:**
  - Id, **TenantId** (unique; this is `X-Tenant-Id`), CompanyCode (unique, max 20), Name (max 300)
  - ActiveDatasetId (FK), CreatedAt, RowVersion
- **Invitations:**
  - Id, CompanyId, TokenHash (32 bytes, unique), Email (max 320), Phone (max 20)
  - Channels (flags Sms=1, Email=2)
  - Status: Sent / Verified / Uploaded / Processed / Failed / Expired
  - IdempotencyKey + CreatedByClient (filtered unique index)
  - CreatedAt, ExpiresAt, VerifiedAt
- **OtpChallenges:** Id, InvitationId, CodeHash, Channel, Attempts, CreatedAt, ExpiresAt, ConsumedAt.
- **Uploads:**
  - Id, CompanyId, InvitationId, Type (OrisDatabase=0 / OrisEntriesCsv=1)
  - Status: InProgress / Completed / Rejected / Aborted
  - FileName, S3Bucket, S3Key, S3UploadId, SizeBytes, Sha256 (fixed 64)
  - CreatedAt, CompletedAt
- **ProcessingJobs:**
  - Id, UploadId, CompanyId
  - Status: Queued / Processing / Succeeded / Failed
  - Attempts, ErrorCode (max 50), ErrorDetail, DatasetId
  - QueuedAt (last time queued), StartedAt, **HeartbeatAt**, FinishedAt
- **Datasets:** Id, CompanyId, ProcessingJobId, Status (Staging / Active / Superseded / Failed), AccountCount, JournalEntryCount, CreatedAt, ActivatedAt.
- **Accounts:**
  - key (DatasetId, Id)
  - Code (char 4), Sub (max 25), Raw, Level, Name (max 200), Currency (3)
  - index (DatasetId, Code, Sub)
- **JournalEntries:**
  - key (DatasetId, Id)
  - RecordNumber, DocumentNumber, EntryNumber (= RANGE)
  - Debet, DebetSub, DebetRaw, Credit, CreditSub, CreditRaw
  - Amount decimal(19,4), Currency, **AmountGel** decimal(19,4), **ExchangeRate** decimal(19,8) (null for GEL)
  - Description (STORY), Quantity, Unit (VELU), PostedBy (USER_NAME)
  - OperationDate (= DATE), PostingDate (= REAL_DATE)
  - indexes: (DatasetId, OperationDate), (DatasetId, Debet, OperationDate), (DatasetId, Credit, OperationDate)
- **DataProtectionKeys:** Id, FriendlyName, Xml (the portal's cookie key ring, shared by replicas).
- **Migrations:** `InitialCreate`, `AddJobHeartbeat`, `AddJournalGelAmount`, `AddDataProtectionKeys`.

### 4.4 ORIS mapping
- **WIRING.TPS:** DOC → DocumentNumber, RANGE → EntryNumber, DEBET/KREDIT → accounts, MONEY → Amount, MON_TYPE → Currency, CURS → ExchangeRate, STORY → Description, QTY → Quantity, VELU → Unit, USER_NAME → PostedBy, DATE → OperationDate, REAL_DATE → PostingDate.
- **Acc_name.tps:** COUNT → account, LEVEL, NAME, MON_TYPE.
- **Rate.tps:** daily exchange rates per currency (used by `GelConverter`).
- **Account parsing:** concatenate the space-separated groups until there are 4 digits; that is the account. The remaining groups, joined by spaces, form the Sub. Examples:
  - `1 6 20 00255` → `1620` + `00255`
  - `3 1 20 1 193` → `3120` + `1 193`
  - `1 2 10` → `1210` + `""`
- **Verified:** reference `WIRING.TPS` = **16,781 rows, total 10,525,411.79**, and it imports into SQL with the same total. The HIRO database imports end-to-end through the worker.

---

## 5. Step-by-step plan and status

| Step | Content | Status | Commit(s) |
|---|---|---|---|
| 0 | Repo hygiene | **Done** | `60735fa` |
| 1 | Parser spike (DataHub.Oris) | **Done** | `6bccbf4` |
| 2 | Domain + database | **Done** | `4b5bbf4` |
| 3 | Invitations API + customer portal | **Done** | `bc5907a`, `7bdfa1a`, `b1c1753` |
| 4 | Worker | **Done** | `41af8c0` |
| 5 | TBC report APIs | **Done** | `e309f5e` |
| 5a | Local run without Docker + Swagger for local testing | **Done**, verified live with HIRO | `4567109`, `1e6c3b1` |
| 6 | Cross-cutting: OTLP logging, telemetry, Migrator, Dockerfiles | **In progress**: code and tests done; live checks wait for Seq and Rancher Desktop | on `feature/step6-observability` |
| 7 | Local k3s test environment + Helm chart | Planned | – |
| 8 | Load test + resource estimate | Planned | – |
| 9 | Release packaging + SFTP upload | Planned | – |

**Resume point (2026-10-01):** step 6 is in progress on `feature/step6-observability` (from `main` after PR #1 was merged). All step 6 code is written and tested: **128 tests passing** (Oris 34, Infrastructure 54, Api 22, Web 16, Worker 2) and 0 build warnings. Committed and pushed (no PR yet); only the live checks below are left.

### ▶ Step 6: what is left
**Done (2026-10-01)**
- Serilog + OTLP/HTTP logs, OpenTelemetry traces and metrics through `DataHub.Hosting` in Web, Api, Worker and Migrator (details in 3.2 "Logging and telemetry").
- Personal-data review of the log calls; the link token is redacted; the phone number was removed from the SMS outbox file name.
- `DataHub.Migrator`, checked live: a fresh database got all migrations (exit 0), a second run was a no-op, an unreachable server gave exit 1. Applied `AddDataProtectionKeys` to the local `DataHub` database.
- Health: `/health/live` and `/health/ready` on Web, Api and Worker.
- The portal's Data Protection keys are in the database (replicas and restarts keep sessions).
- `deploy/docker/Dockerfile`, `.dockerignore` and `scripts/build-images.ps1`.
- Config review: everything comes from configuration, so it can be overridden by environment variables. `amqps://` URIs switch on TLS in RabbitMQ.Client (its `Uri` setter). S3 ServiceURL/region/path-style were already settings.

**Still to do (needs the user's installs; see 8.3)**
1. With Seq running: start the infrastructure and the three apps, do a full HIRO run, and check in Seq (http://localhost:5341) that logs and traces from web, api and worker arrive. Check that the worker's `process job` span joins the upload's trace, that `/i/***` appears instead of the token, and that `TenantId`/`CorrelationId` are set. Try `Otlp:ExportMetrics=true` to see whether Seq accepts metrics, and record the result.
2. With Rancher Desktop running: `scripts\build-images.ps1`. Then run the images with `--read-only --tmpfs /tmp`: the migrator against a throwaway `mssql/server:2022-latest` container (LocalDB isn't reachable from containers), and web/api/worker far enough to answer `/health/live`. Record the image sizes and confirm they run as user 1654.
3. Commit the results and open a PR into `main`.

### Step 0: Repo hygiene (done)
- Added a .NET `.gitignore`. Stopped tracking `bin/`, `obj/` and `.vs/` (that is why the diff against `main` shows ~127 deleted build files).
- Moved the secrets out of `appsettings.json` into user-secrets (`UserSecretsId` added).
- Working branch: `feature/tbc-architecture`.
- **User action still open:** rotate the leaked credentials (see 8.3). Optionally purge them from history with `git filter-repo`.
- **User action still open:** add `Jwt__Key` and `ConnectionStrings__LogyxConnection` to the Azure App Service configuration for the legacy app.

### Step 1: Parser (done)
- `GeorgianText`, `ClarionDate`, `OrisAccount` (`TryParse`/`Parse`).
- `TpsTableReader`: a `TpsRecord` with GetString/GetDecimal/GetLong/GetDate, plus the group workaround.
- `OrisReader`: `ReadJournalLines`, `ReadAccountNames`, `ReadRates`.
- Tests cover the parsing rules and the sample files.

### Step 2: Domain and database (done)
- Entities and enums, `DataHubDbContext`, `SqlDatasetStore` (the `IDatasetStore` operations below), `PersistenceRegistration`, the design-time factory, and a dotnet-ef local tool (10.0.12).
- `IDatasetStore` operations: CreateStaging, WriteAccounts, WriteJournal, Activate, MarkFailed, AbandonStaging, PurgeInactive.
- Tests: the dataset lifecycle (staging is not served; replace plus purge; a failure keeps the old data; an older upload doesn't overwrite newer data; Georgian text, accounts and dates are stored faithfully) and the reference WIRING import.

### Step 3: Invitations API and customer portal (done)
- **3a (`bc5907a`)** Application services, as described in section 3.2: `Secrets`, `InvitationService`, `VerificationService`, `UploadService`, `ZipInspector`, and the bilingual `Messages`.
- **3b (`7bdfa1a`)**
  - Infrastructure adapters: `S3FileStore` + `S3RangeReadStream`, RabbitMQ topology/connection/publisher, `SmtpEmailSender`, `OutboxSmsSender`, `InfrastructureRegistration`.
  - DataHub.Api endpoints:
    - `POST /invitations` (Idempotency-Key header)
    - `GET /invitations/{id}` (status plus the latest job result)
    - `POST /dev/token` (Local only)
    - Swagger and `/health/live`
- **3c (`b1c1753`)** DataHub.Web:
  - `GET/POST /i/{token}` (link page), `GET/POST /verify/{challengeId}` (code page)
  - `/`, `/upload`, `/status`, `POST /logout`
  - `portal-api/uploads` (start / parts / put part / complete / abort), `GET /portal-api/status`
  - `/error/{status}`, `/signed-out`
  - JS: `upload.js` (3 parallel chunks, retry, localStorage resume) and `status.js` (polling)
- **Open for this step:** the real SMS gateway adapter, once TBC's API is known.

### Step 4: Worker (done)
- `ProcessingOptions`:
  - TempDirectory, MaxAttempts 3
  - HeartbeatInterval 30 s, StaleAfter 3 min, RequeueAfter 5 min
  - SweepInterval 1 min, PurgeInterval 15 min
- `IUploadProcessor`, one per `UploadType`. `OrisDatabaseProcessor` is implemented. **CSV later = one more processor class.**
- `JobProcessor`: claim → download and verify → staging import → activate → statuses; failure classification; shutdown hand-back; heartbeat.
- `JobRecovery` (the sweep) and `ProcessingNotifier` (failure email/SMS).
- The `DataHub.Worker` host: `JobConsumer` (RabbitMQ) and `MaintenanceService` (sweep + purge).
- Error codes:
  - `ORIS_NO_JOURNAL`, `ORIS_MULTIPLE_DATABASES`, `ORIS_UNREADABLE`, `ORIS_EMPTY_JOURNAL`, `ORIS_RATES_MISSING`
  - `UPLOAD_CORRUPTED`, `UPLOAD_NOT_ZIP`
  - `PROCESSING_TYPE_NOT_SUPPORTED`, `PROCESSING_FAILED`
  - all have bilingual texts, which the portal status page also shows
- **Disk:** needs about the upload size (up to 2 GB) plus the largest extracted table. In Kubernetes, use an emptyDir with an `ephemeral-storage` request/limit, or a PVC.
- **To verify:** whether TpsParser loads a whole `.tps` into memory. That decides the worker memory limit for 2 GB uploads, so measure it in step 8. If it does, consider a streaming reader.
- **Worker health check:** not yet written; comes with step 6/7 (an exec probe or small HTTP health endpoint).

### Step 5: TBC report APIs (done, `e309f5e`)
- GEL conversion, the `AddJournalGelAmount` migration, the three report endpoints, `/health/ready`, and tests (details in 3.2 "GEL conversion" and "Reports").
- Ported the legacy `HiroWiringController` logic and fixed its bugs: consistent inclusive `toDate`, decimal sums, no manual Bearer checks.
- Final fix before commit: `DatasetLifecycleTests.Imports_reference_wiring_file_end_to_end` failed because the reference `WIRING.TPS` has foreign-currency lines (USD record 13985) but no `Rate.tps` and no line rates. The test now applies placeholder rates of 1.0 per foreign currency, because it only checks the original `Amount` (16,781 rows, total 10,525,411.79). GEL accuracy is covered by `HiroReportTests`, which uses HIRO's real `Rate.tps`.
- **Live result with HIRO (2026-10-01):** 1,533 journal entries for 2024; turnover and balance sheet return figures. Two negative asset lines in the 2024 balance sheet (otherCurrentAssets -0.57, interestReceivable -16.00) go to the accountant's review (8.2).
- Turnover and journal figures are checked against ORIS in `HiroReportTests`. **Still to do:** compare the balance sheet with the reference XLSX files in `Desktop\logyx\balance\` and `TBC APIs Data Hub\` (so far it is only checked to balance and to match the turnover closing).

### Step 5a: Local run and Swagger for testing (done, `4567109`, `1e6c3b1`)
- `scripts/local/start-infra.ps1` and `stop-infra.ps1` (see section 6).
- **SeaweedFS replaces MinIO**: MinIO's open-source server is archived and its downloads return "410 Gone".
- Swagger: Local-only `dev` document with `/dev/token` and pre-filled defaults; example body for `POST /invitations`.
- `JobRecovery` orders stale jobs by heartbeat (removed an EF Core warning).
- Local invitation links point at `https://localhost:7049`.
- **Verified live end to end with HIRO:** invitation via API → link email in Mailpit → OTP email → 3-part upload (5 MB chunks) to SeaweedFS with AES256 → RabbitMQ → worker (about 6 s) → status "processed" in the portal and the API → all three reports → unknown tenant 404.

### Step 6: Cross-cutting TBC requirements (in progress)
What was built is in 3.2 ("Logging and telemetry", "Containers", the worker probes, the portal's Data Protection keys, the Migrator under "Database"). What is left is under "▶ Step 6: what is left" above.
- Changes from the original plan:
  - one Dockerfile with four targets instead of four files
  - an extra setting `Otlp:ExportMetrics`
  - the worker became a web host for its probes (chiseled images have no shell for an exec probe)
  - Data Protection keys in the database, which wasn't in the plan but is needed for more than one web replica and a read-only filesystem
  - `DataHub.Hosting` as a new shared project

### Step 7: Local test environment on the user's PC (planned)
- **Install:** Rancher Desktop (free; bundles k3s Kubernetes, the docker CLI, kubectl and helm, on WSL2). Docker is not installed today.
- Cap WSL at about 10 GB RAM and 4 CPUs (`.wslconfig`).
- **Namespace `datahub-local`:**
  - `mssql` (2022-latest, `MSSQL_MEMORY_LIMIT_MB=2048`)
  - `rabbitmq` (with the management UI)
  - **`seaweedfs`** (S3; replaces MinIO, which is archived), with the bucket `datahub-uploads`, an SSE-S3 key, and lifecycle rules
  - `mailpit` (SMTP plus a web inbox)
  - `seq` (receives OTLP/HTTP logs natively)
  - the DataHub Helm chart with `values-local.yaml`
- Access through Traefik ingress at `datahub.localtest.me` (portal) and `api.datahub.localtest.me` (API). `values-local.yaml` sets `Invitations__PortalBaseUrl` to the ingress address (`appsettings.Local.json` uses `https://localhost:7049` for `dotnet run`).
- The ingress needs a large body size and long timeouts for uploads.
- `docker compose` is kept only for quick inner-loop development (infrastructure only; apps run from Visual Studio or `dotnet run`).
- The upload JavaScript gets its first real browser test (the live run so far drove the portal API from a script).
- Integration tests against real MSSQL, RabbitMQ and S3 containers.

### Step 8: Resource estimate and requests/limits (TBC requirement #7, planned)
- metrics-server, which is built into k3s.
- `scripts/load-test.ps1`:
  - uploads each sample company (AILABI, HIRO, ALTERA, the 16.5 MB ZIP) once, then 5 at the same time
  - also runs synthetic large ZIPs (~500 MB and ~2 GB)
- Measure:
  - upload throughput
  - worker CPU/RAM and temp-disk use
  - processing duration
  - the load on MSSQL
  - whether TpsParser holds whole files in memory
- Capture per-pod CPU and memory peaks with `kubectl top` sampling plus the Seq/OTel metrics.
- Write `docs/resource-estimate.md`:
  - a table per component (idle vs peak)
  - recommended requests/limits
  - sizing guidance per MB of input
  - notes on worker replicas and prefetch
- Starting guesses, to be replaced by the measurements:

  | Component | CPU | Memory |
  |---|---|---|
  | web/api | 100m–500m | 256–512Mi |
  | worker | 250m–1000m | 512Mi–1Gi, plus ephemeral storage of about 2.5–3 GB |

### Step 9: Release packaging for TBC's SFTP (planned)
- `scripts/package-release.ps1 -Version x.y.z` produces:
  - `datahub-images-x.y.z.zip`: `docker save` of the web, api, worker and migrator images
  - `datahub-x.y.z.tgz`: `helm package`, with a default `values.yaml` pointing at external MSSQL/S3/RabbitMQ/OTLP
  - `SHA256SUMS`, `RELEASE-NOTES.md`, `deployment-guide.md`, `resource-estimate.md`
- `scripts/upload-sftp.ps1` uses the built-in OpenSSH `sftp` with key authentication. TBC's host and credentials are supplied at run time.
- Replace the Azure GitHub workflow with CI that builds, tests and produces the release artifacts.
- The chart uses only stable Kubernetes APIs (`apps/v1`, `batch/v1`, `networking.k8s.io/v1`, `autoscaling/v2`), so it runs on k8s 1.25 and later. Ingress class and image registry are values.

---

## 6. Running locally (no Docker)

### 6.1 One-time setup
- **.NET 10 SDK** (installed). **LocalDB** `MSSQLLocalDB` (installed).
- **Trust the HTTPS dev certificate** (Windows asks to confirm): `dotnet dev-certs https --trust`. Still open as of 2026-10-01; until then the browser warns.
- **Create or update the database** with the Migrator (run it again after every pull that adds a migration):
  ```powershell
  dotnet run --project src\DataHub.Migrator     # Local environment → LocalDB database DataHub; exit code 0 = up to date
  ```
  `dotnet ef` (local tool, `dotnet tool restore`) is still used to *create* migrations.
- **Seq** (OTLP receiver for logs and traces): `winget install Datalust.Seq` (elevated). It runs as a Windows service with its UI at http://localhost:5341. The apps' `appsettings.Local.json` send to `http://localhost:5341/ingest/otlp`; if Seq isn't running, they still log to the console.
- **Rancher Desktop** (Docker and k3s, on WSL2) for building images: `wsl --install --no-distribution`, then `winget install SUSE.RancherDesktop` (both elevated; reboot if asked), choosing the dockerd (moby) engine.
- **Local tools** in `.local\tools` (gitignored; downloaded 2026-10-01, checksums verified):
  - `mailpit.exe` (v1.31.3)
  - `weed.exe` (SeaweedFS 4.48)
  - `erlang\` (Erlang/OTP 28.5.0.7, portable zip; RabbitMQ 4.3.x needs Erlang ≥ 27; 29 was avoided as possibly too new)
  - `rabbitmq_server-4.3.6\` (portable Windows zip; RabbitMQ is not in winget)
  - On a new machine, download these again into `.local\tools` (MinIO is no longer available).

### 6.2 Start and stop the services
```powershell
powershell -ExecutionPolicy Bypass -File scripts\local\start-infra.ps1   # idempotent
powershell -ExecutionPolicy Bypass -File scripts\local\stop-infra.ps1    # data is kept
```
| Service | Address | Credentials |
|---|---|---|
| S3 (SeaweedFS) | http://localhost:9000, bucket `datahub-uploads` | minioadmin / minioadmin |
| SeaweedFS filer | http://localhost:8888 (buckets are folders under `/buckets`) | – |
| RabbitMQ | localhost:5672; UI http://localhost:15672 | guest / guest |
| Mailpit | SMTP localhost:1025; inbox http://localhost:8025 | – |

- Data and logs: `.local\infra` (SeaweedFS data, RabbitMQ base, Mailpit database, logs). SeaweedFS's own error logs go to `%TEMP%\weed.exe.*.log.ERROR.*`.
- SeaweedFS needs an SSE key for our AES256 uploads: the script generates it once into `.local\infra\sse.key` and passes it as `WEED_S3_SSE_KEY`. Deleting that file makes stored objects unreadable.
- The bucket is created through the filer HTTP API (`POST /buckets/datahub-uploads/`; 409 = already exists), because `weed shell` cannot run single commands non-interactively.

### 6.3 Run the apps (HTTPS profile is required)
```powershell
dotnet run --project src\DataHub.Api --launch-profile https      # https://localhost:7174 (Swagger at /swagger)
dotnet run --project src\DataHub.Web --launch-profile https      # https://localhost:7049
dotnet run --project src\DataHub.Worker                           # health at http://localhost:5290/health/live and /health/ready
```
Or start them from Visual Studio with the `https` profile.

### 6.4 Build the container images (needs Rancher Desktop)
```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-images.ps1 -Version 0.1.0   # datahub-web/api/worker/migrator:0.1.0
```

### 6.5 Test the flow by hand
1. Swagger https://localhost:7174/swagger → definition **"dev (local token)"** → `POST /dev/token` (pre-filled) → copy `access_token` → **Authorize**.
2. `POST /invitations` (pre-filled example; change `companyCode` to get a separate company). Optional `Idempotency-Key`.
3. Open the `link` from the response. Codes and links arrive in Mailpit; SMS go to `.local\sms-outbox\*.txt`.
4. Upload a ZIP of an ORIS company folder (e.g. zip the HIRO folder; never into the repo). Watch `/status`.
5. Reports: `GET /reports/*` with the invitation's `tenantId` in `X-Tenant-Id`, e.g. `fromDate=2024-01-01&toDate=2024-12-31`.

Pitfalls: a token from `/dev/token` with other values has no scopes → 403; the `http` profile breaks the portal (`__Host-` cookies); PowerShell 5.1 strips quotes from inline JSON passed to `curl.exe` (use `--data-binary @file`).

---

## 7. Verification

### 7.1 Plan
1. **Unit tests:**
   - Georgian decoding, Clarion dates and account parsing.
   - Parser output matches the `WIRING-UNNAMED.CSV` row count and totals.
   - Debit/credit sums per account match the `SQLQuery1.sql`-style checks and the reference XLSX files.
2. **Integration tests** (real MSSQL/RabbitMQ/S3):
   - a full job run
   - a failing ZIP leaves the old dataset unchanged
   - tenant isolation in the APIs
3. **End-to-end on local k3s:**
   - LOS creates an invitation.
   - The link appears in Mailpit and the SMS outbox.
   - Open the link, get the OTP from Mailpit, upload the HIRO ZIP.
   - The status shows completed.
   - `GET /reports/*` with a token and `X-Tenant-Id` returns figures that match `HIRO.xlsx` and the 2024 turnover/journal XLSX files.
   - Logs appear in Seq through OTLP/HTTP.
   - `GET /invitations/{id}` reports each status change.
   - (Already done once without k3s on 2026-10-01; see step 5a.)
4. **Clean-install test**, exactly as TBC would do it:
   - `docker load` the images from the zip, then `helm install` from the `.tgz` into a fresh namespace.
   - `helm upgrade` with a migration.
5. The load test produces `docs/resource-estimate.md`.

### 7.2 Current test inventory (128 tests, all passing, 0 build warnings)
- **DataHub.Oris.Tests (34):**
  - `ParsingRulesTests`: Georgian decoding, Clarion dates, account parsing rules
  - `SampleFileTests`: the reference `WIRING.TPS` (16,781 rows, total 10,525,411.79), the HIRO `Acc_name.tps`, detection of the encrypted `ACCOUNT.TPS`
  - `GelConverterTests`: GEL/blank currency keeps its amount; the line rate wins over the table; the table rate is the latest on or before the date; rounding half away from zero like ORIS; a missing rate is reported with the record
- **DataHub.Infrastructure.Tests (54):**
  - `DatabaseMigratorTests`: a fresh database gets every migration; a second run applies nothing.
  - `DatasetLifecycleTests` (6), including the reference WIRING import into SQL (with placeholder rates).
  - `CustomerFlowTests` (13):
    - an invitation on both channels; idempotent replay; contact validation
    - wrong company code; email OTP; SMS-only OTP; OTP expiry and rate limit
    - a chunked upload queues a location-only message; resume reports the stored parts
    - rejection of a ZIP with no TPS; path traversal; CSV disabled; blocked while processing
    - ASCII-safe S3 keys
  - `JobProcessingTests`:
    - the HIRO database is imported and activated
    - missing journal (customer notified by email and SMS)
    - a corrupt TPS is reported unreadable and its staging is discarded
    - two company databases; checksum mismatch; not a ZIP
    - a duplicate message is skipped; a job being processed elsewhere is skipped
    - transient outage: retried by the sweep, then fails after max attempts
    - an abandoned job is requeued and claimed; an abandoned job on its last attempt fails
  - `HiroReportTests` (5), HIRO with its real `Rate.tps`: the 2024 turnover register matches ORIS; a turnover filtered by account totals that account; the Q1 journals for accounts 1210 and 1410 match ORIS extracts; the balance sheet balances and matches the turnover closing.
  - `AccountFilterTests`: ORIS and compact account forms; an account matches everything nested under it; invalid account numbers are rejected.
- **DataHub.Api.Tests (22):**
  - `InvitationsApiTests` (6): auth required, scope enforced, create/get invitation, idempotency over HTTP, client isolation, validation ProblemDetails.
  - `ReportsApiTests`: the reports scope is required; the tenant header is required and must be known; a company without processed data gets a clear 404; a `tenantId` query must match the header; each tenant sees only its own data; journal contract fields, inclusive dates and paging headers; journal filters by ORIS-form account and currency; invalid parameters are rejected with a code; turnover and balance-sheet contract shapes; readiness checks the database.
- **DataHub.Web.Tests (16):**
  - `PortalFlowTests` (6):
    - the customer verifies, uploads in chunks, and the job is queued
    - an invalid ZIP returns a bilingual error code
    - the upload API requires a verified session (401)
    - the upload API rejects requests without a CSRF token
    - a wrong company code shows an error and sends no code
    - an unknown link shows the invalid page with security headers
  - `ObservabilityUnitTests` (8): link-token redaction of paths; OTLP header and endpoint parsing.
  - `RequestLoggingTests`: requests to `/i/{token}` (valid and invalid) log `/i/***` and never the token, email or phone.
  - `ReplicaTests`: a cookie payload protected by one portal instance is readable by another, and the key is in the database.
- **DataHub.Worker.Tests (2):** `WorkerHealthTests`: liveness fails only when the maintenance loop stops; readiness needs a consumer channel.

Run everything: `dotnet test LogyxDataHub.sln` (needs LocalDB; sample tests skip without `Desktop\logyx`).

---

## 8. Open items

### 8.1 Questions for TBC (none block the design)
- **mTLS:** unknown. It is supported either way: a TLS client-certificate config flag, or handled at TBC's ingress/API gateway with no code change.
- **Kubernetes version:** unknown. The chart targets 1.25+ with stable APIs.
- **SMS gateway API:** needed for the real `ISmsSender` adapter; the outbox is used until then.
- **OIDC authority/audience, and the scope names** for LOS/Risk tokens.
- **SQL Server version:** this sets `Database:CompatibilityLevel` (default 150 = 2019).
- **Retention period** for uploaded ZIPs in S3, and for replaced data.
- **Ingress limits:** whether TBC's ingress or WAF allows 64 MB request bodies (the 16 MB chunks) and long uploads.
- **S3:** confirm SSE-S3 (AES256) is allowed/enabled on their bucket.
- **Paused:** the naming of `turnover-register` vs `turnover` and the docx example field names. This is naming only, not parsing; the data comes from the same WIRING lines either way.

### 8.2 Questions for the user
- Is **ACCOUNT.TPS** (encrypted in the HIRO sample) needed for any report? If so, raise it with the ORIS vendor.
- Confirm or change the step 4 choices:
  - one company per ZIP
  - `Acc_name.tps` optional
  - notify on failure only
  - keep ZIPs in S3
- **Accountant review of the balance-sheet mapping** (`Reports:BalanceSheet:Lines`), including the negative asset lines for HIRO 2024 (otherCurrentAssets -0.57, interestReceivable -16.00).
- **Portal key ring at rest:** the Data Protection keys sit in `datahub.DataProtectionKeys` protected only by database access control. Options if TBC wants more: encrypt them with a certificate from a Kubernetes Secret (`ProtectKeysWithCertificate`), or keep as is.

### 8.3 Actions for the user
- **Rotate the leaked credentials:** the Azure SQL admin password, the JWT key and the DevExpress key. The old values are still in `main`'s history on GitHub.
- Add `Jwt__Key` and `ConnectionStrings__LogyxConnection` to the Azure App Service configuration.
- Confirm deleting the old untracked copy `source\repos\LogyxDataHub`.
- Trust the HTTPS dev certificate (`dotnet dev-certs https --trust`).
- Optional: install the GitHub CLI (`winget install GitHub.cli`, then `gh auth login`), so Claude can open PRs directly. (PR #1 for `feature/tbc-architecture` was merged.)
- **For step 6, in an elevated PowerShell:** `wsl --install --no-distribution`, `winget install SUSE.RancherDesktop`, `winget install Datalust.Seq` (see 6.1).

---

## 9. Security and handling rules
- **ORIS sample files are real customer financial data and must never be committed.** Before every commit, check the staged files for `.tps`, `.csv`, `.zip`, `.xlsx`, `.bak`, `.key` and `.db`, and for `bin/`, `obj/` and `.local/`.
- Before every push, check that no commit in the range adds secrets or data files (`git diff --name-only --diff-filter=AMR origin/main HEAD`).
- **`appsettings.json` never contains secrets.** Only `appsettings.Local.json` holds local-only, deliberately published, non-secret values.
- **This file is committed:** no secret values in it, only where secrets live.
- No personal data, OTPs, link tokens or internal error details in logs or customer messages.
- Link tokens and OTP codes are stored only as hashes.
- Uploaded objects are encrypted at rest (SSE AES256).
- `/dev/token` and the Swagger `dev` document exist only in the `Local` environment.
- Avoid editing files that contain Georgian text with PowerShell 5.1 `Get-Content`/`Set-Content` (they use ANSI and corrupt it). Use the editor tools, or `[IO.File]` with UTF-8.

---

## 10. Problems found and fixed
| Problem | Fix |
|---|---|
| TpsParser NRE on GROUP fields | Request only non-group fields; flatten `ClaGroup` with `GetValues()` |
| PowerShell 5.1 corrupted the Georgian string | Rewrote the file with a UTF-8 tool |
| Wrong test expectation for Clarion 80692 | It is 2021-12-01 (not 12-02) |
| Legacy project at the repo root compiled the new sources | Moved it to `src/LogyxDataHub` |
| EF1002 SQL-injection warning | `ExecuteSqlAsync` with literal table names |
| Stale tracked Dataset counts | Detach the entity after creation |
| Internal members not visible to tests | `InternalsVisibleTo` |
| S3 key sanitiser produced many underscores | The regex now collapses runs of unsafe characters |
| xUnit collection fixture missing in other test assemblies | A `CollectionDefinition` in each test project |
| Validation attributes with `property:` on records caused a 500 | Put them on the constructor parameters (`[DefaultValue]` with `property:` is fine) |
| Razor HTML-encoded Georgian | `WebEncoderOptions` with `UnicodeRanges.All` |
| The API's 401 turned into an empty 400 (status-page re-execution kept the POST and hit antiforgery) | `[IgnoreAntiforgeryToken]` on ErrorController; no status pages for `/portal-api` |
| Forms had no antiforgery token (an explicit `action` suppresses it) | `asp-antiforgery="true"` |
| `IExceptionHandler` rendered HTML for portal API errors | An MVC exception filter on the upload API controller |
| The ZIP-in-queue idea (the user pointed out the 2 GB limit) | Location-only messages; the file stays in S3 |
| Reference WIRING import test failed on USD lines (no `Rate.tps` for that file) | Placeholder rates in that test; GEL checked in `HiroReportTests` |
| Portal on the `http` profile: 500 from antiforgery (`__Host-` cookies need HTTPS) | Run with the `https` profile |
| Local links pointed at `datahub.localtest.me`, which doesn't exist before step 7 | `PortalBaseUrl` = `https://localhost:7049` in `appsettings.Local.json` |
| MinIO downloads return "410 Gone" (project archived) | SeaweedFS as the local S3 |
| SeaweedFS 500 on uploads with SSE AES256 | `WEED_S3_SSE_KEY` from a generated, kept key file |
| `weed shell` ignores piped commands (hangs) | Create the bucket through the filer HTTP API |
| Stopping a background script also killed the services it started | Run `start-infra.ps1` in the foreground; it returns once everything is up |
| `/dev/token` invisible in Swagger (its `dev` group was in no document) | Local-only `dev` Swagger document |
| The SMS outbox file name (which is logged) contained the phone number | Timestamp + random id; the number stays inside the file |
| The link token would reach logs and traces through the URL path `/i/{token}` | `SensitiveData.RedactPath` in the request log and the trace `url.path`; ASP.NET Core request logs at Warning |
| Per-pod Data Protection keys would end portal sessions on restart or on another replica | Key ring in the database (`AddDataProtectionKeys`) |
| Migrator `await using` on `IHost` didn't compile (IHost is only IDisposable) | `using` (disposal still flushes the logs) |
| Serilog quotes string values when it renders a message for other logger providers | Tests match `"GET" "/i/***"` |
| Swagger's `"string"` placeholders produced a token without scopes (403) | `[DefaultValue]` defaults on `DevTokenRequest`; example body for `POST /invitations` |
| EF Core warning: row limit without `OrderBy` in `JobRecovery` | Order stale jobs by `HeartbeatAt` |
