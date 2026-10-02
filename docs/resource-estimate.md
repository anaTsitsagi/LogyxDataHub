# Logyx DataHub: resource estimate and Kubernetes sizing

_Measured 2026-10-02 (step 8, TBC requirement #7). DataHub 0.1.0, Helm chart 0.1.0._
_Sample companies are anonymised (A, B, C); only file sizes and row counts are given._

## 1. Summary

| | Recommendation |
|---|---|
| Portal (web) | 2 replicas; request 100m CPU / 256 MiB; limit 512 MiB |
| LOS API (api) | 2 replicas; request 100m CPU / 256 MiB; limit 512 MiB |
| Import worker | 1–2 replicas (one job at a time each); request 500m CPU / 768 MiB / 3 GiB ephemeral storage; limit 2 GiB |
| Migrator (Helm hook Job) | request 100m CPU / 128 MiB; limit 512 MiB; runs once per install or upgrade |
| CPU limits | None recommended (imports are short CPU bursts of ~1.4 cores; a limit only slows them). If policy requires one: 1 core for web/api, 2 cores for the worker |
| Total (2 web, 2 api, 2 workers) | Requests ≈ 1.4 CPU cores and 2.5 GiB memory, plus 6 GiB ephemeral storage (workers); memory limits ≈ 6 GiB |

**The one real limit:** worker memory grows with the size of the ORIS journal table (`WIRING.TPS`), at about **28 MiB per MB of WIRING.TPS**, because the TPS parser library holds the whole table in memory. With a 2 GiB limit, one worker handles a `WIRING.TPS` of up to **about 55 MB (~190,000 journal lines)**. The largest sample tested is 4.8 MB (16,781 lines). The upload size (up to 2 GB) affects disk, not memory.

## 2. Test setup

- Single-node Kubernetes (k3s 1.36 in Rancher Desktop) on a Windows 10 development PC: WSL2 VM with 4 vCPUs and 7.9 GB RAM.
- DataHub images 0.1.0 (.NET 10, chiseled), deployed with the Helm chart. Infrastructure in the same cluster: SQL Server 2022 (memory capped at 2 GB), RabbitMQ 4.3 (quorum queues), SeaweedFS as S3 (server-side encryption on), Seq for logs, traces and metrics.
- Each test run does the complete customer flow through the HTTPS ingress: invitation from the API → link and one-time code → chunked upload (5 MB parts) → queue → import by the worker → the three reports. Every run checked all steps (13 checks each); all runs passed.
- CPU and memory per container sampled every ~2 s (`docker stats`), the worker's temp disk with `du`, the worker's own process memory from its .NET metrics, and a separate benchmark of the TPS parser.

| Input | WIRING.TPS | Journal lines | ZIP |
|---|---|---|---|
| Sample A | 4.38 MB | 15,419 | 1.8 MB |
| Sample B | 1.84 MB | 6,550 | 12.2 MB |
| Sample C | 0.23 MB | 748 | 0.2 MB |
| Reference journal file (parser benchmark only) | 4.78 MB | 16,781 | – |
| Synthetic 500 MB | Sample B + 488 MB incompressible padding | 6,550 | 500 MB |
| Synthetic 1.9 GB | Sample B + 1,888 MB incompressible padding | 6,550 | 1,901 MB |

## 3. Results

### 3.1 Scenarios

| Scenario | Result | Import time (worker) |
|---|---|---|
| A, B and C one after another | all passed | C 2.8 s, B 3.6 s, A 8.7 s |
| 5 uploads at the same time (A, B, C, A, B) | all passed | 10.7–19.9 s each including queue wait (one worker, one job at a time); all five done within ~60 s |
| 500 MB ZIP | passed | 21.5 s |
| 1.9 GB ZIP | passed | 57.4 s (includes downloading the ZIP from S3) |

### 3.2 Per component (CPU in millicores: 1000 = one core)

| Component | Idle CPU | Idle memory | Peak CPU | Peak memory | Notes |
|---|---|---|---|---|---|
| Portal (web) | 15–26 | 185–225 MiB | 1,147 | 227 MiB | Peak while receiving 5 uploads |
| API | 3–5 | 143–172 MiB | 681 | 187 MiB | Peak during report queries |
| Worker | 2–14 | 200–330 MiB | 1,375 | ≤ 410 MiB process memory | Container memory incl. file cache reached 1.7 GiB on the 1.9 GB ZIP (reclaimable cache, not process memory) |
| Worker temp disk | – | – | – | 1,901 MiB | = ZIP size + one extracted table (1.9 GB run); 503 MiB for the 500 MB run |
| SQL Server | 28–54 | 1.14–1.18 GiB | 984 | 1.18 GiB | Memory capped at 2 GB in the test |
| RabbitMQ | 4–10 | 117–120 MiB | 37 | 120 MiB | See 5.3 about readiness probes |
| S3 (SeaweedFS, test only) | 7–15 | 150–380 MiB | 960 | ~544 MiB + file cache | Stands in for the bank's S3 |

### 3.3 TPS parser memory (the worker's main memory driver)

| WIRING.TPS | Memory after opening | Peak heap while reading | Peak process memory | Rows | Time |
|---|---|---|---|---|---|
| 0.23 MB | 2.4 MB | 8 MB | 39 MB | 748 | 0.4 s |
| 1.84 MB | 19 MB | 40 MB | 79 MB | 6,550 | 1.4 s |
| 4.38 MB | 45 MB | 92 MB | 157 MB | 15,419 | 2.0 s |
| 4.78 MB | 49 MB | 99 MB | 166 MB | 16,781 | 2.1 s |

Memory grows linearly: about 10× the file size when the table is opened, 20× peak heap and **~28 MiB of process memory per MB**. Rows are written to SQL Server in batches of 10,000, so the database side does not add to it.

### 3.4 Database and storage

- **SQL Server:** about **530 bytes per journal line** including indexes. That is about **1.9 MB per MB of WIRING.TPS**, or ~8 MB for a company with 15,000 journal lines. Only each company's active dataset is kept; replaced datasets are purged every 15 minutes.
- **S3:** uploaded ZIPs are kept after processing until the bank's retention rule is decided (planned as an S3 lifecycle rule). Storage then equals the sum of the uploads in the retention period.

### 3.5 Upload throughput

- Inside the cluster the portal accepted **15 MB/s per upload** sending one 5 MB part at a time (0.33 s per part, mostly the S3 write). The browser sends 3 parts in parallel.
- From the Windows test PC into its local cluster it was only 3.7–5 MB/s, limited by the development PC's virtual network, **not representative** of the bank's network. The 1.9 GB upload took 8 minutes for that reason.

## 4. Sizing formulas

| What | Estimate |
|---|---|
| Worker memory | ≈ 350 MiB + 28 MiB × (WIRING.TPS in MB) |
| Worker temp disk | ≈ ZIP size + WIRING.TPS size (≤ ~2.1 GB at the 2 GB upload limit) |
| Import time | ≈ 2.5 s + 1.4 s per MB of WIRING.TPS (parsing and database writes) + downloading the ZIP from S3 (~38 MB/s in the test: ~50 s for 1.9 GB) |
| Database | ≈ 1.9 MB per MB of WIRING.TPS for each company's active dataset |
| Worker capacity | One job at a time per replica; typical company archives take 3–10 s, so roughly 6–12 imports per minute per worker |

## 5. Recommendations and notes

1. **Resource settings:** as in section 1. They are the defaults in the Helm chart (`values.yaml`).
2. **Worker replicas:** 2 for availability. Each takes one job at a time (prefetch 1). CPU-based autoscaling isn't a good signal, because imports are short bursts; if scaling is needed, scale on the queue length (e.g. KEDA with the RabbitMQ queue).
3. **RabbitMQ:** an `exec` readiness probe with `rabbitmq-diagnostics` starts an Erlang VM every time it runs. In the test it used ~0.4 CPU core continuously while idle; a TCP probe on port 5672 brought idle use down to ~10 millicores.
4. **Large journal tables:** DataHub refuses a `WIRING.TPS` larger than the `Uploads__MaxJournalBytes` setting (default 55 MB, matching the 2 GiB worker limit). The customer sees a clear message in the portal as soon as the upload completes ("too large to process automatically, please contact the bank"), and the worker checks again before reading. If larger journals are expected, raise the setting and the worker's memory limit together, using the formula above.
5. These figures come from a single-node development cluster. They should be confirmed once in the bank's test environment (the same load-test script can be used against any test installation).

## 6. Questions for the bank

1. What is the largest expected ORIS journal table (`WIRING.TPS` size or number of journal lines) per customer?
2. How many uploads per day, and how many at the same time at peak?
3. Is there a policy requiring CPU limits on pods?
4. Is 3 GiB of ephemeral storage per worker pod available on the nodes?
5. How long should uploaded ZIPs be kept in S3 (retention rule)?
